using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveCaptionTranslator.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _liveCaptionsStatus = "未检测";
    private string _originalSubtitleText = "等待读取 Live captions 原文字幕。";
    private string _logText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string LiveCaptionsStatus
    {
        get => _liveCaptionsStatus;
        set => SetField(ref _liveCaptionsStatus, value);
    }

    public string OriginalSubtitleText
    {
        get => _originalSubtitleText;
        set => SetField(ref _originalSubtitleText, value);
    }

    public ObservableCollection<string> Logs { get; } = [];

    public string LogText
    {
        get => _logText;
        private set => SetField(ref _logText, value);
    }

    public void AddLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Logs.Add(line);
        LogText = string.IsNullOrEmpty(LogText)
            ? line
            : $"{LogText}{Environment.NewLine}{line}";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
