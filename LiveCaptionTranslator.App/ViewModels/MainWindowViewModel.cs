using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LiveCaptionTranslator.App.Models;
using LiveCaptionTranslator.App.Services.Subtitle;

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

    public void ApplySubmittedSegments(
        IEnumerable<CaptionSegment> submittedSegments,
        IEnumerable<CaptionSegment> replacedSegments)
    {
        foreach (var replacedSegment in replacedSegments)
        {
            var existing = SubmittedSegments.FirstOrDefault(segment => segment.Id == replacedSegment.Id);
            if (existing is not null)
            {
                SubmittedSegments.Remove(existing);
            }
        }

        foreach (var segment in submittedSegments)
        {
            SubmittedSegments.Add(segment);
        }
    }

    public void ClearSubmittedSegments()
    {
        SubmittedSegments.Clear();
    }

    public bool UpsertTranslationResult(TranslationResult result)
    {
        var existing = TranslationResults.FirstOrDefault(item =>
            SubtitleDeduplicator.AreSameUtterance(item.SourceText, result.SourceText) &&
            SubtitleDeduplicator.IsMoreCompleteUtterance(result.SourceText, item.SourceText));

        if (existing is not null)
        {
            var index = TranslationResults.IndexOf(existing);
            TranslationResults[index] = result;
            return true;
        }

        if (TranslationResults.Any(item =>
                string.Equals(item.SourceText, result.SourceText, StringComparison.Ordinal) &&
                string.Equals(item.TargetLanguage, result.TargetLanguage, StringComparison.Ordinal)))
        {
            return false;
        }

        TranslationResults.Add(result);
        return false;
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
