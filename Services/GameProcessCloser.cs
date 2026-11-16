using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterMuv.Core;

namespace BetterMuv.Services;

/// <summary>关闭游戏窗口/进程（千机链 <c>-c</c>、CLI relaunch 共用）。</summary>
public static class GameProcessCloser
{
    private static readonly string[] ProcessNameHints =
    [
        GamePathLocator.ProcessName,
        "muv_luv_girlsgarden",
        "muv_luv_girlsgardenx",
        "muv_luv_girlsgardenx_cl"
    ];

    public static async Task CloseAsync(
        AutomationConfig config,
        Action<string> log,
        CancellationToken cancellationToken,
        int waitVisibleMs = 15000)
    {
        nint? hwnd = null;
        string? processName = null;
        try
        {
            var capture = new WindowCaptureService();
            GameWindow window = capture.FindWindow(config);
            hwnd = window.Handle;
            log($"关闭现有游戏窗口：{window.Title}");
            try
            {
                GetWindowThreadProcessId(window.Handle, out uint pid);
                processName = Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                // 忽略取进程名失败。
            }

            NativeCloseWindow(window.Handle);
        }
        catch (InvalidOperationException)
        {
            log("当前没有游戏窗口。");
        }

        var timer = Stopwatch.StartNew();
        var screen = new ScreenAutomation(config, log);
        while (timer.ElapsedMilliseconds < waitVisibleMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool visible;
            try
            {
                _ = screen.FindWindow(config.WindowTitleKeyword);
                visible = true;
            }
            catch (InvalidOperationException)
            {
                visible = false;
            }

            if (!visible)
            {
                log("游戏窗口已关闭。");
                break;
            }

            await Task.Delay(500, cancellationToken);
        }

        KillByNames(log, processName);
    }

    public static void KillByNames(Action<string> log, string? knownProcessName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(knownProcessName))
            names.Add(knownProcessName);
        foreach (string hint in ProcessNameHints)
            names.Add(hint);

        foreach (string name in names)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (Process proc in procs)
            {
                try
                {
                    log($"结束游戏进程：{proc.ProcessName} (pid {proc.Id})");
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    log($"结束进程失败：{proc.ProcessName} — {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }

    private static void NativeCloseWindow(nint hwnd) =>
        PostMessage(hwnd, 0x0010 /* WM_CLOSE */, 0, 0);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
