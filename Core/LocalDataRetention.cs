namespace BetterMuv.Core;

/// <summary>
/// 启动时清理过期本地日志与诊断截图；静默执行，不写业务日志。
/// </summary>
public static class LocalDataRetention
{
    public const int RetainDays = 3;

    /// <summary>删除超过保留天数的日志文件与诊断目录。失败忽略。</summary>
    public static void CleanupOlderThanDays(
        int retainDays = RetainDays,
        string? logsDirectory = null,
        string? diagnosticRoot = null)
    {
        if (retainDays < 1)
            retainDays = RetainDays;

        DateTime cutoff = DateTime.Now.AddDays(-retainDays);
        try { CleanupOldFiles(logsDirectory ?? ConfigStore.LogsDirectory, "*.log", cutoff); }
        catch { /* 静默 */ }

        string root = diagnosticRoot
            ?? Path.Combine(AppContext.BaseDirectory, "diagnostics");
        try
        {
            if (Directory.Exists(root))
                CleanupOldDirectories(root, cutoff);
        }
        catch { /* 静默 */ }
    }

    private static void CleanupOldFiles(string directory, string searchPattern, DateTime cutoff)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (string file in Directory.EnumerateFiles(directory, searchPattern))
        {
            try
            {
                if (File.GetLastWriteTime(file) >= cutoff)
                    continue;
                File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CleanupOldDirectories(string root, DateTime cutoff)
    {
        foreach (string directory in Directory.EnumerateDirectories(root).ToList())
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (Directory.GetLastWriteTime(directory) >= cutoff)
                    continue;
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (string file in Directory.EnumerateFiles(root, "*.png"))
        {
            try
            {
                if (File.GetLastWriteTime(file) >= cutoff)
                    continue;
                File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
