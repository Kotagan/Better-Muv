using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using BetterMuv.Core;

namespace BetterMuv.Services;

public sealed record GameWindow(nint Handle, string Title, ScreenRect ClientRect, ScreenRect DisplayRect);

public sealed class WindowCaptureService
{
    private const int SourceCopy = 0x00CC0020;
    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    public GameWindow FindWindow(string titleKeyword)
    {
        GameWindow? match = null;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;

            int length = GetWindowTextLength(handle);
            if (length == 0)
                return true;

            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            if (!title.ToString().Contains(titleKeyword, StringComparison.OrdinalIgnoreCase))
                return true;

            match = new GameWindow(
                handle,
                title.ToString(),
                GetClientRectOnScreen(handle),
                GetDisplayRect(handle));
            return false;
        }, 0);

        return match ?? throw new InvalidOperationException($"找不到标题包含“{titleKeyword}”的可见窗口。");
    }

    public GameWindow Refresh(GameWindow window)
    {
        if (!IsWindow(window.Handle))
            throw new InvalidOperationException("游戏窗口已经关闭。");
        return window with
        {
            ClientRect = GetClientRectOnScreen(window.Handle),
            DisplayRect = GetDisplayRect(window.Handle)
        };
    }

    public BitmapSource Capture(ScreenRect screenRect) =>
        CaptureFromDesktop(screenRect);

    /// <summary>
    /// 从游戏窗口客户区截图（不受本工具窗口遮挡影响）。
    /// </summary>
    public BitmapSource Capture(GameWindow window, ScreenRect screenRect)
    {
        int x = screenRect.Left - window.ClientRect.Left;
        int y = screenRect.Top - window.ClientRect.Top;
        int width = screenRect.Width;
        int height = screenRect.Height;
        if (x < 0 || y < 0 ||
            x + width > window.ClientRect.Width ||
            y + height > window.ClientRect.Height)
        {
            // 越界时回退桌面截图，兼容客户区小于整屏的情况。
            return CaptureFromDesktop(screenRect);
        }

        nint sourceDc = GetDC(window.Handle);
        if (sourceDc == 0)
            return CaptureFromDesktop(screenRect);

        nint memoryDc = 0;
        nint bitmap = 0;
        nint oldObject = 0;
        try
        {
            memoryDc = CreateCompatibleDC(sourceDc);
            bitmap = CreateCompatibleBitmap(sourceDc, width, height);
            if (memoryDc == 0 || bitmap == 0)
                return CaptureFromDesktop(screenRect);

            oldObject = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, width, height, sourceDc, x, y, SourceCopy))
                return CaptureFromDesktop(screenRect);

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (oldObject != 0) SelectObject(memoryDc, oldObject);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memoryDc != 0) DeleteDC(memoryDc);
            ReleaseDC(window.Handle, sourceDc);
        }
    }

    private static BitmapSource CaptureFromDesktop(ScreenRect screenRect)
    {
        nint sourceDc = GetDC(0);
        if (sourceDc == 0)
            throw new InvalidOperationException("无法获取桌面 DC。");

        nint memoryDc = 0;
        nint bitmap = 0;
        nint oldObject = 0;
        try
        {
            memoryDc = CreateCompatibleDC(sourceDc);
            bitmap = CreateCompatibleBitmap(sourceDc, screenRect.Width, screenRect.Height);
            if (memoryDc == 0 || bitmap == 0)
                throw new InvalidOperationException("无法创建截图缓冲区。");

            oldObject = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, screenRect.Width, screenRect.Height,
                    sourceDc, screenRect.Left, screenRect.Top, SourceCopy))
                throw new InvalidOperationException("BitBlt 截图失败。");

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (oldObject != 0) SelectObject(memoryDc, oldObject);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memoryDc != 0) DeleteDC(memoryDc);
            ReleaseDC(0, sourceDc);
        }
    }

    private static ScreenRect GetClientRectOnScreen(nint handle)
    {
        if (!GetClientRect(handle, out NativeRect rect))
            throw new InvalidOperationException("无法读取游戏客户区。");
        var origin = new NativePoint();
        if (!ClientToScreen(handle, ref origin))
            throw new InvalidOperationException("无法转换客户区屏幕坐标。");

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("游戏窗口已最小化或客户区尺寸无效。");
        return new ScreenRect(origin.X, origin.Y, width, height);
    }

    private static ScreenRect GetDisplayRect(nint handle)
    {
        nint monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("无法读取游戏所在显示器的边界。");
        return new ScreenRect(
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left,
            info.Monitor.Bottom - info.Monitor.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint handle, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int operation);
}
