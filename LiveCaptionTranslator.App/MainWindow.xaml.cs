using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveCaptionTranslator.App.Models;
using LiveCaptionTranslator.App.Services.LiveCaptions;
using LiveCaptionTranslator.App.Services.Subtitle;
using LiveCaptionTranslator.App.Services.Translation;
using LiveCaptionTranslator.App.ViewModels;

namespace LiveCaptionTranslator.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly LiveCaptionsDetector _detector;
    private readonly LiveCaptionsLauncher _launcher;
    private readonly UiaTreeDumper _uiaTreeDumper;
    private readonly LiveCaptionsReader _reader;
    private readonly SubtitleDeduplicator _deduplicator;
    private readonly PythonTranslationWorker _translationWorker;
    private readonly TranslationQueue _translationQueue;
    private readonly DispatcherTimer _deduplicationTimer;

    public MainWindow()
    {
        _detector = new LiveCaptionsDetector();
        _launcher = new LiveCaptionsLauncher(_detector);
        _uiaTreeDumper = new UiaTreeDumper(_detector);
        _reader = new LiveCaptionsReader(_detector);
        _deduplicator = new SubtitleDeduplicator();
        _translationWorker = new PythonTranslationWorker();
        _translationQueue = new TranslationQueue(_translationWorker);
        _deduplicationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };

        InitializeComponent();
        DataContext = _viewModel;

        _reader.TextChanged += Reader_TextChanged;
        _reader.StatusChanged += Reader_StatusChanged;
        _reader.LogMessage += Reader_LogMessage;
        _translationWorker.StatusChanged += Translation_StatusChanged;
        _translationWorker.LogMessage += Translation_LogMessage;
        _translationQueue.StatusChanged += Translation_StatusChanged;
        _translationQueue.LogMessage += Translation_LogMessage;
        _translationQueue.TranslationCompleted += TranslationQueue_TranslationCompleted;
        _deduplicationTimer.Tick += DeduplicationTimer_Tick;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        _viewModel.AddLog("应用已启动。");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshLiveCaptionsStatusAsync();
    }

    private async void StartLiveCaptionsButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AddLog("正在通过 Win + Ctrl + L 启动 Live captions。");

        try
        {
            var status = await _launcher.LaunchAsync();
            ApplyLiveCaptionsStatus(status);

            _viewModel.AddLog(status.IsRunning
                ? "Live captions 已启动。"
                : "启动后仍未找到 Live captions 窗口。");
        }
        catch (OperationCanceledException)
        {
            _viewModel.AddLog("启动 Live captions 已取消。");
        }
        catch (Exception ex)
        {
            _viewModel.AddLog($"启动 Live captions 失败：{ex.Message}");
        }
    }

    private async void StartReadingButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await RefreshLiveCaptionsStatusAsync();
        if (!status.IsRunning)
        {
            _viewModel.AddLog("当前未找到 Live captions 窗口，读取器会继续尝试重连。");
        }

        _deduplicator.Reset();
        _translationQueue.ClearPending();
        _viewModel.ClearSubmittedSegments();
        _viewModel.ClearTranslationResults();
        _viewModel.CurrentBufferText = "等待稳定字幕片段。";
        _reader.Start();
        _deduplicationTimer.Start();
    }

    private async void StopReadingButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyDeduplicationResult(_deduplicator.Tick());
        _deduplicationTimer.Stop();
        await _reader.StopAsync();
    }

    private async void DumpUiaTreeButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AddLog("正在导出 Live captions UIA 树。");

        try
        {
            var outputPath = await _uiaTreeDumper.DumpAsync();
            _viewModel.AddLog($"UIA 树已导出：{outputPath}");
        }
        catch (OperationCanceledException)
        {
            _viewModel.AddLog("导出 UIA 树已取消。");
        }
        catch (Exception ex)
        {
            _viewModel.AddLog($"导出 UIA 树失败：{ex.Message}");
        }
    }

    private async void StartTranslationWorkerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _translationQueue.Start();
            await _translationWorker.StartAsync();
            _viewModel.AddLog("翻译 worker 已启动。");
        }
        catch (Exception ex)
        {
            _viewModel.TranslationStatus = "翻译 worker 启动失败";
            _viewModel.AddLog($"翻译 worker 启动失败：{ex.Message}");
        }
    }

    private async void StopTranslationWorkerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _translationQueue.StopAsync();
            await _translationWorker.StopAsync();
            _viewModel.AddLog("翻译 worker 已停止。");
        }
        catch (Exception ex)
        {
            _viewModel.AddLog($"停止翻译 worker 失败：{ex.Message}");
        }
    }

    private void RunSegmentationDebugButton_Click(object sender, RoutedEventArgs e)
    {
        var report = SubtitleDeduplicatorDebugScenarios.RunAll();
        _viewModel.AddLog("分段测试结果：");
        foreach (var line in report.Split(Environment.NewLine))
        {
            _viewModel.AddLog(line);
        }
    }

    private async Task<LiveCaptionsStatus> RefreshLiveCaptionsStatusAsync()
    {
        var status = await Task.Run(() => _detector.Detect());
        ApplyLiveCaptionsStatus(status);
        return status;
    }

    private void ApplyLiveCaptionsStatus(LiveCaptionsStatus status)
    {
        _viewModel.LiveCaptionsStatus = status.Message;

        if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
        {
            _viewModel.AddLog($"Live captions 状态检测异常：{status.ErrorMessage}");
        }
    }

    private void Reader_TextChanged(object? sender, string text)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var result = _deduplicator.Push(text);
            ApplyDeduplicationResult(result);
        });
    }

    private void Reader_StatusChanged(object? sender, string status)
    {
        Dispatcher.InvokeAsync(() => _viewModel.LiveCaptionsStatus = status);
    }

    private void Reader_LogMessage(object? sender, string message)
    {
        Dispatcher.InvokeAsync(() => _viewModel.AddLog(message));
    }

    private void Translation_StatusChanged(object? sender, string status)
    {
        Dispatcher.InvokeAsync(() => _viewModel.TranslationStatus = status);
    }

    private void Translation_LogMessage(object? sender, string message)
    {
        Dispatcher.InvokeAsync(() => _viewModel.AddLog(message));
    }

    private void TranslationQueue_TranslationCompleted(object? sender, TranslationResult result)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _viewModel.AddTranslationResult(result);
            if (string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.AddLog($"翻译完成：{result.TranslatedText}");
            }
            else
            {
                _viewModel.AddLog($"翻译失败：{result.ErrorMessage}");
            }
        });
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.ScrollToEnd();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _deduplicationTimer.Stop();
        _reader.Dispose();
        _translationQueue.Dispose();
        _translationWorker.Dispose();
    }

    private void DeduplicationTimer_Tick(object? sender, EventArgs e)
    {
        ApplyDeduplicationResult(_deduplicator.Tick());
    }

    private void ApplyDeduplicationResult(SubtitleDeduplicationResult result)
    {
        _viewModel.OriginalSubtitleText = string.IsNullOrWhiteSpace(result.RealtimeText)
            ? "等待读取 Live captions 原文字幕。"
            : result.RealtimeText;

        _viewModel.CurrentBufferText = string.IsNullOrWhiteSpace(result.BufferText)
            ? "等待稳定字幕片段。"
            : result.BufferText;

        if (result.SubmittedSegments.Count == 0)
        {
            return;
        }

        _viewModel.AddSubmittedSegments(result.SubmittedSegments);
        foreach (var segment in result.SubmittedSegments)
        {
            _viewModel.AddLog($"提交字幕片段：{segment.Text}");
            _translationQueue.Enqueue(
                segment,
                _viewModel.SelectedSourceLanguage,
                _viewModel.SelectedTargetLanguage);
        }
    }
}
