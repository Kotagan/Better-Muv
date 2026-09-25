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
    private const ushort VkEscape = 0x1B;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const ushort VkA = 0x41;
    private const ushort VkDelete = 0x2E;

    public async Task ClickAsync(nint windowHandle, Point screenPoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("点击前发现游戏窗口已经关闭。");

        // 点击前强制抢前台（含关掉开始菜单），避免 SendInput 打到桌面/菜单。
        for (int i = 0; i < 3; i++)
        {
            EnsureForeground(windowHandle);
            if (GetForegroundWindow() == windowHandle && !IsShellOverlayVisible())
                break;
            if (IsShellOverlayVisible())
            {
                SendKey(VkEscape, KeyDown);
                SendKey(VkEscape, KeyUp);
            }
            await Task.Delay(80, cancellationToken);
        }

        await Task.Delay(80, cancellationToken);
        SendMove((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
        // 悬停稍久，避免全屏客户端还没认出 hover 就按下。
        await Task.Delay(120, cancellationToken);

        bool isDown = false;
        try
        {
            SendMouse(0, 0, LeftDown);
            isDown = true;
            await Task.Delay(110, cancellationToken);
        }
        finally
        {
            if (isDown)
                SendMouse(0, 0, LeftUp);
        }

        // 抬起后再停一下，再挪开光标时游戏才来得及吃到点击。
        await Task.Delay(90, cancellationToken);
    }

    /// <summary>把光标挪开，避免停在粉钮上挡住 BitBlt 模板识别。</summary>
    public void MoveTo(nint windowHandle, Point screenPoint)
    {
        if (!IsWindow(windowHandle))
            return;
        SendMove((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
    }

    public async Task<bool> FocusWindowAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("聚焦前发现游戏窗口已经关闭。");

        if (IsIconic(windowHandle))
            return false;

        // 开始菜单/搜索盖住全屏时，GetForegroundWindow 仍可能是游戏，必须先关掉遮罩。
        if (IsShellOverlayVisible())
        {
            SendKey(VkEscape, KeyDown);
            SendKey(VkEscape, KeyUp);
            await Task.Delay(200, cancellationToken);
        }

        for (int attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt == 4 && IsShellOverlayVisible())
            {
                SendKey(VkEscape, KeyDown);
                SendKey(VkEscape, KeyUp);
                await Task.Delay(200, cancellationToken);
            }

            EnsureForeground(windowHandle);
            if (GetForegroundWindow() == windowHandle && !IsShellOverlayVisible())
            {
                await Task.Delay(120, cancellationToken);
                return GetForegroundWindow() == windowHandle && !IsShellOverlayVisible();
            }
            await Task.Delay(50, cancellationToken);
        }

        return GetForegroundWindow() == windowHandle && !IsShellOverlayVisible();
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

    /// <summary>向已聚焦窗口发送 Ctrl+V（剪贴板须已由调用方写好）。</summary>
    public async Task SendCtrlVAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        await SendChordAsync(windowHandle, VkControl, VkV, cancellationToken);
    }

    /// <summary>向已聚焦窗口发送 Ctrl+A 全选。</summary>
    public async Task SendCtrlAAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        await SendChordAsync(windowHandle, VkControl, VkA, cancellationToken);
    }

    /// <summary>向已聚焦窗口发送 Delete。</summary>
    public async Task SendDeleteAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("按键前发现游戏窗口已经关闭。");

        EnsureForeground(windowHandle);
        await Task.Delay(40, cancellationToken);
        SendKey(VkDelete, KeyDown);
        await Task.Delay(40, cancellationToken);
        SendKey(VkDelete, KeyUp);
        await Task.Delay(60, cancellationToken);
    }

    private async Task SendChordAsync(
        nint windowHandle, ushort modifier, ushort key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("按键前发现游戏窗口已经关闭。");

        EnsureForeground(windowHandle);
        await Task.Delay(60, cancellationToken);
        SendKey(modifier, KeyDown);
        await Task.Delay(30, cancellationToken);
        SendKey(key, KeyDown);
        await Task.Delay(40, cancellationToken);
        SendKey(key, KeyUp);
        await Task.Delay(30, cancellationToken);
        SendKey(modifier, KeyUp);
        await Task.Delay(80, cancellationToken);
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
        if (foreground == windowHandle && !IsShellOverlayVisible())
            return;

        uint foreThread = GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(windowHandle, out _);
        bool attachedFore = false;
        bool attachedTarget = false;
        if (foreThread != 0 && foreThread != thisThread)
            attachedFore = AttachThreadInput(foreThread, thisThread, true);
        if (targetThread != 0 && targetThread != thisThread && targetThread != foreThread)
            attachedTarget = AttachThreadInput(targetThread, thisThread, true);

        try
        {
            // Alt 轻点可解开前台锁，否则 SetForegroundWindow 会被系统直接拒绝。
            SendKey(VkMenu, KeyDown);
            SendKey(VkMenu, KeyUp);
            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(targetThread, thisThread, false);
            if (attachedFore)
                AttachThreadInput(foreThread, thisThread, false);
        }

        if (GetForegroundWindow() != windowHandle)
            SetForegroundWindow(windowHandle);
    }

    /// <summary>开始菜单或搜索是否还盖在屏幕上（关掉后才截得到游戏）。</summary>
    private static bool IsShellOverlayVisible()
    {
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            if (found || !IsWindowVisible(hwnd))
                return true;
            if (!GetWindowRect(hwnd, out RECT rect))
                return true;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            // 关掉后仍可能留着很小的隐藏宿主窗，只处理真正盖住画面的。
            if (width < 400 || height < 300)
                return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return true;
            try
            {
                string name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                if (name is "StartMenuExperienceHost" or "SearchHost")
                {
                    found = true;
                    return false;
                }
            }
            catch (ArgumentException)
            {
                // 进程已退出。
            }
            catch (InvalidOperationException)
            {
                // 进程已退出。
            }

            return true;
        }, 0);
        return found;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out RECT rect);
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
}
