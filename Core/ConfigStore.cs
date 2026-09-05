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
        // 旧版窄条 ROI 无法容纳新版按钮模板及窗口布局偏移。
        if (config.RouteSelectionSize.Width == 181 && config.RouteSelectionSize.Height == 37)
        {
            config.RouteSelectionTopLeft = new ConfigPoint(1500, 880);
            config.RouteSelectionSize = new ConfigSize(360, 180);
        }
        if (MigrateTreasureRecognitionDefaults(config) || MigrateExecutionDefaults(config))
            Save(config);
        return config;
    }

    private static bool MigrateExecutionDefaults(AutomationConfig config)
    {
        bool changed = false;
        if (!AutomationConfig.IsSupportedFunctionKey(config.PauseHotkey))
        {
            config.PauseHotkey = AutomationConfig.IsSupportedFunctionKey(config.ToggleHotkey)
                ? config.ToggleHotkey : "F10";
            changed = true;
        }
        if (!AutomationConfig.IsSupportedFunctionKey(config.StopHotkey) ||
            config.StopHotkey.Equals(config.PauseHotkey, StringComparison.OrdinalIgnoreCase))
        {
            config.StopHotkey = config.PauseHotkey == "F11" ? "F10" : "F11";
            changed = true;
        }
        if (config.GameLaunchTimeoutSeconds is < 5 or > 600)
        {
            config.GameLaunchTimeoutSeconds = 90;
            changed = true;
        }
        return changed;
    }

    public static void Save(AutomationConfig config) =>
        config.Save(EnsureUserConfigPath());

    /// <summary>
    /// 将旧版过大的宝物 ROI / 过低阈值迁移到当前默认，避免读到过期 AppData 配置。
    /// </summary>
    private static bool MigrateTreasureRecognitionDefaults(AutomationConfig config)
    {
        bool changed = false;

        // PowerShell ConvertTo-Json 等曾把「マブラヴ」写成字面 \uXXXX 或乱码。
        const string expectedTitle = "マブラヴ";
        if (string.IsNullOrWhiteSpace(config.WindowTitleKeyword) ||
            !config.WindowTitleKeyword.Contains('マ'))
        {
            config.WindowTitleKeyword = expectedTitle;
            changed = true;
        }

        // 旧 4K 宝物条 → 1080p 默认。
        if (config.TreasureOptionsTopLeft is { X: 956, Y: 632 } ||
            config.TreasureOptionsSize is { Width: 2162, Height: 180 } ||
            config.TreasureOptionsSize is { Width: 1440, Height: 640 } ||
            config.TreasureClickOffset is { X: -80, Y: 110 } or { X: 0, Y: 70 } or { X: -120, Y: 90 } or { X: 0, Y: 60 })
        {
            config.TreasureOptionsTopLeft = new ConfigPoint(478, 316);
            config.TreasureOptionsSize = new ConfigSize(1081, 90);
            config.TreasureClickOffset = new ConfigPoint(-40, 55);
            changed = true;
        }

        if (Math.Abs(config.TreasureMatchThreshold - 0.58) > 0.001)
        {
            config.TreasureMatchThreshold = 0.58;
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

        // 难度数字 ROI：保持能完整框住三位数的区域；过窄会裁掉末位。
        // 误读 140→100 由 DigitOcrService 形态学区分开口 4 / 斜杠 0 解决，不靠过度收窄 ROI。
        if (config.DifficultyDigitTopLeft is { X: 1200, Y: 340 } &&
            config.DifficultyDigitSize is { Width: 280, Height: 90 })
        {
            config.DifficultyDigitTopLeft = new ConfigPoint(1329, 326);
            config.DifficultyDigitSize = new ConfigSize(374, 125);
            changed = true;
        }

        // 提速：旧轮询 250ms / 双击间隔 100ms 偏慢。
        if (config.DetectionPollIntervalMs > 120)
        {
            config.DetectionPollIntervalMs = 120;
            changed = true;
        }
        if (config.DoubleClickIntervalMs > 50)
        {
            config.DoubleClickIntervalMs = 50;
            changed = true;
        }

        // 默认优先级迁移：钻石 → 盾 → 剑 → 心 → 骷髅（闪光殿后）。
        string[] desired = ["diamond", "shield", "sword", "heart", "skull", "sparkle"];
        string[][] legacyDefaults =
        [
            ["diamond", "sparkle", "shield", "sword", "heart"],
            ["diamond", "skull", "sword", "sparkle", "shield", "heart"],
            ["diamond", "skull", "sword", "shield", "sparkle", "heart"],
            ["sparkle", "sword", "shield", "diamond", "heart", "skull"]
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

        // 配置基准改为 1080p；旧 4K 坐标整表迁移。
        if (config.ReferenceWidth >= 3000 ||
            config.SearchTopLeft is { X: 2230, Y: 1836 } or { X: 2198, Y: 1804 } or { X: 1099, Y: 902 } ||
            config.FourthSearchTopLeft is { X: 3204, Y: 1854 } or { X: 3254, Y: 1856 })
        {
            ApplyFixedSearchRegions(config);
            changed = true;
        }

        // 主页 ROI：4K (2226,1838)→(2356,1938) → 1080p (1113,919) 65×50。
        if (config.SearchTopLeft is not { X: 1113, Y: 919 } ||
            config.FirstSearchSize is not { Width: 65, Height: 50 })
        {
            config.SearchTopLeft = new ConfigPoint(1113, 919);
            config.FirstSearchSize = new ConfigSize(65, 50);
            changed = true;
        }

        // 探索按钮 ROI：需盖住完整粉钮「探索」，旧 182×38 只扫到上沿。
        if (config.FourthSearchSize.Width < 280 || config.FourthSearchSize.Height < 90)
        {
            config.FourthSearchTopLeft = new ConfigPoint(1560, 910);
            config.FourthSearchSize = new ConfigSize(320, 120);
            config.FourthClick = new ConfigPoint(1700, 960);
            changed = true;
        }

        return changed;
    }

    private static void ApplyFixedSearchRegions(AutomationConfig config)
    {
        var defaults = new AutomationConfig();
        config.ReferenceWidth = defaults.ReferenceWidth;
        config.ReferenceHeight = defaults.ReferenceHeight;
        config.SearchTopLeft = defaults.SearchTopLeft;
        config.FirstSearchSize = defaults.FirstSearchSize;
        config.SecondSearchTopLeft = defaults.SecondSearchTopLeft;
        config.SecondSearchSize = defaults.SecondSearchSize;
        config.ThirdSearchTopLeft = defaults.ThirdSearchTopLeft;
        config.ThirdSearchSize = defaults.ThirdSearchSize;
        config.FourthSearchTopLeft = defaults.FourthSearchTopLeft;
        config.FourthSearchSize = defaults.FourthSearchSize;
        config.FifthSearchTopLeft = defaults.FifthSearchTopLeft;
        config.FifthSearchSize = defaults.FifthSearchSize;
        config.PartnerSelectionTopLeft = defaults.PartnerSelectionTopLeft;
        config.PartnerSelectionSize = defaults.PartnerSelectionSize;
        config.BattleSkipTopLeft = defaults.BattleSkipTopLeft;
        config.BattleSkipSize = defaults.BattleSkipSize;
        config.EventChoiceTopLeft = defaults.EventChoiceTopLeft;
        config.EventChoiceSize = defaults.EventChoiceSize;
        config.EventChoiceFirstOption = defaults.EventChoiceFirstOption;
        config.EventChoiceSecondOption = defaults.EventChoiceSecondOption;
        config.SettlementTopLeft = defaults.SettlementTopLeft;
        config.SettlementSearchTopLeft = defaults.SettlementSearchTopLeft;
        config.SettlementSearchSize = defaults.SettlementSearchSize;
        config.SettlementCategoryDaily = defaults.SettlementCategoryDaily;
        config.SettlementCategoryEquipment = defaults.SettlementCategoryEquipment;
        config.SettlementCategoryExcavation = defaults.SettlementCategoryExcavation;
        config.SettlementCategoryArtifactor = defaults.SettlementCategoryArtifactor;
        config.SettlementSubcategoryTabs = [.. defaults.SettlementSubcategoryTabs];
        config.SettlementBuyButtons = [.. defaults.SettlementBuyButtons];
        config.SettlementMultiplierToggle = defaults.SettlementMultiplierToggle;
        config.SettlementMultiplierTopLeft = defaults.SettlementMultiplierTopLeft;
        config.SettlementMultiplierSize = defaults.SettlementMultiplierSize;
        config.SettlementBuyButtonSearchInset = defaults.SettlementBuyButtonSearchInset;
        config.SettlementBuyButtonSearchSize = defaults.SettlementBuyButtonSearchSize;
        config.SettlementConfirmTopLeft = defaults.SettlementConfirmTopLeft;
        config.SettlementConfirmSize = defaults.SettlementConfirmSize;
        config.SettlementConfirmCancel = defaults.SettlementConfirmCancel;
        config.SettlementConfirmOk = defaults.SettlementConfirmOk;
        config.TreasureStateTopLeft = defaults.TreasureStateTopLeft;
        config.TreasureStateSize = defaults.TreasureStateSize;
        config.TreasureOptionsTopLeft = defaults.TreasureOptionsTopLeft;
        config.TreasureOptionsSize = defaults.TreasureOptionsSize;
        config.TreasureClickOffset = defaults.TreasureClickOffset;
        config.RouteSelectionTopLeft = defaults.RouteSelectionTopLeft;
        config.RouteSelectionSize = defaults.RouteSelectionSize;
        config.RouteTreasureOptionsTopLeft = defaults.RouteTreasureOptionsTopLeft;
        config.RouteTreasureOptionsSize = defaults.RouteTreasureOptionsSize;
        config.FirstClick = defaults.FirstClick;
        config.SecondClick = defaults.SecondClick;
        config.ThirdClick = defaults.ThirdClick;
        config.FourthClick = defaults.FourthClick;
        config.FifthClick = defaults.FifthClick;
        config.PartnerClick = defaults.PartnerClick;
        config.BattleSkipClick = defaults.BattleSkipClick;
        config.RouteClick = defaults.RouteClick;
        config.DifficultyDigitTopLeft = defaults.DifficultyDigitTopLeft;
        config.DifficultyDigitSize = defaults.DifficultyDigitSize;
        config.DifficultyDecreaseClick = defaults.DifficultyDecreaseClick;
        config.DifficultyIncreaseClick = defaults.DifficultyIncreaseClick;
        config.DifficultyOpenSliderClick = defaults.DifficultyOpenSliderClick;
        config.DifficultyListTopLeft = defaults.DifficultyListTopLeft;
        config.DifficultyListSize = defaults.DifficultyListSize;
        config.DifficultyConfirmClick = defaults.DifficultyConfirmClick;
    }
}
