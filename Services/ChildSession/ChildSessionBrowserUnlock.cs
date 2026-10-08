using System.Diagnostics;

namespace BetterMuv.Services.ChildSession;

/// <summary>
/// 同一 Windows 用户的主会话若已打开 Chrome/Edge，会独占用户配置目录，
/// 分身里点「登录」拉起的浏览器会无窗口/秒退。结束主会话中的浏览器进程后即可。
/// </summary>
internal static class ChildSessionBrowserUnlock
{
    private static readonly string[] BrowserProcessNames =
    [
        "chrome",
        "msedge"
    ];

    internal static int TerminateBrowsersInCurrentSession()
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        int killed = 0;

        foreach (string name in BrowserProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (Process process in processes)
            {
                try
                {
                    if (process.SessionId != sessionId)
                        continue;
                    process.Kill(entireProcessTree: true);
                    killed++;
                }
                catch
                {
                    // 个别进程已退出或无权限，忽略
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return killed;
    }
}
