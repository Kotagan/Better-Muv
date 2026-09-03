using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);

double rx = args.Length > 0 && double.TryParse(args[0], out double a) ? a : 0.5;
double ry = args.Length > 1 && double.TryParse(args[1], out double b) ? b : 0.5;

nint hwnd = Native.FindGame();
if (hwnd == 0)
{
    Console.WriteLine("NO_WINDOW");
    return 1;
}

Native.SetForegroundWindow(hwnd);
Thread.Sleep(250);
Native.GetClientRect(hwnd, out Native.RECT cr);
var pt = new Native.POINT();
Native.ClientToScreen(hwnd, ref pt);
int x = pt.X + (int)((cr.Right - cr.Left) * rx);
int y = pt.Y + (int)((cr.Bottom - cr.Top) * ry);
Native.SetCursorPos(x, y);
Thread.Sleep(40);
Native.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
Thread.Sleep(30);
Native.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
Console.WriteLine($"clicked {x},{y} ratio={rx:F2},{ry:F2} client={cr.Right - cr.Left}x{cr.Bottom - cr.Top} pid={Native.GetPid(hwnd)}");
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
            catch { }

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
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public struct POINT { public int X, Y; }
}
