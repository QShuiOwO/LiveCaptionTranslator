using System.Windows.Automation;
using Rect = System.Windows.Rect;

namespace LiveCaptionTranslator.App.Services.LiveCaptions;

public sealed class LiveCaptionsReader : IDisposable
{
    private static readonly string[] WindowTitleFragments =
    [
        "Live captions",
        "实时字幕",
        "实时辅助字幕",
        "ライブ キャプション"
    ];

    private readonly LiveCaptionsDetector _detector;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _readingTask;
    private AutomationElement? _windowElement;
    private AutomationElement? _subtitleElement;
    private string _lastStatus = string.Empty;
    private string _lastText = string.Empty;
    private int _readTicks;

    public LiveCaptionsReader(LiveCaptionsDetector detector)
    {
        _detector = detector;
    }

    public event EventHandler<string>? TextChanged;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<string>? LogMessage;

    public bool IsRunning => _readingTask is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        _lastText = string.Empty;
        _readTicks = 0;
        _readingTask = Task.Run(() => ReadLoopAsync(_cancellationTokenSource.Token));
        PublishLog("Live captions 读取已启动。");
    }

    public async Task StopAsync()
    {
        if (_cancellationTokenSource is null)
        {
            return;
        }

        _cancellationTokenSource.Cancel();

        try
        {
            if (_readingTask is not null)
            {
                await _readingTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _readingTask = null;
            _windowElement = null;
            _subtitleElement = null;
            PublishStatus("已停止");
            PublishLog("Live captions 读取已停止。");
        }
    }

    public void Dispose()
    {
        if (_cancellationTokenSource is null)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                EnsureConnected();

                if (_windowElement is not null)
                {
                    var text = TryReadSubtitleText(_windowElement);
                    if (text is not null && !string.Equals(text, _lastText, StringComparison.Ordinal))
                    {
                        _lastText = text;
                        TextChanged?.Invoke(this, text);
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                ResetConnection("Live captions 窗口已丢失，正在重连。");
            }
            catch (Exception ex)
            {
                PublishLog($"读取失败：{ex.Message}");
                ResetConnection("读取失败，正在重连。");
            }

            await Task.Delay(200, cancellationToken);
        }
    }

    private void EnsureConnected()
    {
        if (_windowElement is not null)
        {
            try
            {
                _ = _windowElement.Current.Name;
                return;
            }
            catch (ElementNotAvailableException)
            {
                ResetConnection("Live captions 窗口已丢失，正在重连。");
            }
        }

        var status = _detector.Detect();
        PublishStatus(status.Message);

        if (status.WindowElement is null)
        {
            return;
        }

        _windowElement = status.WindowElement;
        _subtitleElement = null;
        PublishLog($"已连接 Live captions 窗口：{status.WindowName}");
    }

    private string? TryReadSubtitleText(AutomationElement windowElement)
    {
        _readTicks++;
        var shouldRescan = _subtitleElement is null || _readTicks % 5 == 0;

        if (!shouldRescan && _subtitleElement is not null)
        {
            var cachedText = NormalizeText(ReadElementText(_subtitleElement));
            if (IsCandidateText(cachedText))
            {
                return cachedText;
            }

            _subtitleElement = null;
        }

        var candidate = FindBestSubtitleCandidate(windowElement);
        if (candidate is null)
        {
            return null;
        }

        _subtitleElement = candidate.Element;
        return candidate.Text;
    }

    private static SubtitleCandidate? FindBestSubtitleCandidate(AutomationElement windowElement)
    {
        var rootCandidate = TryCreateCandidate(windowElement);
        SubtitleCandidate? bestCandidate = rootCandidate;

        AutomationElementCollection elements;
        try
        {
            elements = windowElement.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        }
        catch
        {
            return bestCandidate;
        }

        foreach (AutomationElement element in elements)
        {
            var candidate = TryCreateCandidate(element);
            if (candidate is null)
            {
                continue;
            }

            if (bestCandidate is null || candidate.Score > bestCandidate.Score)
            {
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }

    private static SubtitleCandidate? TryCreateCandidate(AutomationElement element)
    {
        try
        {
            if (element.Current.IsOffscreen)
            {
                return null;
            }

            var text = NormalizeText(ReadElementText(element));
            if (!IsCandidateText(text))
            {
                return null;
            }

            var controlType = element.Current.ControlType;
            if (controlType == ControlType.Button ||
                controlType == ControlType.MenuItem ||
                controlType == ControlType.CheckBox ||
                controlType == ControlType.RadioButton)
            {
                return null;
            }

            var score = ScoreCandidate(element, text, controlType);
            return new SubtitleCandidate(element, text, score);
        }
        catch
        {
            return null;
        }
    }

    private static int ScoreCandidate(AutomationElement element, string text, ControlType controlType)
    {
        var score = Math.Min(text.Length, 120);

        if (SupportsPattern(element, TextPattern.Pattern))
        {
            score += 150;
        }

        if (controlType == ControlType.Text)
        {
            score += 80;
        }
        else if (controlType == ControlType.Document || controlType == ControlType.Edit)
        {
            score += 70;
        }
        else if (controlType == ControlType.Pane || controlType == ControlType.Custom)
        {
            score += 20;
        }

        var bounds = SafeReadRect(element);
        if (!bounds.IsEmpty)
        {
            if (bounds.Width >= 200)
            {
                score += 20;
            }

            if (bounds.Height >= 20)
            {
                score += 10;
            }
        }

        if (text.Contains('\n', StringComparison.Ordinal))
        {
            score += 20;
        }

        return score;
    }

    private static string ReadElementText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                var text = textPattern.DocumentRange.GetText(-1);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch
        {
        }

        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeText(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static bool IsCandidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var titleFragment in WindowTitleFragments)
        {
            if (string.Equals(text, titleFragment, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void ResetConnection(string message)
    {
        _windowElement = null;
        _subtitleElement = null;
        PublishStatus(message);
    }

    private void PublishStatus(string status)
    {
        if (string.Equals(status, _lastStatus, StringComparison.Ordinal))
        {
            return;
        }

        _lastStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    private void PublishLog(string message)
    {
        LogMessage?.Invoke(this, message);
    }

    private static bool SupportsPattern(AutomationElement element, AutomationPattern pattern)
    {
        try
        {
            return element.TryGetCurrentPattern(pattern, out _);
        }
        catch
        {
            return false;
        }
    }

    private static Rect SafeReadRect(AutomationElement element)
    {
        try
        {
            return element.Current.BoundingRectangle;
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private sealed record SubtitleCandidate(AutomationElement Element, string Text, int Score);
}
