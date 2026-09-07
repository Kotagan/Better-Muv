using System.Text.RegularExpressions;

namespace BetterMuv.Core;

/// <summary>
/// 一次运行批次（单任务或一条龙）共用 run 目录；批次内各任务分目录保存，互不覆盖。
/// 开始新批次前会删除 diagnostics 下全部子目录。
/// </summary>
public sealed class DiagnosticTaskSession
{
    private static readonly Regex LegacyRootDiagnosticPng = new(
        @"^(?:.+-roi|difficulty-digit)-\d{8}-\d{6}-\d{3}\.png$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string? RunDirectoryPath { get; private set; }
    public string? DirectoryPath { get; private set; }
    public string? TaskKey { get; private set; }

    /// <summary>开始或恢复一个运行批次目录（一条龙/单任务共用）。</summary>
    public string BeginRun(string root, bool resume)
    {
        if (resume && RunDirectoryPath is not null && Directory.Exists(RunDirectoryPath))
            return RunDirectoryPath;

        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("诊断目录不能是目录链接。");

        int removed = CleanupPreviousRuns(root);

        string current = Path.Combine(root, $"run-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(Path.Combine(current, "screenshots"));
        RunDirectoryPath = current;
        DirectoryPath = null;
        TaskKey = null;
        LastCleanupRemovedDirectories = removed;
        return current;
    }

    /// <summary>最近一次非 resume 的 BeginRun 删除的子目录数量。</summary>
    public int LastCleanupRemovedDirectories { get; private set; }

    /// <summary>在当前批次下为具体任务创建子目录；同批次内切换任务不会删除已有截图。</summary>
    public string Begin(string root, string taskKey, bool resume)
    {
        if (RunDirectoryPath is null || !Directory.Exists(RunDirectoryPath))
            BeginRun(root, resume: false);

        if (resume && TaskKey == taskKey && DirectoryPath is not null && Directory.Exists(DirectoryPath))
            return DirectoryPath;

        string safeKey = SanitizeTaskKey(taskKey);
        string current = Path.Combine(
            RunDirectoryPath!,
            $"task-{safeKey}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(current);
        DirectoryPath = current;
        TaskKey = taskKey;
        return current;
    }

    public string ScreenshotsDirectory
    {
        get
        {
            if (RunDirectoryPath is null)
                throw new InvalidOperationException("尚未开始诊断批次。");
            string path = Path.Combine(RunDirectoryPath, "screenshots");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <returns>成功删除的子目录数量。</returns>
    private static int CleanupPreviousRuns(string root)
    {
        int removed = 0;
        // 新批次开始前清空 diagnostics 下全部子目录（上一批次 run / 旧 task 等）。
        foreach (string directory in Directory.EnumerateDirectories(root).ToList())
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;

            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
                continue;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // 目录被占用时尽量删光其中文件后再尝试拆空目录。
            foreach (string file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            TryDeleteEmptyTree(directory);
            if (!Directory.Exists(directory))
                removed++;
        }

        // 兼容旧版直接放在根目录下的诊断图，避免误删用户其他图片。
        foreach (string file in Directory.EnumerateFiles(root, "*.png"))
        {
            if (!LegacyRootDiagnosticPng.IsMatch(Path.GetFileName(file)))
                continue;
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return removed;
    }

    private static void TryDeleteEmptyTree(string directory)
    {
        foreach (string child in Directory.EnumerateDirectories(directory))
            TryDeleteEmptyTree(child);

        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            try { Directory.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string SanitizeTaskKey(string taskKey)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = taskKey.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        string cleaned = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "task" : cleaned;
    }
}
