using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace BetterMuv.Services;

/// <summary>检测管理员权限，并以 UAC 提升方式重启当前进程。</summary>
internal static class ElevationHelper
{
    internal static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 弹出 UAC 并以管理员身份重启；成功启动新进程后关闭当前应用。
    /// 用户取消 UAC 时返回 false。
    /// </summary>
    internal static bool TryRestartElevated(params string[] extraArgs)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exePath))
            throw new InvalidOperationException("无法定位当前程序路径，无法提升权限重启。");

        var args = new List<string>();
        foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (string.Equals(arg, AppInstance.OpenChildSessionArgument, StringComparison.OrdinalIgnoreCase))
                continue;
            args.Add(arg);
        }

        foreach (string extra in extraArgs)
        {
            if (!args.Contains(extra, StringComparer.OrdinalIgnoreCase))
                args.Add(extra);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            Arguments = string.Join(" ", args.Select(QuoteArg))
        };

        try
        {
            Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户取消了 UAC
            return false;
        }

        Application.Current?.Shutdown();
        return true;
    }

    private static string QuoteArg(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";
        if (arg.Contains(' ') || arg.Contains('"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }
}
