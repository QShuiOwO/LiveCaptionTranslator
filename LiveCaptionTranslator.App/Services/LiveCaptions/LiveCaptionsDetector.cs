using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.LiveCaptions;

public sealed class LiveCaptionsDetector
{
    internal static readonly string[] WindowTitleFragments =
    [
        "Live captions",
        "实时字幕",
        "实时辅助字幕",
        "ライブ キャプション",
        "LiveCaptions"
    ];

    private static readonly string[] ProcessNameFragments =
    [
        "LiveCaptions"
    ];

    public LiveCaptionsStatus Detect()
    {
        try
        {
            var root = AutomationElement.RootElement;
            if (root is null)
            {
                return LiveCaptionsStatus.NotFound();
            }

            var processStatus = TryFindLiveCaptionsProcessElement(root);
            if (processStatus is not null)
            {
                return processStatus;
            }

            var nativeStatus = TryFindNativeLiveCaptionsWindow();
            if (nativeStatus is not null)
            {
                return nativeStatus;
            }

            var windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement window in windows)
            {
                var status = TryMatchWindow(window);
                if (status is not null)
                {
                    return status;
                }
            }

            return LiveCaptionsStatus.NotFound();
        }
        catch (Exception ex)
        {
            return LiveCaptionsStatus.Error(ex);
        }
    }

    private static LiveCaptionsStatus? TryFindLiveCaptionsProcessElement(AutomationElement root)
    {
        foreach (var process in Process.GetProcessesByName("LiveCaptions"))
        {
            try
            {
                var status = TryFindElementByProcessId(root, process.Id);
                if (status is not null)
                {
                    return status;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static LiveCaptionsStatus? TryFindElementByProcessId(AutomationElement root, int processId)
    {
        AutomationElementCollection elements;
        try
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            elements = root.FindAll(TreeScope.Subtree, condition);
        }
        catch
        {
            return null;
        }

        AutomationElement? bestElement = null;
        var bestScore = int.MinValue;
        foreach (AutomationElement element in elements)
        {
            var score = ScoreLiveCaptionsProcessElement(element);
            if (score > bestScore)
            {
                bestElement = element;
                bestScore = score;
            }
        }

        if (bestElement is null)
        {
            return null;
        }

        return LiveCaptionsStatus.Running(bestElement, ToDisplayName(bestElement, "LiveCaptions"));
    }

    private static int ScoreLiveCaptionsProcessElement(AutomationElement element)
    {
        var score = 0;

        try
        {
            var controlType = element.Current.ControlType;
            if (controlType == ControlType.Window)
            {
                score += 500;
            }
            else if (controlType == ControlType.Pane || controlType == ControlType.Custom || controlType == ControlType.Group)
            {
                score += 300;
            }
            else if (controlType == ControlType.Document || controlType == ControlType.Edit || controlType == ControlType.Text)
            {
                score += 180;
            }
        }
        catch
        {
        }

        var name = SafeRead(() => element.Current.Name);
        if (ContainsLiveCaptionsFragment(name))
        {
            score += 250;
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            score += Math.Min(name.Length, 80);
        }

        var className = SafeRead(() => element.Current.ClassName);
        if (ContainsLiveCaptionsFragment(className))
        {
            score += 120;
        }

        try
        {
            var bounds = element.Current.BoundingRectangle;
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                score += 100;

                if (bounds.Width >= 300)
                {
                    score += 30;
                }

                if (bounds.Height >= 30)
                {
                    score += 20;
                }
            }

            if (!element.Current.IsOffscreen)
            {
                score += 80;
            }
        }
        catch
        {
        }

        return score;
    }

    private static LiveCaptionsStatus? TryFindNativeLiveCaptionsWindow()
    {
        LiveCaptionsStatus? matchedStatus = null;
        var processIds = Process.GetProcessesByName("LiveCaptions")
            .Select(process =>
            {
                var id = process.Id;
                process.Dispose();
                return id;
            })
            .ToHashSet();

        if (processIds.Count == 0)
        {
            return null;
        }

        EnumWindows((windowHandle, _) =>
        {
            if (matchedStatus is not null)
            {
                return true;
            }

            GetWindowThreadProcessId(windowHandle, out var processId);
            if (!processIds.Contains((int)processId))
            {
                return true;
            }

            try
            {
                var element = AutomationElement.FromHandle(windowHandle);
                if (element is null)
                {
                    return true;
                }

                matchedStatus = LiveCaptionsStatus.Running(element, ToDisplayName(element, "LiveCaptions"));
            }
            catch
            {
                return true;
            }

            return matchedStatus is null;
        }, IntPtr.Zero);

        return matchedStatus;
    }

    private static LiveCaptionsStatus? TryMatchWindow(AutomationElement window)
    {
        try
        {
            var name = SafeRead(() => window.Current.Name);
            if (ContainsLiveCaptionsFragment(name))
            {
                return LiveCaptionsStatus.Running(window, ToDisplayName(name, window));
            }

            var processName = TryGetProcessName(window);
            if (ContainsProcessFragment(processName))
            {
                return LiveCaptionsStatus.Running(window, ToDisplayName(name, window, processName));
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string ToDisplayName(AutomationElement element, string? processName = null)
    {
        return ToDisplayName(SafeRead(() => element.Current.Name), element, processName);
    }

    private static string ToDisplayName(string? name, AutomationElement element, string? processName = null)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var className = SafeRead(() => element.Current.ClassName);
        if (!string.IsNullOrWhiteSpace(processName) && !string.IsNullOrWhiteSpace(className))
        {
            return $"{processName} / {className}";
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            return processName;
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            return className;
        }

        return "Live captions";
    }

    private static bool ContainsLiveCaptionsFragment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var titleFragment in WindowTitleFragments)
        {
            if (value.Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsProcessFragment(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        foreach (var processNameFragment in ProcessNameFragments)
        {
            if (processName.Contains(processNameFragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryGetProcessName(AutomationElement element)
    {
        try
        {
            var processId = element.Current.ProcessId;
            if (processId <= 0 || processId == Environment.ProcessId)
            {
                return null;
            }

            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeRead(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
