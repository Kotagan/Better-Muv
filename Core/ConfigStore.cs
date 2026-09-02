namespace BetterMuv.Core;

public static class ConfigStore
{
    public static string UserDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Better-Muv");

    public static string UserConfigPath { get; } = Path.Combine(UserDirectory, "config.json");

    public static string BundledConfigPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static string LogsDirectory { get; } = Path.Combine(UserDirectory, "logs");

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

    /// <summary>按日期滚动的日志文件路径，目录不存在时自动创建。</summary>
    public static string EnsureLogFilePath(DateTime? date = null)
    {
        Directory.CreateDirectory(LogsDirectory);
        DateTime day = date ?? DateTime.Now;
        return Path.Combine(LogsDirectory, $"Better-Muv-{day:yyyyMMdd}.log");
    }

    public static AutomationConfig Load()
    {
        AutomationConfig config = AutomationConfig.Load(EnsureUserConfigPath());
        if (MigrateTreasureRecognitionDefaults(config))
            Save(config);
        return config;
    }

    public static void Save(AutomationConfig config) =>
        config.Save(EnsureUserConfigPath());

    /// <summary>
    /// 将旧版过大的宝物 ROI / 过低阈值迁移到当前默认，避免读到过期 AppData 配置。
    /// </summary>
    private static bool MigrateTreasureRecognitionDefaults(AutomationConfig config)
    {
        bool changed = false;
        if (config.TreasureOptionsSize.Height != 180 ||
            config.TreasureOptionsTopLeft.Y != 632 ||
            config.TreasureOptionsTopLeft.X != 956)
        {
            config.TreasureOptionsTopLeft = new ConfigPoint(956, 632);
            config.TreasureOptionsSize = new ConfigSize(2162, 180);
            changed = true;
        }

        if (Math.Abs(config.TreasureMatchThreshold - 0.58) > 0.001)
        {
            config.TreasureMatchThreshold = 0.58;
            changed = true;
        }

        if (config.TreasureClickOffset is { X: 0, Y: 70 } or { X: -120, Y: 90 })
        {
            config.TreasureClickOffset = new ConfigPoint(-80, 110);
            changed = true;
        }

        if (config.TreasureMatchRetryCount > 3)
        {
            config.TreasureMatchRetryCount = 3;
            changed = true;
        }

        if (config.TreasureMatchRetryDelayMs > 150)
        {
            config.TreasureMatchRetryDelayMs = 150;
            changed = true;
        }

        // 默认优先级迁移：钻石 → 盾 → 剑 → 心 → 骷髅（闪光殿后）。
        string[] desired = ["diamond", "shield", "sword", "heart", "skull", "sparkle"];
        string[][] legacyDefaults =
        [
            ["diamond", "sparkle", "shield", "sword", "heart"],
            ["diamond", "skull", "sword", "sparkle", "shield", "heart"],
            ["diamond", "skull", "sword", "shield", "sparkle", "heart"]
        ];
        bool isLegacy = legacyDefaults.Any(legacy =>
            config.TreasurePriority.SequenceEqual(legacy, StringComparer.OrdinalIgnoreCase));
        if (isLegacy)
        {
            config.TreasurePriority = [.. desired];
            changed = true;
        }
        else if (!config.TreasurePriority.Any(k => k.Equals("skull", StringComparison.OrdinalIgnoreCase)))
        {
            config.TreasurePriority.Add("skull");
            changed = true;
        }

        return changed;
    }
}
