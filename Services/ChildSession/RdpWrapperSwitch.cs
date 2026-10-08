using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

namespace BetterMuv.Services.ChildSession;

/// <summary>
/// RDP Wrapper / SuperRDP 会把 TermService 的 ServiceDll 指到 rdpwrap.dll，
/// 与 WTS Child Session 冲突并常见错误 516。启动分身时可临时切回 termsrv.dll，退出后还原。
/// </summary>
internal static class RdpWrapperSwitch
{
    private const string TermServiceParametersPath =
        @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";
    private const string ServiceDllValueName = "ServiceDll";

    private static readonly object Sync = new();
    private static string? _backupServiceDll;
    private static bool _switched;

    internal static bool IsSwitchedAwayFromWrapper
    {
        get { lock (Sync) return _switched; }
    }

    internal static string NativeTermServiceDllPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "termsrv.dll");

    internal static string? ReadServiceDll()
    {
        try
        {
            using RegistryKey localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = localMachine.OpenSubKey(TermServiceParametersPath);
            return key?.GetValue(ServiceDllValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>若当前为 RDP Wrapper，则备份并切到原生 termsrv.dll，然后重启 TermService。</summary>
    internal static void TemporarilyUseNativeTermService(Action<string>? log = null)
    {
        lock (Sync)
        {
            if (_switched)
            {
                log?.Invoke("桌面分身：已处于临时原生 RDP 模式，跳过重复切换。");
                return;
            }

            string? current = ReadServiceDll();
            if (string.IsNullOrWhiteSpace(current) ||
                !current.Contains("rdpwrap.dll", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("桌面分身：未检测到 RDP Wrapper ServiceDll，无需切换。");
                return;
            }

            if (!File.Exists(NativeTermServiceDllPath))
                throw new FileNotFoundException("找不到系统原生 termsrv.dll。", NativeTermServiceDllPath);

            log?.Invoke($"桌面分身：检测到 RDP Wrapper（{current}），临时切回 {NativeTermServiceDllPath}");
            WriteServiceDll(NativeTermServiceDllPath);
            _backupServiceDll = current;
            _switched = true;
            RestartTermService(log);
            // 给 TermService / Session 子系统一点就绪时间，避免立刻连上仍报 516。
            Thread.Sleep(1500);
            log?.Invoke("桌面分身：TermService 已重启为原生模式。");
        }
    }

    /// <summary>若曾临时切换，则还原 RDP Wrapper 的 ServiceDll 并重启 TermService。</summary>
    internal static void RestoreIfNeeded(Action<string>? log = null)
    {
        lock (Sync)
        {
            if (!_switched || string.IsNullOrWhiteSpace(_backupServiceDll))
                return;

            string restorePath = _backupServiceDll;
            try
            {
                log?.Invoke($"桌面分身：还原 RDP Wrapper ServiceDll → {restorePath}");
                WriteServiceDll(restorePath);
                RestartTermService(log);
                log?.Invoke("桌面分身：RDP Wrapper 已还原。");
            }
            catch (Exception ex)
            {
                log?.Invoke("桌面分身：还原 RDP Wrapper 失败：" + ex.Message
                    + "。请手动把 TermService\\Parameters\\ServiceDll 改回 rdpwrap.dll 并重启 TermService。");
            }
            finally
            {
                _backupServiceDll = null;
                _switched = false;
            }
        }
    }

    private static void WriteServiceDll(string path)
    {
        try
        {
            using RegistryKey localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = localMachine.OpenSubKey(TermServiceParametersPath, writable: true)
                ?? throw new InvalidOperationException(
                    "无法打开 TermService Parameters 注册表。请以管理员身份运行 Better-Muv 后再试。");
            key.SetValue(ServiceDllValueName, path, RegistryValueKind.ExpandString);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                "写入 TermService\\ServiceDll 需要管理员权限。请右键「以管理员身份运行」Better-Muv 后再启动桌面分身。",
                ex);
        }
        catch (System.Security.SecurityException ex)
        {
            throw new InvalidOperationException(
                "写入 TermService\\ServiceDll 需要管理员权限。请右键「以管理员身份运行」Better-Muv 后再启动桌面分身。",
                ex);
        }
    }

    /// <summary>
    /// 卡住的 Child Session / 切换 RDP Wrapper 后，常需重启 TermService 才能再次 LoginComplete。
    /// </summary>
    internal static void TryRestartTermService(Action<string>? log = null)
    {
        RestartTermService(log);
        Thread.Sleep(1500);
    }

    private static void RestartTermService(Action<string>? log)
    {
        using var controller = new ServiceController("TermService");
        TimeSpan timeout = TimeSpan.FromSeconds(45);

        if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            log?.Invoke("桌面分身：正在停止 TermService…");
            try
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }
            catch (InvalidOperationException ex)
            {
                // 某些依赖会话下 Stop 可能失败，继续尝试 Start/刷新。
                log?.Invoke("桌面分身：停止 TermService 警告：" + ex.Message);
            }
        }

        controller.Refresh();
        if (controller.Status != ServiceControllerStatus.Running)
        {
            log?.Invoke("桌面分身：正在启动 TermService…");
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }
    }
}
