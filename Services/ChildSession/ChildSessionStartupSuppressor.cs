using Microsoft.Win32;

namespace BetterMuv.Services.ChildSession;

/// <summary>
/// 桌面分身登录会再次执行当前用户的开机启动项，容易冲突/卡死。
/// 连接前临时禁用 Run/RunOnce 与「启动」文件夹，分身登录完成后立刻还原。
/// </summary>
internal static class ChildSessionStartupSuppressor
{
    private const string PoliciesExplorerPath =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    private static readonly string[] PolicyValueNames =
    [
        "DisableCurrentUserRun",
        "DisableLocalMachineRun",
        "DisableCurrentUserRunOnce",
        "DisableLocalMachineRunOnce"
    ];

    private static readonly object Sync = new();
    private static bool _active;
    private static readonly Dictionary<string, object?> PreviousPolicyValues = new(StringComparer.OrdinalIgnoreCase);
    private static string? _startupFolderPath;
    private static string? _startupFolderBackupPath;

    internal static bool IsActive
    {
        get { lock (Sync) return _active; }
    }

    internal static void Suppress(Action<string>? log = null)
    {
        lock (Sync)
        {
            if (_active)
            {
                log?.Invoke("桌面分身：开机启动项已处于抑制状态。");
                return;
            }

            try
            {
                SaveAndSetRunPolicies(log);
                RenameStartupFolder(log);
                _active = true;
                log?.Invoke("桌面分身：已临时禁止开机启动项（Run / 启动文件夹），登录完成后会还原。");
            }
            catch (Exception ex)
            {
                // 尽量回滚已做的改动，避免父会话长期丢启动项。
                try { RestoreCore(log); } catch { /* ignore */ }
                throw new InvalidOperationException(
                    "无法临时禁用开机启动项：" + ex.Message, ex);
            }
        }
    }

    internal static void Restore(Action<string>? log = null)
    {
        lock (Sync)
        {
            if (!_active)
                return;
            try
            {
                RestoreCore(log);
                log?.Invoke("桌面分身：开机启动项设置已还原。");
            }
            catch (Exception ex)
            {
                log?.Invoke("桌面分身：还原开机启动项失败：" + ex.Message);
            }
            finally
            {
                _active = false;
            }
        }
    }

    private static void SaveAndSetRunPolicies(Action<string>? log)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(PoliciesExplorerPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Policies\\Explorer。");

        PreviousPolicyValues.Clear();
        foreach (string name in PolicyValueNames)
        {
            PreviousPolicyValues[name] = key.GetValue(name);
            key.SetValue(name, 1, RegistryValueKind.DWord);
        }

        log?.Invoke("桌面分身：已写入禁用 Run/RunOnce 策略。");
    }

    private static void RenameStartupFolder(Action<string>? log)
    {
        string startup = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        _startupFolderPath = startup;
        _startupFolderBackupPath = startup + ".BetterMuvDisabled";

        if (!Directory.Exists(startup))
        {
            log?.Invoke("桌面分身：未找到启动文件夹，跳过重命名。");
            return;
        }

        if (Directory.Exists(_startupFolderBackupPath))
        {
            // 上次异常退出残留：先清掉空备份或合并回启动目录。
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(_startupFolderBackupPath))
                {
                    string name = Path.GetFileName(entry);
                    string dest = Path.Combine(startup, name);
                    if (!File.Exists(dest) && !Directory.Exists(dest))
                        Directory.Move(entry, dest);
                }
                Directory.Delete(_startupFolderBackupPath, recursive: true);
            }
            catch (Exception ex)
            {
                log?.Invoke("桌面分身：清理残留启动备份时警告：" + ex.Message);
            }
        }

        Directory.Move(startup, _startupFolderBackupPath);
        Directory.CreateDirectory(startup); // 保持路径存在，避免部分程序报错
        log?.Invoke("桌面分身：已暂时移走「启动」文件夹。");
    }

    private static void RestoreCore(Action<string>? log)
    {
        // 还原策略
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PoliciesExplorerPath, writable: true);
            if (key is not null)
            {
                foreach (string name in PolicyValueNames)
                {
                    if (!PreviousPolicyValues.TryGetValue(name, out object? previous) || previous is null)
                        key.DeleteValue(name, throwOnMissingValue: false);
                    else
                        key.SetValue(name, previous);
                }
            }
        }
        finally
        {
            PreviousPolicyValues.Clear();
        }

        // 还原启动文件夹
        string? original = _startupFolderPath;
        string? backup = _startupFolderBackupPath;
        _startupFolderPath = null;
        _startupFolderBackupPath = null;

        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(backup))
            return;

        if (!Directory.Exists(backup))
            return;

        try
        {
            if (Directory.Exists(original))
            {
                // 我们创建的空 Startup：删掉再移回
                bool onlyPlaceholder = !Directory.EnumerateFileSystemEntries(original).Any();
                if (onlyPlaceholder)
                    Directory.Delete(original, recursive: false);
                else
                {
                    // 分身期间若有程序往 Startup 写了东西，合并进备份再整体换回
                    foreach (string entry in Directory.EnumerateFileSystemEntries(original))
                    {
                        string name = Path.GetFileName(entry);
                        string dest = Path.Combine(backup, name);
                        if (!File.Exists(dest) && !Directory.Exists(dest))
                            Directory.Move(entry, dest);
                    }
                    Directory.Delete(original, recursive: true);
                }
            }

            Directory.Move(backup, original);
        }
        catch (Exception ex)
        {
            log?.Invoke("桌面分身：还原启动文件夹失败：" + ex.Message
                + $"。请手动把「{backup}」改回「{original}」。");
        }
    }
}
