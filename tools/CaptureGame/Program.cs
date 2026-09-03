using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);

string outDir = args.Length > 0 ? args[0] : @"C:\workspace\Better-Muv\diagnostics-mainquest";
Directory.CreateDirectory(outDir);
string path = Path.Combine(outDir, $"screen-{DateTime.Now:HHmmss}.png");

nint hwnd = Native.FindGame();
if (hwnd == 0)
{
    Console.WriteLine("NO_WINDOW");
    return 1;
}

var sb = new StringBuilder(512);
Native.GetWindowText(hwnd, sb, sb.Capacity);
Console.WriteLine($"hwnd={hwnd} title={sb} pid={Native.GetPid(hwnd)}");

Native.SetForegroundWindow(hwnd);
Thread.Sleep(400);

Native.GetClientRect(hwnd, out Native.RECT cr);
var pt = new Native.POINT();
Native.ClientToScreen(hwnd, ref pt);
int w = cr.Right - cr.Left;
int h = cr.Bottom - cr.Top;
uint dpi = Native.GetDpiForWindow(hwnd);
Console.WriteLine($"client={w}x{h} @{pt.X},{pt.Y} dpi={dpi}");

nint src = Native.GetDC(0);
nint mem = Native.CreateCompatibleDC(src);
nint bmp = Native.CreateCompatibleBitmap(src, w, h);
nint old = Native.SelectObject(mem, bmp);
Native.BitBlt(mem, 0, 0, w, h, src, pt.X, pt.Y, 0x00CC0020);
Native.SelectObject(mem, old);
using (var bitmap = Image.FromHbitmap(bmp))
    bitmap.Save(path, ImageFormat.Png);
Native.DeleteObject(bmp);
Native.DeleteDC(mem);
Native.ReleaseDC(0, src);
Console.WriteLine($"saved -> {path}");
return 0;

static class Native
{
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = (nint)(-4);

    public static nint FindGame()
    {
        nint byProcess = 0;
        nint byTitle = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            uint pid = GetPid(h);
            try
            {
                string? name = Process.GetProcessById((int)pid).ProcessName;
                if (name.Contains("muv_luv", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("girlsgarden", StringComparison.OrdinalIgnoreCase))
                {
                    GetClientRect(h, out RECT r);
                    if (r.Right - r.Left >= 800 && r.Bottom - r.Top >= 600)
                    {
                        byProcess = h;
                        return false;
                    }
                }
            }
            catch { /* ignore */ }

            var title = new StringBuilder(512);
            GetWindowText(h, title, title.Capacity);
            string t = title.ToString();
            if (byTitle == 0 && (t.Contains("ガールズガーデン") || t.Contains("マブラヴ・")))
                byTitle = h;
            return true;
        }, 0);
        return byProcess != 0 ? byProcess : byTitle;
    }

    public static uint GetPid(nint h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        return pid;
    }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(nint hdcDest, int x, int y, int w, int h, nint hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint hdc, nint hgdi);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint hdc);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public struct POINT { public int X, Y; }
}
