using System.Runtime.InteropServices;
using System.Windows;
using BetterMuv.Core;

namespace BetterMuv.Services;

public sealed class MouseInputService
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint Move = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint Wheel = 0x0800;
    private const uint KeyDown = 0x0000;
    private const uint KeyUp = 0x0002;
    private const uint VirtualDesk = 0x4000;
    private const uint Absolute = 0x8000;
    private const ushort VkMenu = 0x12;
    private const ushort VkReturn = 0x0D;

    public async Task ClickAsync(nint windowHandle, Point screenPoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("点击前发现游戏窗口已经关闭。");

        // 点击只保证前台，绝不 ShowWindow / 改尺寸。
        EnsureForeground(windowHandle);
        await Task.Delay(40, cancellationToken);
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

    public async Task<bool> FocusWindowAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("聚焦前发现游戏窗口已经关闭。");

        if (IsIconic(windowHandle))
            return false;

        for (int attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureForeground(windowHandle);
            if (GetForegroundWindow() == windowHandle)
            {
                await Task.Delay(120, cancellationToken);
                return GetForegroundWindow() == windowHandle;
            }
            await Task.Delay(50, cancellationToken);
        }

        return GetForegroundWindow() == windowHandle;
    }

    /// <summary>向已聚焦的游戏窗口发送 Alt+Enter 切换全屏。</summary>
    public async Task SendAltEnterAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("发送快捷键前发现游戏窗口已经关闭。");

        EnsureForeground(windowHandle);
        await Task.Delay(80, cancellationToken);
        SendKey(VkMenu, KeyDown);
        await Task.Delay(30, cancellationToken);
        SendKey(VkReturn, KeyDown);
        await Task.Delay(40, cancellationToken);
        SendKey(VkReturn, KeyUp);
        await Task.Delay(30, cancellationToken);
        SendKey(VkMenu, KeyUp);
    }

    public async Task WheelAsync(
        nint windowHandle, Point screenPoint, int wheelNotches, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("滚动前发现游戏窗口已经关闭。");
        EnsureForeground(windowHandle);
        SendMove((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
        await Task.Delay(40, cancellationToken);
        SendMouse(0, 0, Wheel, unchecked((uint)(wheelNotches * 120)));
    }

    public async Task DragAsync(
        nint windowHandle, Point from, Point to, CancellationToken cancellationToken, int steps = 12)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("拖动前发现游戏窗口已经关闭。");
        EnsureForeground(windowHandle);
        SendMove((int)Math.Round(from.X), (int)Math.Round(from.Y));
        await Task.Delay(60, cancellationToken);
        bool isDown = false;
        try
        {
            SendMouse(0, 0, LeftDown);
            isDown = true;
            for (int i = 1; i <= Math.Max(2, steps); i++)
            {
                double t = i / (double)Math.Max(2, steps);
                SendMove(
                    (int)Math.Round(from.X + (to.X - from.X) * t),
                    (int)Math.Round(from.Y + (to.Y - from.Y) * t));
                await Task.Delay(18, cancellationToken);
            }
        }
        finally
        {
            if (isDown) SendMouse(0, 0, LeftUp);
        }
    }

    public static bool IsForeground(nint windowHandle) =>
        IsWindow(windowHandle) && GetForegroundWindow() == windowHandle;

    private static void EnsureForeground(nint windowHandle)
    {
        // 绝不调用 ShowWindow：最小化/全屏游戏被 Restore/Show 后会变成窗口化小尺寸。
        if (IsIconic(windowHandle))
            return; // 最小化时不擅自还原，避免缩窗；由用户手动恢复全屏。

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
            SendKey(VkMenu, KeyUp);
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

    private static void SendMouse(int dx, int dy, uint flags, uint mouseData = 0)
    {
        Input[] inputs =
        [
            new()
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MouseInput { Dx = dx, Dy = dy, MouseData = mouseData, Flags = flags }
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
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
}
