using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BetterMuv.Core;

/// <summary>定位并校验用户配置的游戏启动路径（任意 .exe / 指向 exe 的 .lnk）。</summary>
public static class GamePathLocator
{
    /// <summary>自动搜索时优先查找的进程名（不限制用户手动配置的文件名）。</summary>
    public const string ProcessName = "muv_luv_girlsgardenx_cl";
    /// <summary>自动搜索时优先查找的文件名（不限制用户手动配置的文件名）。</summary>
    public const string ExecutableName = "muv_luv_girlsgardenx_cl.exe";

    private static readonly string[] RelativeHints =
    [
        @"muvluv\muv_luv_girlsgardenx_cl",
        @"muv_luv_girlsgardenx_cl",
        @"Games\muvluv\muv_luv_girlsgardenx_cl",
        @"Games\muv_luv_girlsgardenx_cl",
        @"DMMGames\muv_luv_girlsgardenx_cl",
        @"DMMGamePlayer\GameContents\muv_luv_girlsgardenx_cl",
        @"Program Files\DMMGamePlayer\GameContents\muv_luv_girlsgardenx_cl",
        @"Program Files (x86)\DMMGamePlayer\GameContents\muv_luv_girlsgardenx_cl"
    ];

    public static bool IsShortcut(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DMMGamePlayerFastLauncher 快捷方式目标常是 schtasks.exe，真正启动靠 /run /tn。
    /// 这种启动方式支持：请保存 .lnk，或保存 schtasks.exe 并带上计划任务参数。
    /// </summary>
    public static bool IsSchtasksLauncher(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetFileName(path), "schtasks.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>兼容旧名。</summary>
    public static bool IsBareSchtasksStub(string? path) => IsSchtasksLauncher(path);

    public static bool HasSchtasksRunArguments(string? arguments) =>
        !string.IsNullOrWhiteSpace(arguments) &&
        arguments.Contains("/run", StringComparison.OrdinalIgnoreCase) &&
        arguments.Contains("/tn", StringComparison.OrdinalIgnoreCase);

    /// <summary>路径本身是否可保存（.lnk / 普通 exe；裸 schtasks 需另配参数才可启动）。</summary>
    public static bool IsValid(string? path) =>
        IsValid(path, launchArguments: null, allowSchtasksWithoutArgs: false);

    public static bool IsValid(string? path, string? launchArguments, bool allowSchtasksWithoutArgs = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        if (IsShortcut(path))
            return true;
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsSchtasksLauncher(path))
            return allowSchtasksWithoutArgs || HasSchtasksRunArguments(launchArguments);
        return true;
    }

    /// <summary>
    /// 规范化用户选择的路径。DMM 快速启动请直接选桌面 .lnk（会原样保留，启动时等价双击快捷方式）。
    /// </summary>
    public static string? TryNormalize(string? selectedPath, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            reason = "路径为空。";
            return null;
        }

        string path = selectedPath.Trim().Trim('"');

        if (IsShortcut(path))
        {
            if (!File.Exists(path))
            {
                reason = "快捷方式不存在：" + path;
                return null;
            }

            // 保留 .lnk：DMM FastLauncher / 计划任务参数都在快捷方式里。
            return Path.GetFullPath(path);
        }

        if (Directory.Exists(path))
        {
            string? inDir = FindPreferredExecutableNearby(path);
            if (inDir is not null)
                return inDir;

            reason = "目录中未找到可用的 .exe：" + path;
            return null;
        }

        if (!File.Exists(path))
        {
            reason = "文件不存在：" + path;
            return null;
        }

        if (IsSchtasksLauncher(path))
        {
            // 支持这种启动方式，但裸路径不够；优先找回对应的桌面 .lnk。
            string? shortcut = EnumerateLikelyShortcuts().FirstOrDefault(IsValid);
            if (shortcut is not null)
            {
                reason = "";
                return Path.GetFullPath(shortcut);
            }

            reason =
                "检测到 schtasks.exe（DMM 快速启动常用）。" +
                "请改选桌面上的游戏快捷方式 .lnk（推荐），" +
                "或在启动参数里填写该快捷方式的 /run /tn … 后再保存 schtasks.exe。";
            return null;
        }

        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(path);

        reason = $"请选择 .exe 或 .lnk（当前为 {Path.GetFileName(path)}）。";
        return null;
    }

