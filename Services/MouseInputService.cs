using System.Runtime.InteropServices;
using System.Windows;

namespace BetterMuv.Services;

public sealed class MouseInputService
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint Move = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint KeyUp = 0x0002;
    private const uint VirtualDesk = 0x4000;
    private const uint Absolute = 0x8000;
    private const int Restore = 9;
    private const int Show = 5;
    private const ushort VkMenu = 0x12;

    public async Task ClickAsync(nint windowHandle, Point screenPoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("点击前发现游戏窗口已经关闭。");

        // 仅在最小化时 SW_RESTORE。对已最大化/全屏窗口无条件 Restore
        // 会把它缩回“还原”尺寸，表现为启动自动化后游戏窗突然变小。
        FocusWindow(windowHandle);
        await Task.Delay(80, cancellationToken);
        SendMove((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
        await Task.Delay(40, cancellationToken);

        bool isDown = false;
        try
        {
            SendMouse(0, 0, LeftDown);
            isDown = true;
            await Task.Delay(50, cancellationToken);
        }
        finally
        {
            if (isDown)
                SendMouse(0, 0, LeftUp);
        }
    }

    public async Task FocusWindowAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("聚焦前发现游戏窗口已经关闭。");

        FocusWindow(windowHandle);
        await Task.Delay(150, cancellationToken);
    }

    private static void FocusWindow(nint windowHandle)
    {
        if (IsIconic(windowHandle))
            ShowWindow(windowHandle, Restore);
        else
            ShowWindow(windowHandle, Show);

        nint foreground = GetForegroundWindow();
        if (foreground == windowHandle)
            return;

        uint foreThread = GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = false;
        if (foreThread != 0 && foreThread != thisThread)
            attached = AttachThreadInput(foreThread, thisThread, true);

        try
        {
            // 松开一次 Alt，绕过部分前台锁定限制。
            SendKey(VkMenu, KeyUp);
            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attached)
                AttachThreadInput(foreThread, thisThread, false);
        }

        if (GetForegroundWindow() != windowHandle)
            SetForegroundWindow(windowHandle);
    }

    private static void SendMove(int x, int y)
    {
        int left = GetSystemMetrics(76);
        int top = GetSystemMetrics(77);
        int width = Math.Max(1, GetSystemMetrics(78));
        int height = Math.Max(1, GetSystemMetrics(79));
        int absoluteX = Math.Clamp((int)Math.Round((x - left) * 65535.0 / Math.Max(1, width - 1)), 0, 65535);
        int absoluteY = Math.Clamp((int)Math.Round((y - top) * 65535.0 / Math.Max(1, height - 1)), 0, 65535);
        SendMouse(absoluteX, absoluteY, Move | Absolute | VirtualDesk);
    }

    private static void SendMouse(int dx, int dy, uint flags)
    {
        Input[] inputs =
        [
            new()
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = flags }
                }
            }
        ];
        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("SendInput 失败；请确认游戏与工具的权限级别一致。");
    }

    private static void SendKey(ushort virtualKey, uint flags)
    {
        Input[] inputs =
        [
            new()
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags }
                }
            }
        ];
        _ = SendInput(1, inputs, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
}
