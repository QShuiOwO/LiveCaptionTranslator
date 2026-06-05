using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.Translation;

public sealed class PythonTranslationWorker : ITranslationService, IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);
    private const string PythonPathEnvironmentVariable = "LCT_NLLB_PYTHON";
    private const string PreferredCondaPythonPath = @"D:\miniconda3\envs\local-translator-nllb\python.exe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _startStopLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private Process? _process;
    private CancellationTokenSource? _processCancellationTokenSource;
    private TaskCompletionSource<WorkerReadyResult>? _readyCompletionSource;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private bool _isStopping;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<string>? LogMessage;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startStopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                PublishStatus("翻译 worker 运行中");
                return;
            }

            FailPendingRequests("翻译 worker 已重启。");

            var scriptPath = FindWorkerScriptPath();
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException("未找到 Python translation worker。", scriptPath);
            }

            var startErrors = new List<string>();
            foreach (var candidate in CreatePythonStartCandidates(scriptPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    PublishStatus("正在加载翻译 worker");
                    StartProcess(candidate.FileName, candidate.Arguments, candidate.WorkingDirectory);
                    var readyResult = await WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
                    if (!readyResult.Success)
                    {
                        await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);
                        throw new InvalidOperationException(readyResult.Error ?? "worker 初始化失败。");
                    }

                    PublishStatus("翻译 worker 运行中");
                    PublishLog($"翻译 worker 已启动：{Path.GetFileName(scriptPath)}");
                    return;
                }
                catch (Exception ex)
                {
                    startErrors.Add($"{candidate.FileName}: {ex.Message}");
                }
            }

            throw new InvalidOperationException($"无法启动 Python worker：{string.Join("; ", startErrors)}");
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _startStopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopProcessAsync(cancellationToken).ConfigureAwait(false);
            PublishStatus("翻译 worker 已停止");
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var resultId = Guid.NewGuid();
        var createdAt = DateTimeOffset.Now;

        if (string.IsNullOrWhiteSpace(text))
        {
            return CreateFailureResult(resultId, text, sourceLanguage, targetLanguage, createdAt, "原文为空。");
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);

        var process = _process;
        if (process is null || process.HasExited)
        {
            return CreateFailureResult(resultId, text, sourceLanguage, targetLanguage, createdAt, "翻译 worker 未运行。");
        }

        var requestId = resultId.ToString("N");
        var pending = new PendingRequest(resultId, text, sourceLanguage, targetLanguage, createdAt);
        if (!_pendingRequests.TryAdd(requestId, pending))
        {
            return CreateFailureResult(resultId, text, sourceLanguage, targetLanguage, createdAt, "请求 ID 冲突。");
        }

        try
        {
            var request = new WorkerRequest(requestId, text, sourceLanguage, targetLanguage);
            var json = JsonSerializer.Serialize(request, JsonOptions);

            await process.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);

            return await pending.TaskCompletionSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
        catch (Exception ex)
        {
            _pendingRequests.TryRemove(requestId, out _);
            return CreateFailureResult(resultId, text, sourceLanguage, targetLanguage, createdAt, ex.Message);
        }
    }

    public void Dispose()
    {
        _processCancellationTokenSource?.Cancel();
        _processCancellationTokenSource?.Dispose();

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        FailPendingRequests("翻译 worker 已释放。");
        _process?.Dispose();
        _startStopLock.Dispose();
    }

    private void StartProcess(string fileName, string arguments, string workingDirectory)
    {
        _processCancellationTokenSource?.Cancel();
        _processCancellationTokenSource?.Dispose();
        _processCancellationTokenSource = new CancellationTokenSource();
        _readyCompletionSource = new TaskCompletionSource<WorkerReadyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom
            },
            EnableRaisingEvents = true
        };

        process.Exited += Worker_Exited;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Process.Start returned false.");
        }

        _process = process;
        _stdoutTask = Task.Run(() => ReadStdoutLoopAsync(process, _processCancellationTokenSource.Token));
        _stderrTask = Task.Run(() => ReadStderrLoopAsync(process, _processCancellationTokenSource.Token));
    }

    private async Task<WorkerReadyResult> WaitForReadyAsync(CancellationToken cancellationToken)
    {
        var readyCompletionSource = _readyCompletionSource
            ?? throw new InvalidOperationException("worker ready state was not initialized.");

        using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellationTokenSource.CancelAfter(ReadyTimeout);

        try
        {
            return await readyCompletionSource.Task
                .WaitAsync(timeoutCancellationTokenSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WorkerReadyResult(false, "等待 worker ready 超时。");
        }
    }

    private async Task StopProcessAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;

        try
        {
            var process = _process;
            _process = null;

            _processCancellationTokenSource?.Cancel();
            FailPendingRequests("翻译 worker 已停止。");

            if (process is null)
            {
                return;
            }

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }

            try
            {
                if (!process.HasExited)
                {
                    var exitedTask = process.WaitForExitAsync(cancellationToken);
                    var completed = await Task.WhenAny(exitedTask, Task.Delay(1000, cancellationToken)).ConfigureAwait(false);
                    if (completed != exitedTask && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                process.Dispose();
            }
        }
        finally
        {
            _isStopping = false;
        }
    }

    private async Task ReadStdoutLoopAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                HandleWorkerResponse(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PublishLog($"读取 worker stdout 失败：{ex.Message}");
        }
    }

    private async Task ReadStderrLoopAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    PublishLog($"worker stderr：{line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PublishLog($"读取 worker stderr 失败：{ex.Message}");
        }
    }

    private void HandleWorkerResponse(string line)
    {
        WorkerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<WorkerResponse>(line, JsonOptions);
        }
        catch (Exception ex)
        {
            PublishLog($"worker 返回了无效 JSON：{ex.Message}");
            return;
        }

        if (response is null)
        {
            PublishLog("worker 返回空响应。");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Id))
        {
            if (string.Equals(response.Type, "ready", StringComparison.OrdinalIgnoreCase))
            {
                _readyCompletionSource?.TrySetResult(new WorkerReadyResult(
                    response.Success,
                    response.Error));
            }
            else
            {
                PublishLog("worker 返回缺少 id。");
            }

            return;
        }

        if (!_pendingRequests.TryRemove(response.Id, out var pending))
        {
            return;
        }

        var result = response.Success
            ? new TranslationResult
            {
                Id = pending.Id,
                SourceText = pending.Text,
                TranslatedText = response.TranslatedText ?? string.Empty,
                SourceLanguage = pending.SourceLanguage,
                TargetLanguage = pending.TargetLanguage,
                CreatedAt = pending.CreatedAt,
                CompletedAt = DateTimeOffset.Now,
                Status = "Completed"
            }
            : CreateFailureResult(
                pending.Id,
                pending.Text,
                pending.SourceLanguage,
                pending.TargetLanguage,
                pending.CreatedAt,
                response.Error ?? "worker 返回失败。");

        pending.TaskCompletionSource.TrySetResult(result);
    }

    private void Worker_Exited(object? sender, EventArgs e)
    {
        if (_isStopping)
        {
            return;
        }

        _process = null;
        _processCancellationTokenSource?.Cancel();
        _readyCompletionSource?.TrySetResult(new WorkerReadyResult(false, "翻译 worker 异常退出。"));
        FailPendingRequests("翻译 worker 异常退出。");
        PublishStatus("翻译 worker 异常退出");
        PublishLog("翻译 worker 异常退出。");
    }

    private void FailPendingRequests(string message)
    {
        foreach (var pair in _pendingRequests.ToArray())
        {
            if (_pendingRequests.TryRemove(pair.Key, out var pending))
            {
                pending.TaskCompletionSource.TrySetResult(CreateFailureResult(
                    pending.Id,
                    pending.Text,
                    pending.SourceLanguage,
                    pending.TargetLanguage,
                    pending.CreatedAt,
                    message));
            }
        }
    }

    private static TranslationResult CreateFailureResult(
        Guid id,
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        DateTimeOffset createdAt,
        string errorMessage)
    {
        return new TranslationResult
        {
            Id = id,
            SourceText = sourceText,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.Now,
            Status = "Failed",
            ErrorMessage = errorMessage
        };
    }

    private static IEnumerable<(string FileName, string Arguments, string WorkingDirectory)> CreatePythonStartCandidates(string scriptPath)
    {
        var quotedScript = Quote(scriptPath);
        var nllbDirectory = Path.GetDirectoryName(scriptPath)
            ?? throw new DirectoryNotFoundException("无法定位 NLLB worker 目录。");
        var modelPath = Path.Combine(nllbDirectory, "nllb-200-distilled-600M-ct2");
        var tokenizerPath = Path.Combine(nllbDirectory, "tokenizer");

        if (!Directory.Exists(modelPath))
        {
            throw new DirectoryNotFoundException($"未找到 NLLB 模型目录：{modelPath}");
        }

        if (!Directory.Exists(tokenizerPath))
        {
            throw new DirectoryNotFoundException($"未找到 NLLB tokenizer 目录：{tokenizerPath}");
        }

        var arguments =
            $"-u {quotedScript} --model {Quote(modelPath)} --tokenizer {Quote(tokenizerPath)} --device cpu --compute-type int8";

        var configuredPythonPath = Environment.GetEnvironmentVariable(PythonPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPythonPath) && File.Exists(configuredPythonPath))
        {
            yield return (configuredPythonPath, arguments, nllbDirectory);
        }

        if (!string.Equals(configuredPythonPath, PreferredCondaPythonPath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(PreferredCondaPythonPath))
        {
            yield return (PreferredCondaPythonPath, arguments, nllbDirectory);
        }

        yield return ("python", arguments, nllbDirectory);
        yield return ("py", $"-3 {arguments}", nllbDirectory);
    }

    private static string FindWorkerScriptPath()
    {
        var candidates = new List<string>
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var startDirectory in candidates)
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "tools", "nllb", "translate_worker.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "tools", "nllb", "translate_worker.py");
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private void PublishStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void PublishLog(string message)
    {
        LogMessage?.Invoke(this, message);
    }

    private sealed class WorkerRequest
    {
        public WorkerRequest(string id, string text, string sourceLang, string targetLang)
        {
            Id = id;
            Text = text;
            SourceLang = sourceLang;
            TargetLang = targetLang;
        }

        [JsonPropertyName("id")]
        public string Id { get; }

        [JsonPropertyName("text")]
        public string Text { get; }

        [JsonPropertyName("source_lang")]
        public string SourceLang { get; }

        [JsonPropertyName("target_lang")]
        public string TargetLang { get; }

        [JsonPropertyName("source_language")]
        public string SourceLanguage => SourceLang;

        [JsonPropertyName("target_language")]
        public string TargetLanguage => TargetLang;
    }

    private sealed class WorkerResponse
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("translated_text")]
        public string? TranslatedText { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed record WorkerReadyResult(bool Success, string? Error);

    private sealed record PendingRequest(
        Guid Id,
        string Text,
        string SourceLanguage,
        string TargetLanguage,
        DateTimeOffset CreatedAt)
    {
        public TaskCompletionSource<TranslationResult> TaskCompletionSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
