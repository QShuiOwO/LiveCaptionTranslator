using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.Translation;

public sealed class TranslationQueue : IDisposable
{
    private readonly ITranslationService _translationService;
    private readonly object _syncRoot = new();
    private readonly Queue<TranslationWorkItem> _queue = new();
    private readonly Dictionary<string, TranslationResult> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerTask;

    public TranslationQueue(ITranslationService translationService)
    {
        _translationService = translationService;
    }

    public event EventHandler<TranslationResult>? TranslationCompleted;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<string>? LogMessage;

    public int MaxQueueLength { get; init; } = 10;

    public TimeSpan TranslationTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public bool IsRunning => _workerTask is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        _workerTask = Task.Run(() => ProcessLoopAsync(_cancellationTokenSource.Token));
        PublishStatus("翻译队列运行中");
    }

    public async Task StopAsync()
    {
        if (_cancellationTokenSource is null)
        {
            PublishStatus("翻译队列已停止");
            return;
        }

        _cancellationTokenSource.Cancel();
        _signal.Release();

        try
        {
            if (_workerTask is not null)
            {
                await _workerTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _workerTask = null;
            PublishStatus("翻译队列已停止");
        }
    }

    public void Enqueue(CaptionSegment segment, string sourceLanguage, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(segment.Text))
        {
            return;
        }

        var cacheKey = CreateCacheKey(segment.Text, sourceLanguage, targetLanguage);
        if (_cache.TryGetValue(cacheKey, out var cachedResult))
        {
            TranslationCompleted?.Invoke(this, cachedResult);
            PublishStatus("命中翻译缓存");
            return;
        }

        lock (_syncRoot)
        {
            if (_queue.Count >= MaxQueueLength)
            {
                DropOldestShortPendingItem();
            }

            _queue.Enqueue(new TranslationWorkItem(segment, sourceLanguage, targetLanguage));
            PublishStatus(IsRunning
                ? $"翻译队列等待中：{_queue.Count}"
                : $"翻译队列已入队：{_queue.Count}，等待启动 worker");
        }

        _signal.Release();
    }

    public void ClearPending()
    {
        lock (_syncRoot)
        {
            _queue.Clear();
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _signal.Dispose();
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            TranslationWorkItem? item;
            lock (_syncRoot)
            {
                item = _queue.Count > 0 ? _queue.Dequeue() : null;
            }

            if (item is null)
            {
                continue;
            }

            var cacheKey = CreateCacheKey(item.Segment.Text, item.SourceLanguage, item.TargetLanguage);
            if (_cache.TryGetValue(cacheKey, out var cachedResult))
            {
                TranslationCompleted?.Invoke(this, cachedResult);
                continue;
            }

            PublishStatus("正在翻译");

            TranslationResult result;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TranslationTimeout);

            try
            {
                result = await _translationService.TranslateAsync(
                    item.Segment.Text,
                    item.SourceLanguage,
                    item.TargetLanguage,
                    timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = CreateTimeoutResult(item);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = CreateFailureResult(item, ex.Message);
            }

            if (string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _cache[cacheKey] = result;
            }

            TranslationCompleted?.Invoke(this, result);
            PublishStatus(GetQueueStatus());
        }
    }

    private void DropOldestShortPendingItem()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        var items = _queue.ToList();
        var dropIndex = items.FindIndex(item => item.Segment.Text.Length <= 20);
        if (dropIndex < 0)
        {
            dropIndex = 0;
        }

        var dropped = items[dropIndex];
        items.RemoveAt(dropIndex);

        _queue.Clear();
        foreach (var item in items)
        {
            _queue.Enqueue(item);
        }

        PublishLog($"翻译队列已满，丢弃旧片段：{dropped.Segment.Text}");
    }

    private string GetQueueStatus()
    {
        lock (_syncRoot)
        {
            return _queue.Count == 0 ? "翻译队列空闲" : $"翻译队列等待中：{_queue.Count}";
        }
    }

    private static string CreateCacheKey(string text, string sourceLanguage, string targetLanguage)
    {
        return $"{sourceLanguage}\u001f{targetLanguage}\u001f{text}";
    }

    private static TranslationResult CreateTimeoutResult(TranslationWorkItem item)
    {
        return new TranslationResult
        {
            SourceText = item.Segment.Text,
            SourceLanguage = item.SourceLanguage,
            TargetLanguage = item.TargetLanguage,
            CreatedAt = DateTimeOffset.Now,
            CompletedAt = DateTimeOffset.Now,
            Status = "Timeout",
            ErrorMessage = "单句翻译超时。"
        };
    }

    private static TranslationResult CreateFailureResult(TranslationWorkItem item, string message)
    {
        return new TranslationResult
        {
            SourceText = item.Segment.Text,
            SourceLanguage = item.SourceLanguage,
            TargetLanguage = item.TargetLanguage,
            CreatedAt = DateTimeOffset.Now,
            CompletedAt = DateTimeOffset.Now,
            Status = "Failed",
            ErrorMessage = message
        };
    }

    private void PublishStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void PublishLog(string message)
    {
        LogMessage?.Invoke(this, message);
    }

    private sealed record TranslationWorkItem(
        CaptionSegment Segment,
        string SourceLanguage,
        string TargetLanguage);
}
