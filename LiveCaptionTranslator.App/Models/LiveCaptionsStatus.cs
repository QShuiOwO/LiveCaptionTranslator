using System.Windows.Automation;

namespace LiveCaptionTranslator.App.Models;

public enum LiveCaptionsState
{
    Unknown,
    Running,
    NotFound,
    Error
}

public sealed record LiveCaptionsStatus
{
    public LiveCaptionsState State { get; init; } = LiveCaptionsState.Unknown;

    public string Message { get; init; } = "未检测";

    public string? WindowName { get; init; }

    public string? ErrorMessage { get; init; }

    public AutomationElement? WindowElement { get; init; }

    public bool IsRunning => State == LiveCaptionsState.Running && WindowElement is not null;

    public static LiveCaptionsStatus Running(AutomationElement windowElement, string windowName)
    {
        return new LiveCaptionsStatus
        {
            State = LiveCaptionsState.Running,
            Message = $"运行中：{windowName}",
            WindowName = windowName,
            WindowElement = windowElement
        };
    }

    public static LiveCaptionsStatus NotFound()
    {
        return new LiveCaptionsStatus
        {
            State = LiveCaptionsState.NotFound,
            Message = "未找到 Live captions 窗口"
        };
    }

    public static LiveCaptionsStatus Error(Exception exception)
    {
        return new LiveCaptionsStatus
        {
            State = LiveCaptionsState.Error,
            Message = "检测失败",
            ErrorMessage = exception.Message
        };
    }
}
