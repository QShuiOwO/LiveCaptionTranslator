using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveCaptionTranslator.App.ViewModels;

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private string _sourceText = string.Empty;
    private string _translatedText = "等待译文";
    private bool _showSourceText;
    private double _fontSize = 30;
    private double _backgroundOpacity = 0.45;
    private int _maxLines = 3;
    private bool _isLocked;
    private bool _isClickThrough;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceText
    {
        get => _sourceText;
        set => SetField(ref _sourceText, value);
    }

    public string TranslatedText
    {
        get => _translatedText;
        set => SetField(ref _translatedText, string.IsNullOrWhiteSpace(value) ? "等待译文" : value);
    }

    public bool ShowSourceText
    {
        get => _showSourceText;
        set => SetField(ref _showSourceText, value);
    }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (SetField(ref _fontSize, Math.Clamp(value, 16, 72)))
            {
                OnPropertyChanged(nameof(SourceFontSize));
                OnPropertyChanged(nameof(TranslatedMaxHeight));
                OnPropertyChanged(nameof(SourceMaxHeight));
            }
        }
    }

    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set => SetField(ref _backgroundOpacity, Math.Clamp(value, 0.05, 0.95));
    }

    public int MaxLines
    {
        get => _maxLines;
        set
        {
            if (SetField(ref _maxLines, Math.Clamp(value, 1, 8)))
            {
                OnPropertyChanged(nameof(TranslatedMaxHeight));
                OnPropertyChanged(nameof(SourceMaxHeight));
            }
        }
    }

    public bool IsLocked
    {
        get => _isLocked;
        set => SetField(ref _isLocked, value);
    }

    public bool IsClickThrough
    {
        get => _isClickThrough;
        set => SetField(ref _isClickThrough, value);
    }

    public double SourceFontSize => Math.Max(12, FontSize * 0.55);

    public double TranslatedMaxHeight => FontSize * 1.35 * MaxLines;

    public double SourceMaxHeight => SourceFontSize * 1.35 * Math.Max(1, MaxLines - 1);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
