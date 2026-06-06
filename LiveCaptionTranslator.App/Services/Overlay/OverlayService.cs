using System.ComponentModel;
using System.Windows;
using LiveCaptionTranslator.App.Services.Settings;
using LiveCaptionTranslator.App.ViewModels;

namespace LiveCaptionTranslator.App.Services.Overlay;

public sealed class OverlayService
{
    private static readonly TimeSpan SettingsSaveDebounce = TimeSpan.FromMilliseconds(300);

    private readonly AppSettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly object _settingsSyncRoot = new();
    private OverlayWindow? _window;
    private CancellationTokenSource? _settingsSaveCancellationTokenSource;
    private bool _isRestoringWindow;

    public OverlayService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = _settingsService.Load();
        ViewModel = CreateViewModel(_settings.Overlay);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public OverlayViewModel ViewModel { get; }

    public OverlaySettings OverlaySettings => _settings.Overlay;

    public bool IsVisible => _window?.IsVisible == true;

    public void Show()
    {
        if (_window is null)
        {
            _window = new OverlayWindow(ViewModel);
            _window.LocationChanged += OverlayWindow_BoundsChanged;
            _window.SizeChanged += OverlayWindow_BoundsChanged;
            _window.Closed += OverlayWindow_Closed;
            RestoreWindowBounds(_window);
        }

        _window.Show();
        _window.Activate();
    }

    public void Hide()
    {
        if (_window is null)
        {
            return;
        }

        SaveWindowBounds();
        _window.Hide();
    }

    public void Close()
    {
        if (_window is null)
        {
            SaveSettingsImmediately();
            return;
        }

        SaveWindowBounds();
        SaveSettingsImmediately();
        _window.Close();
    }

    public void UpdateSubtitle(string sourceText, string translatedText)
    {
        ViewModel.SourceText = sourceText;
        ViewModel.TranslatedText = translatedText;
    }

    public void SetLocked(bool isLocked)
    {
        ViewModel.IsLocked = isLocked;
    }

    public void SetClickThrough(bool isClickThrough)
    {
        ViewModel.IsClickThrough = isClickThrough;
    }

    public void SetShowSourceText(bool showSourceText)
    {
        ViewModel.ShowSourceText = showSourceText;
    }

    public void SetFontSize(double fontSize)
    {
        ViewModel.FontSize = fontSize;
    }

    public void SetBackgroundOpacity(double opacity)
    {
        ViewModel.BackgroundOpacity = opacity;
    }

    public void SaveWindowBounds()
    {
        if (_window is null || _isRestoringWindow)
        {
            return;
        }

        lock (_settingsSyncRoot)
        {
            _settings.Overlay.Left = _window.Left;
            _settings.Overlay.Top = _window.Top;
            _settings.Overlay.Width = _window.Width;
            _settings.Overlay.Height = _window.Height;
        }

        QueueSettingsSave();
    }

    private void RestoreWindowBounds(Window window)
    {
        _isRestoringWindow = true;
        try
        {
            var overlay = _settings.Overlay;
            window.Width = Math.Clamp(overlay.Width, 320, 2400);
            window.Height = Math.Clamp(overlay.Height, 90, 900);
            window.Left = ClampToVirtualScreen(overlay.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth, window.Width);
            window.Top = ClampToVirtualScreen(overlay.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenHeight, window.Height);
        }
        finally
        {
            _isRestoringWindow = false;
        }
    }

    private static double ClampToVirtualScreen(double value, double start, double length, double windowLength)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return start + 80;
        }

        var max = start + Math.Max(0, length - windowLength);
        return Math.Clamp(value, start, max);
    }

    private static OverlayViewModel CreateViewModel(OverlaySettings settings)
    {
        return new OverlayViewModel
        {
            ShowSourceText = settings.ShowSourceText,
            FontSize = settings.FontSize,
            BackgroundOpacity = settings.BackgroundOpacity,
            MaxLines = settings.MaxLines,
            IsLocked = settings.IsLocked,
            IsClickThrough = settings.IsClickThrough
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        lock (_settingsSyncRoot)
        {
            _settings.Overlay.ShowSourceText = ViewModel.ShowSourceText;
            _settings.Overlay.FontSize = ViewModel.FontSize;
            _settings.Overlay.BackgroundOpacity = ViewModel.BackgroundOpacity;
            _settings.Overlay.MaxLines = ViewModel.MaxLines;
            _settings.Overlay.IsClickThrough = ViewModel.IsClickThrough;
            _settings.Overlay.IsLocked = ViewModel.IsLocked;
        }

        QueueSettingsSave();
    }

    private void OverlayWindow_BoundsChanged(object? sender, EventArgs e)
    {
        SaveWindowBounds();
    }

    private void OverlayWindow_Closed(object? sender, EventArgs e)
    {
        SaveWindowBounds();
        _window = null;
    }

    private void QueueSettingsSave()
    {
        CancellationTokenSource saveCancellationTokenSource;
        lock (_settingsSyncRoot)
        {
            _settingsSaveCancellationTokenSource?.Cancel();
            saveCancellationTokenSource = new CancellationTokenSource();
            _settingsSaveCancellationTokenSource = saveCancellationTokenSource;
        }

        _ = SaveSettingsAfterDelayAsync(saveCancellationTokenSource);
    }

    private async Task SaveSettingsAfterDelayAsync(CancellationTokenSource saveCancellationTokenSource)
    {
        try
        {
            await Task.Delay(SettingsSaveDebounce, saveCancellationTokenSource.Token).ConfigureAwait(false);

            lock (_settingsSyncRoot)
            {
                if (!ReferenceEquals(_settingsSaveCancellationTokenSource, saveCancellationTokenSource))
                {
                    return;
                }

                _settingsSaveCancellationTokenSource = null;
                _settingsService.Save(_settings);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            saveCancellationTokenSource.Dispose();
        }
    }

    private void SaveSettingsImmediately()
    {
        lock (_settingsSyncRoot)
        {
            _settingsSaveCancellationTokenSource?.Cancel();
            _settingsSaveCancellationTokenSource = null;
            _settingsService.Save(_settings);
        }
    }
}
