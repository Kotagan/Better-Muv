namespace BetterMuv.Core;

/// <summary>
/// 一次运行批次（单任务或一条龙）共用 run 目录；批次内各任务分目录保存，互不覆盖。
/// 过期清理由 <see cref="LocalDataRetention"/> 在启动时按天数处理。
/// </summary>
public sealed class DiagnosticTaskSession
{
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

        string current = Path.Combine(root, $"run-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(Path.Combine(current, "screenshots"));
        RunDirectoryPath = current;
        DirectoryPath = null;
        TaskKey = null;
        return current;
    }

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

    private static string SanitizeTaskKey(string taskKey)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = taskKey.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        string cleaned = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "task" : cleaned;
    }
}
