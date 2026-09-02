namespace BetterMuv.Core;

public static class ConfigStore
{
    public static string UserDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Better-Muv");

    public static string UserConfigPath { get; } = Path.Combine(UserDirectory, "config.json");

    public static string BundledConfigPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>
    /// 使用用户目录中的 config.json；若不存在则从程序目录默认配置复制。
    /// </summary>
    public static string EnsureUserConfigPath()
    {
        Directory.CreateDirectory(UserDirectory);
        if (!File.Exists(UserConfigPath))
        {
            if (!File.Exists(BundledConfigPath))
                throw new FileNotFoundException("找不到默认配置文件。", BundledConfigPath);
            File.Copy(BundledConfigPath, UserConfigPath, overwrite: false);
        }

        return UserConfigPath;
    }

    public static AutomationConfig Load() =>
        AutomationConfig.Load(EnsureUserConfigPath());

    public static void Save(AutomationConfig config) =>
        config.Save(EnsureUserConfigPath());
}
