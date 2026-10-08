using System.Diagnostics;
using System.Reflection;
using BetterMuv.Core;

namespace BetterMuv.Services;

public enum UpdateCheckKind
{
    /// <summary>已是最新，或远程不比当前新。</summary>
    UpToDate,
    /// <summary>有新版本且应提示用户。</summary>
    Available,
    /// <summary>有新版本，但用户已忽略当前 Major.Minor 版本线。</summary>
    SuppressedByIgnore
}

public sealed record UpdateCheckResult(
    UpdateCheckKind Kind,
    AppVersion Current,
    GitHubRelease? Latest,
    string Message);

/// <summary>检查 / 下载 GitHub Release，并持久化「忽略当前 1.3.x 线」。</summary>
public sealed class AppUpdateService
{
    private readonly GitHubReleaseClient _client;

    public AppUpdateService(GitHubReleaseClient? client = null)
    {
        _client = client ?? new GitHubReleaseClient();
    }

    public static AppVersion GetCurrentVersion()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string? informational =
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (AppVersion.TryParse(informational, out AppVersion fromInfo))
            return fromInfo;

        Version? v = asm.GetName().Version;
        if (v is not null)
            return new AppVersion(v.Major, v.Minor, Math.Max(0, v.Build));
        return new AppVersion(0, 0, 0);
    }

    public static string FormatLine(int major, int minor) => $"{major}.{minor}.x";

    public static string? GetIgnoredLineLabel(AutomationConfig config) =>
        config.IgnoredUpdateMajor is int major && config.IgnoredUpdateMinor is int minor
            ? FormatLine(major, minor)
            : null;

    /// <param name="respectIgnore">
    /// true：启动时静默检查，已忽略的版本线返回 <see cref="UpdateCheckKind.SuppressedByIgnore"/>；
    /// false：设置页手动检查，仍报告有更新（由 UI 决定是否提示）。
    /// </param>
    public async Task<UpdateCheckResult> CheckAsync(
        bool respectIgnore = true,
        CancellationToken cancellationToken = default)
    {
        AppVersion current = GetCurrentVersion();
        GitHubRelease latest = await _client.FetchLatestAsync(cancellationToken);
        if (latest.Version <= current)
        {
            return new UpdateCheckResult(
                UpdateCheckKind.UpToDate,
                current,
                latest,
                $"已是最新版本（当前 {current}，远程 {latest.Version}）。");
        }

        AutomationConfig config = ConfigStore.Load();
        string? ignoredLine = GetIgnoredLineLabel(config);
        if (respectIgnore && IsSuppressed(config, latest.Version))
        {
            return new UpdateCheckResult(
                UpdateCheckKind.SuppressedByIgnore,
                current,
                latest,
                $"发现 {latest.Version}，但已忽略 {ignoredLine}，直到更高小版本再提示。");
        }

        string message = IsSuppressed(config, latest.Version)
            ? $"发现新版本 {latest.Version}（当前 {current}；此前已忽略 {ignoredLine}）。"
            : $"发现新版本 {latest.Version}（当前 {current}）。";
        return new UpdateCheckResult(UpdateCheckKind.Available, current, latest, message);
    }

    /// <summary>忽略当前 Major.Minor 线（如 1.3.x），直到出现更高 Minor 或 Major。</summary>
    public static void IgnoreVersionLine(AppVersion remoteVersion)
    {
        AutomationConfig config = ConfigStore.Load();
        config.IgnoredUpdateMajor = remoteVersion.Major;
        config.IgnoredUpdateMinor = remoteVersion.Minor;
        ConfigStore.Save(config);
    }

    public static void ClearIgnoredLine()
    {
        AutomationConfig config = ConfigStore.Load();
        if (config.IgnoredUpdateMajor is null && config.IgnoredUpdateMinor is null)
            return;
        config.IgnoredUpdateMajor = null;
        config.IgnoredUpdateMinor = null;
        ConfigStore.Save(config);
    }

    /// <summary>
    /// 仅压制同一 Major.Minor 线（1.3.x）；1.4.0 / 2.0.0 会重新提示。
    /// 旧配置若只有 Major、没有 Minor，不压制（避免误伤整条 1.x）。
    /// </summary>
    public static bool IsSuppressed(AutomationConfig config, AppVersion remoteVersion) =>
        config.IgnoredUpdateMajor is int major &&
        config.IgnoredUpdateMinor is int minor &&
        remoteVersion.Major == major &&
        remoteVersion.Minor == minor;

    public async Task<string> DownloadSetupAsync(
        GitHubRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (release.SetupAsset is null)
            throw new InvalidOperationException("该 Release 没有可下载的安装包（Better-Muv-Setup-*.exe）。");

        string dir = Path.Combine(Path.GetTempPath(), "Better-Muv-updates");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, release.SetupAsset.Name);
        await _client.DownloadAsync(release.SetupAsset.DownloadUrl, path, progress, cancellationToken);
        return path;
    }

    /// <summary>启动安装包并退出当前进程（避免文件占用导致覆盖失败）。</summary>
    public static void LaunchInstallerAndExit(string setupPath)
    {
        if (!File.Exists(setupPath))
            throw new FileNotFoundException("安装包不存在。", setupPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            UseShellExecute = true
        });
        System.Windows.Application.Current?.Shutdown();
    }
}