    /// <summary>构造启动用的 ProcessStartInfo。.lnk 按资源管理器双击方式打开。</summary>
    public static ProcessStartInfo CreateLaunchStartInfo(
        string launchPath,
        string? extraArguments = null,
        string? workingDirectory = null)
    {
        string path = launchPath.Trim().Trim('"');
        if (IsShortcut(path))
        {
            return new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = true
            };
        }

        string args = extraArguments ?? "";
        string workDir = workingDirectory ?? "";
        if (string.IsNullOrWhiteSpace(workDir))
            workDir = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;

        return new ProcessStartInfo
        {
            FileName = path,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = true
        };
    }

    /// <summary>读取快捷方式参数。</summary>
    public static string? TryResolveShortcutArguments(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
            return null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return null;
            object shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("WScript.Shell");
            object shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath])!;
            string? arguments = shortcut.GetType().InvokeMember(
                "Arguments",
                System.Reflection.BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;
            return string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>配置缺参数时，尝试找回桌面快捷方式或默认客户端。</summary>
    public static string? TryRecoverLaunchPath(string? current, string? launchArguments = null)
    {
        if (!string.IsNullOrWhiteSpace(current) && IsValid(current, launchArguments))
            return Path.GetFullPath(current);

        foreach (string candidate in EnumerateLikelyShortcuts())
        {
            if (IsValid(candidate))
                return Path.GetFullPath(candidate);
        }

        return TryFind(null);
    }

    private static IEnumerable<string> EnumerateLikelyShortcuts()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        foreach (string dir in new[] { desktop, publicDesktop })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;

            string preferred = Path.Combine(dir, "muv.lnk");
            if (File.Exists(preferred))
                yield return preferred;

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.lnk");
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                (string? target, _) = TryResolveShortcutDetails(file);
                if (IsBareSchtasksStub(target) ||
                    (!string.IsNullOrWhiteSpace(target) &&
                     target.Contains("muv_luv", StringComparison.OrdinalIgnoreCase)))
                    yield return file;
            }
        }
    }

    /// <summary>读取 .lnk 目标路径；失败返回 null。</summary>
    public static string? TryResolveShortcut(string shortcutPath)
    {
        (string? target, _) = TryResolveShortcutDetails(shortcutPath);
        return target;
    }

    /// <summary>读取 .lnk 的 TargetPath 与 WorkingDirectory。</summary>
    public static (string? TargetPath, string? WorkingDirectory) TryResolveShortcutDetails(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
            return (null, null);
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return (null, null);
            object shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("WScript.Shell");
            object shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath])!;
            Type shortcutType = shortcut.GetType();
            string? target = shortcutType.InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;
            string? working = shortcutType.InvokeMember(
                "WorkingDirectory",
                System.Reflection.BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;
            target = string.IsNullOrWhiteSpace(target) ? null : target.Trim().Trim('"');
            working = string.IsNullOrWhiteSpace(working) ? null : working.Trim().Trim('"');
            return (target, working);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>目录旁优先找推荐游戏名，其次任意 .exe（仅当前目录，不全盘乱扫）。</summary>
    private static string? FindPreferredExecutableNearby(params string?[] directories)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) || !seen.Add(dir))
                continue;

            string preferred = Path.Combine(dir, ExecutableName);
            if (IsValid(preferred))
                return Path.GetFullPath(preferred);

            try
            {
                foreach (string child in Directory.EnumerateDirectories(dir))
                {
                    string candidate = Path.Combine(child, ExecutableName);
                    if (IsValid(candidate))
                        return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // 忽略无权访问的目录。
            }

            try
            {
                string? anyExe = Directory.EnumerateFiles(dir, "*.exe")
                    .FirstOrDefault(IsValid);
                if (anyExe is not null)
                    return Path.GetFullPath(anyExe);
            }
            catch
            {
                // 忽略无权访问的目录。
            }
        }

        return null;
    }

    /// <summary>按优先级查找：已配置路径 → 运行中进程 → 常见目录 → Steam 库 → 各盘全盘文件名搜索。</summary>
    public static string? TryFind(string? preferred = null)
    {
        if (IsValid(preferred))
            return Path.GetFullPath(preferred!);

        foreach (string path in FromRunningProcess())
            return path;

        foreach (string path in FromKnownFolders())
            return path;

        foreach (string path in FromSteamLibraries())
            return path;

        foreach (string path in FromDriveScan())
            return path;

        return null;
    }

    public static IEnumerable<string> FromRunningProcess()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch
        {
            yield break;
        }

        foreach (Process process in processes)
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // 无权限读模块路径时跳过。
            }
            finally
            {
                process.Dispose();
            }

            if (IsValid(path))
                yield return Path.GetFullPath(path!);
        }
    }

    public static IEnumerable<string> FromKnownFolders()
    {
        foreach (DriveInfo drive in SafeFixedDrives())
        {
            string root = drive.RootDirectory.FullName;
            foreach (string relative in RelativeHints)
            {
                string path = Path.Combine(root, relative, ExecutableName);
                if (IsValid(path))
                    yield return Path.GetFullPath(path);
            }
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (string dir in new[]
                 {
                     Path.Combine(local, "DMMGamePlayer", "GameContents", ProcessName),
                     Path.Combine(roaming, "dmmgameplayer5", "GameContents", ProcessName)
                 })
        {
            string path = Path.Combine(dir, ExecutableName);
            if (IsValid(path))
                yield return Path.GetFullPath(path);
        }
    }

    public static IEnumerable<string> FromSteamLibraries()
    {
        foreach (string library in EnumerateSteamLibraries())
        {
            string common = Path.Combine(library, "steamapps", "common");
            if (!Directory.Exists(common))
                continue;
            string? hit = null;
            try
            {
                hit = Directory.EnumerateFiles(common, ExecutableName, SearchOption.AllDirectories)
                    .FirstOrDefault(IsValid);
            }
            catch
            {
                // 跳过无权访问的 Steam 子目录。
            }

            if (hit is not null)
                yield return Path.GetFullPath(hit);
        }
    }

    public static IEnumerable<string> FromDriveScan()
    {
        foreach (DriveInfo drive in SafeFixedDrives())
        {
            // C: 全盘太慢，只扫常见游戏根目录。
            if (string.Equals(drive.Name, @"C:\", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string path in ScanRoots(
                             Path.Combine(drive.RootDirectory.FullName, "Games"),
                             Path.Combine(drive.RootDirectory.FullName, "muvluv"),
                             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DMMGamePlayer"),
                             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "DMMGamePlayer")))
                    yield return path;
                continue;
            }

            foreach (string path in ScanRoots(drive.RootDirectory.FullName))
                yield return path;
        }
    }

    private static IEnumerable<string> ScanRoots(params string?[] roots)
    {
        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;
            IEnumerator<string>? enumerator = null;
            try
            {
                enumerator = Directory.EnumerateFiles(root, ExecutableName, SearchOption.AllDirectories)
                    .GetEnumerator();
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = enumerator.MoveNext();
                    }
                    catch
                    {
                        break;
                    }

                    if (!moved)
                        break;
                    if (IsValid(enumerator.Current))
                        yield return Path.GetFullPath(enumerator.Current);
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }
    }

    private static IEnumerable<System.IO.DriveInfo> SafeFixedDrives()
    {
        System.IO.DriveInfo[] drives;
        try
        {
            drives = System.IO.DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (DriveInfo drive in drives)
        {
            bool ready = false;
            try
            {
                ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch
            {
                // 忽略异常盘符。
            }

            if (ready)
                yield return drive;
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        string[] vdfCandidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "libraryfolders.vdf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "libraryfolders.vdf")
        ];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string vdf in vdfCandidates)
        {
            if (!File.Exists(vdf))
                continue;
            string text;
            try
            {
                text = File.ReadAllText(vdf);
            }
            catch
            {
                continue;
            }

            foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                string path = match.Groups[1].Value.Replace(@"\\", @"\");
                if (seen.Add(path))
                    yield return path;
            }
        }
    }
}
