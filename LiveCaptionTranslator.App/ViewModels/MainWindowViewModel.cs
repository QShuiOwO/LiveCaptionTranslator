using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _liveCaptionsStatus = "未检测";
    private string _translationStatus = "翻译 worker 未启动";
    private string _originalSubtitleText = "等待读取 Live captions 原文字幕。";
    private string _currentBufferText = "等待稳定字幕片段。";
    private string _selectedSourceLanguage = "auto";
    private string _selectedTargetLanguage = "zho_Hans";
    private string _logText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string LiveCaptionsStatus
    {
        get => _liveCaptionsStatus;
        set => SetField(ref _liveCaptionsStatus, value);
    }

    public string TranslationStatus
    {
        get => _translationStatus;
        set => SetField(ref _translationStatus, value);
    }

    public string OriginalSubtitleText
    {
        get => _originalSubtitleText;
        set => SetField(ref _originalSubtitleText, value);
    }

    public string CurrentBufferText
    {
        get => _currentBufferText;
        set => SetField(ref _currentBufferText, value);
    }

    public string SelectedSourceLanguage
    {
        get => _selectedSourceLanguage;
        set => SetField(ref _selectedSourceLanguage, value);
    }

    public string SelectedTargetLanguage
    {
        get => _selectedTargetLanguage;
        set => SetField(ref _selectedTargetLanguage, value);
    }

    public ObservableCollection<string> SourceLanguages { get; } =
    [
        "auto",
        "eng_Latn",
        "jpn_Jpan",
        "zho_Hans"
    ];

    public ObservableCollection<string> TargetLanguages { get; } =
    [
        "zho_Hans"
    ];

    public ObservableCollection<string> Logs { get; } = [];

    public ObservableCollection<CaptionSegment> SubmittedSegments { get; } = [];

    public ObservableCollection<TranslationResult> TranslationResults { get; } = [];

    public string LogText
    {
        get => _logText;
        private set => SetField(ref _logText, value);
    }

    public void AddSubmittedSegments(IEnumerable<CaptionSegment> segments)
    {
        foreach (var segment in segments)
        {
            SubmittedSegments.Add(segment);
        }
    }

    public void ClearSubmittedSegments()
    {
        SubmittedSegments.Clear();
    }

    public void AddTranslationResult(TranslationResult result)
    {
        TranslationResults.Add(result);
    }

    public void ClearTranslationResults()
    {
        TranslationResults.Clear();
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
