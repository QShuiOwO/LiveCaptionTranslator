using System.IO;
using System.Text.Json;

namespace LiveCaptionTranslator.App.Services.Settings;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveCaptionTranslator");
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}

public sealed class AppSettings
{
    public OverlaySettings Overlay { get; set; } = new();
}

public sealed class OverlaySettings
{
    public double Left { get; set; } = 120;

    public double Top { get; set; } = 120;

    public double Width { get; set; } = 900;

    public double Height { get; set; } = 180;

    public double FontSize { get; set; } = 30;

    public double BackgroundOpacity { get; set; } = 0.45;

    public int MaxLines { get; set; } = 3;

    public bool ShowSourceText { get; set; }

    public bool IsClickThrough { get; set; }

    public bool IsLocked { get; set; }
}
