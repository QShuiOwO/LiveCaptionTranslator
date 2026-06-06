using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveCaptionTranslator.App.Models;
using LiveCaptionTranslator.App.Services.LiveCaptions;
using LiveCaptionTranslator.App.Services.Overlay;
using LiveCaptionTranslator.App.Services.Settings;
using LiveCaptionTranslator.App.Services.Subtitle;
using LiveCaptionTranslator.App.Services.Translation;
using LiveCaptionTranslator.App.ViewModels;

namespace LiveCaptionTranslator.App;

public partial class MainWindow : Window
{
    private const int MaxDisplayedRealtimeTextLength = 5000;
    private const int MaxDisplayedBufferTextLength = 1500;
    private const int DedupDecisionLogIntervalMs = 1500;

    private readonly MainWindowViewModel _viewModel = new();
    private readonly LiveCaptionsDetector _detector;
    private readonly LiveCaptionsLauncher _launcher;
    private readonly UiaTreeDumper _uiaTreeDumper;
    private readonly LiveCaptionsReader _reader;
    private readonly SubtitleDeduplicator _deduplicator;
    private readonly AppSettingsService _settingsService;
    private readonly OverlayService _overlayService;
    private readonly PythonTranslationWorker _translationWorker;
    private readonly TranslationQueue _translationQueue;
    private readonly DispatcherTimer _deduplicationTimer;
    private readonly object _readerTextSyncRoot = new();
    private string? _latestReaderText;
    private DateTimeOffset _lastDedupDecisionLogAt = DateTimeOffset.MinValue;
    private int _suppressedDedupDecisionLogCount;
    private bool _isOverlayUiReady;

    public MainWindow()
    {
        _detector = new LiveCaptionsDetector();
        _launcher = new LiveCaptionsLauncher(_detector);
        _uiaTreeDumper = new UiaTreeDumper(_detector);
        _reader = new LiveCaptionsReader(_detector);
        _deduplicator = new SubtitleDeduplicator();
        _settingsService = new AppSettingsService();
        _overlayService = new OverlayService(_settingsService);
        _translationWorker = new PythonTranslationWorker();
        _translationQueue = new TranslationQueue(_translationWorker);
        _deduplicationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };

        InitializeComponent();
        ApplyOverlaySettingsToMainViewModel();
        DataContext = _viewModel;
        _isOverlayUiReady = true;

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
        lock (_readerTextSyncRoot)
        {
            _latestReaderText = null;
        }

