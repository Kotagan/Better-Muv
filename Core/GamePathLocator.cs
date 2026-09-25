using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BetterMuv.Core;

/// <summary>定位 マブラヴ・ガールズガーデンX 客户端 exe。</summary>
public static class GamePathLocator
{
    public const string ProcessName = "muv_luv_girlsgardenx_cl";
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

    public static bool IsValid(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(path) &&
        string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析用户选择的路径（支持 .lnk）。成功时返回游戏 exe 绝对路径；失败返回 null 并给出原因。
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
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string? target = TryResolveShortcut(path);
            if (target is null)
            {
                reason = "无法解析快捷方式：" + path;
                return null;
            }

            path = target;
        }

        if (!File.Exists(path))
        {
            reason = "文件不存在：" + path;
            return null;
        }

        if (!string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            reason =
                $"不是游戏程序（需要 {ExecutableName}，实际为 {Path.GetFileName(path)}）。" +
                "请选游戏目录下的 exe，或指向该 exe 的快捷方式。";
            return null;
        }

        return Path.GetFullPath(path);
    }

    /// <summary>读取 .lnk 目标路径；失败返回 null。</summary>
    public static string? TryResolveShortcut(string shortcutPath)
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
            string? target = shortcut.GetType().InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;
            return string.IsNullOrWhiteSpace(target) ? null : target.Trim().Trim('"');
        }
        catch
        {
            return null;
        }
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

    private static IEnumerable<DriveInfo> SafeFixedDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
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
