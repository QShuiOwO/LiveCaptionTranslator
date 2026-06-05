using System.Windows;
using System.Windows.Controls;
using LiveCaptionTranslator.App.Models;
using LiveCaptionTranslator.App.Services.LiveCaptions;
using LiveCaptionTranslator.App.ViewModels;

namespace LiveCaptionTranslator.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly LiveCaptionsDetector _detector;
    private readonly LiveCaptionsLauncher _launcher;
    private readonly UiaTreeDumper _uiaTreeDumper;
    private readonly LiveCaptionsReader _reader;

    public MainWindow()
    {
        _detector = new LiveCaptionsDetector();
        _launcher = new LiveCaptionsLauncher(_detector);
        _uiaTreeDumper = new UiaTreeDumper(_detector);
        _reader = new LiveCaptionsReader(_detector);

        InitializeComponent();
        DataContext = _viewModel;

        _reader.TextChanged += Reader_TextChanged;
        _reader.StatusChanged += Reader_StatusChanged;
        _reader.LogMessage += Reader_LogMessage;

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

        _reader.Start();
    }

    private async void StopReadingButton_Click(object sender, RoutedEventArgs e)
    {
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
        Dispatcher.InvokeAsync(() => _viewModel.OriginalSubtitleText = text);
    }

    private void Reader_StatusChanged(object? sender, string status)
    {
        Dispatcher.InvokeAsync(() => _viewModel.LiveCaptionsStatus = status);
    }

    private void Reader_LogMessage(object? sender, string message)
    {
        Dispatcher.InvokeAsync(() => _viewModel.AddLog(message));
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.ScrollToEnd();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _reader.Dispose();
    }
}