        _viewModel.ClearSubmittedSegments();
        _viewModel.ClearTranslationResults();
        _viewModel.CurrentBufferText = "等待稳定字幕片段。";
        _reader.Start();
        _deduplicationTimer.Start();
    }

    private async void StopReadingButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyLatestReaderText();
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
            await _translationQueue.StopAsync();
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

    private void ShowOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        _overlayService.Show();
        UpdateOverlayFromLatestCompletedTranslation();
        _viewModel.OverlayStatus = "Overlay 已显示";
        _viewModel.AddLog("Overlay 已显示。");
    }

    private void HideOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        _overlayService.Hide();
        _viewModel.OverlayStatus = "Overlay 已隐藏";
        _viewModel.AddLog("Overlay 已隐藏。");
    }

    private void OverlayLockedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isOverlayUiReady)
        {
            return;
        }

        var isLocked = (sender as CheckBox)?.IsChecked == true;
        _overlayService.SetLocked(isLocked);
        _viewModel.OverlayIsLocked = isLocked;
    }

    private void OverlayClickThroughCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isOverlayUiReady)
        {
            return;
        }

        var isClickThrough = (sender as CheckBox)?.IsChecked == true;
        _overlayService.SetClickThrough(isClickThrough);
        _viewModel.OverlayIsClickThrough = isClickThrough;
    }

    private void OverlayShowSourceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isOverlayUiReady)
        {
            return;
        }

        var showSourceText = (sender as CheckBox)?.IsChecked == true;
        _overlayService.SetShowSourceText(showSourceText);
        _viewModel.OverlayShowSourceText = showSourceText;
    }

    private void OverlayFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isOverlayUiReady)
        {
            return;
        }

        _overlayService.SetFontSize(e.NewValue);
        _viewModel.OverlayFontSize = e.NewValue;
    }

    private void OverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isOverlayUiReady)
        {
            return;
        }

        _overlayService.SetBackgroundOpacity(e.NewValue);
        _viewModel.OverlayBackgroundOpacity = e.NewValue;
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
        lock (_readerTextSyncRoot)
        {
            _latestReaderText = text;
        }
    }

    private void Reader_StatusChanged(object? sender, string status)
    {
        Dispatcher.InvokeAsync(() => _viewModel.LiveCaptionsStatus = status, DispatcherPriority.Background);
    }

    private void Reader_LogMessage(object? sender, string message)
    {
        Dispatcher.InvokeAsync(() => _viewModel.AddLog(message), DispatcherPriority.Background);
    }

    private void Translation_StatusChanged(object? sender, string status)
    {
        Dispatcher.InvokeAsync(() => _viewModel.TranslationStatus = status, DispatcherPriority.Background);
    }

    private void Translation_LogMessage(object? sender, string message)
    {
        Dispatcher.InvokeAsync(() => _viewModel.AddLog(message), DispatcherPriority.Background);
    }

    private void TranslationQueue_TranslationCompleted(object? sender, TranslationResult result)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var replacedExisting = _viewModel.UpsertTranslationResult(result);
            ScrollListBoxToEnd(TranslationResultsListBox);
            if (string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _overlayService.UpdateSubtitle(result.SourceText, result.TranslatedText);
                _viewModel.AddLog(replacedExisting
                    ? $"翻译完成并替换旧结果：{result.TranslatedText}"
                    : $"翻译完成：{result.TranslatedText}");
            }
            else
            {
                _viewModel.AddLog($"翻译失败：{result.ErrorMessage}");
            }
        }, DispatcherPriority.Background);
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
        _overlayService.Close();
    }

    private void DeduplicationTimer_Tick(object? sender, EventArgs e)
    {
        ApplyLatestReaderText();
        ApplyDeduplicationResult(_deduplicator.Tick());
    }

    private void ApplyDeduplicationResult(SubtitleDeduplicationResult result)
    {
        _viewModel.OriginalSubtitleText = string.IsNullOrWhiteSpace(result.RealtimeText)
            ? "等待读取 Live captions 原文字幕。"
            : TrimDisplayText(result.RealtimeText, MaxDisplayedRealtimeTextLength);

        _viewModel.CurrentBufferText = string.IsNullOrWhiteSpace(result.BufferText)
            ? "等待稳定字幕片段。"
            : TrimDisplayText(result.BufferText, MaxDisplayedBufferTextLength);

        foreach (var logEntry in result.LogEntries)
        {
            AddDeduplicationLog(logEntry);
        }

        foreach (var replacedSegment in result.ReplacedSegments)
        {
            var replacementText = result.SubmittedSegments
                .FirstOrDefault(segment =>
                    SubtitleDeduplicator.AreSameUtterance(replacedSegment.Text, segment.Text) &&
                    SubtitleDeduplicator.IsMoreCompleteUtterance(segment.Text, replacedSegment.Text))
                ?.Text ?? result.BufferText;

            _translationQueue.Supersede(
                replacedSegment,
                replacementText,
                _viewModel.SelectedSourceLanguage,
                _viewModel.SelectedTargetLanguage);
        }

        if (result.SubmittedSegments.Count == 0)
        {
            if (result.ReplacedSegments.Count > 0)
            {
                _viewModel.ApplySubmittedSegments([], result.ReplacedSegments);
            }

            return;
        }

        _viewModel.ApplySubmittedSegments(result.SubmittedSegments, result.ReplacedSegments);
        ScrollListBoxToEnd(SubmittedSegmentsListBox);
        foreach (var segment in result.SubmittedSegments)
        {
            _viewModel.AddLog($"提交字幕片段：{segment.Text}");
            _translationQueue.Enqueue(
                segment,
                _viewModel.SelectedSourceLanguage,
                _viewModel.SelectedTargetLanguage);
        }
    }

    private static void ScrollListBoxToEnd(ListBox listBox)
    {
        if (listBox.Items.Count == 0)
        {
            return;
        }

        listBox.ScrollIntoView(listBox.Items[listBox.Items.Count - 1]);
    }

    private void ApplyLatestReaderText()
    {
        string? latestText;
        lock (_readerTextSyncRoot)
        {
            latestText = _latestReaderText;
            _latestReaderText = null;
        }

        if (latestText is null)
        {
            return;
        }

        ApplyDeduplicationResult(_deduplicator.Push(latestText));
    }

    private void AddDeduplicationLog(string logEntry)
    {
        if (!logEntry.StartsWith("Dedup判断", StringComparison.Ordinal))
        {
            _viewModel.AddLog(logEntry);
            return;
        }

        _suppressedDedupDecisionLogCount++;
        var now = DateTimeOffset.Now;
        if (now - _lastDedupDecisionLogAt < TimeSpan.FromMilliseconds(DedupDecisionLogIntervalMs))
        {
            return;
        }

        var prefix = _suppressedDedupDecisionLogCount > 1
            ? $"Dedup判断已合并 {_suppressedDedupDecisionLogCount} 次，最近一次："
            : string.Empty;

        _viewModel.AddLog($"{prefix}{logEntry}");
        _suppressedDedupDecisionLogCount = 0;
        _lastDedupDecisionLogAt = now;
    }

    private static string TrimDisplayText(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return $"...{text[^maxLength..]}";
    }

    private void ApplyOverlaySettingsToMainViewModel()
    {
        var overlay = _overlayService.ViewModel;
        _viewModel.OverlayShowSourceText = overlay.ShowSourceText;
        _viewModel.OverlayIsLocked = overlay.IsLocked;
        _viewModel.OverlayIsClickThrough = overlay.IsClickThrough;
        _viewModel.OverlayFontSize = overlay.FontSize;
        _viewModel.OverlayBackgroundOpacity = overlay.BackgroundOpacity;
    }

    private void UpdateOverlayFromLatestCompletedTranslation()
    {
        var latestCompletedResult = _viewModel.TranslationResults
            .LastOrDefault(result => string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase));

        if (latestCompletedResult is null)
        {
            return;
        }

        _overlayService.UpdateSubtitle(latestCompletedResult.SourceText, latestCompletedResult.TranslatedText);
    }
}
