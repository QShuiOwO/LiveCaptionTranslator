using System.Runtime.InteropServices;
using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.LiveCaptions;

public sealed class LiveCaptionsLauncher
{
    private const ushort VirtualKeyLeftWindows = 0x5B;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyL = 0x4C;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    private readonly LiveCaptionsDetector _detector;

    public LiveCaptionsLauncher(LiveCaptionsDetector detector)
    {
        _detector = detector;
    }

    public async Task<LiveCaptionsStatus> LaunchAsync(CancellationToken cancellationToken = default)
    {
        var currentStatus = _detector.Detect();
        if (currentStatus.IsRunning)
        {
            return currentStatus;
        }

        try
        {
            SendLiveCaptionsShortcut();
        }
        catch (Exception ex)
        {
            return LiveCaptionsStatus.Error(ex);
        }

        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            currentStatus = _detector.Detect();
            if (currentStatus.IsRunning)
            {
                return currentStatus;
            }
        }

        return currentStatus;
    }

    private static void SendLiveCaptionsShortcut()
    {
        Input[] inputs =
        [
            KeyDown(VirtualKeyLeftWindows),
            KeyDown(VirtualKeyControl),
            KeyDown(VirtualKeyL),
            KeyUp(VirtualKeyL),
            KeyUp(VirtualKeyControl),
            KeyUp(VirtualKeyLeftWindows)
        ];

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput failed. Sent {sent} of {inputs.Length} keyboard events. Win32Error={Marshal.GetLastWin32Error()}.");
        }
    }

    private static Input KeyDown(ushort virtualKey)
    {
        return CreateKeyboardInput(virtualKey, 0);
    }

    private static Input KeyUp(ushort virtualKey)
    {
        return CreateKeyboardInput(virtualKey, KeyEventKeyUp);
    }

    private static Input CreateKeyboardInput(ushort virtualKey, uint flags)
    {
        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                KeyboardInput = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = flags
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput MouseInput;

        [FieldOffset(0)]
        public KeyboardInput KeyboardInput;

        [FieldOffset(0)]
        public HardwareInput HardwareInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort LowParam;
        public ushort HighParam;
    }
}
