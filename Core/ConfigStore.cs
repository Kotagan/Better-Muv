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

        // 区域选择「選択」：旧 (1125,930) 落在粉钮上方空白，等于没点确定随后被取消。
        if (config.DifficultyConfirmClick.Y < 950 ||
            config.DifficultyConfirmClick is { X: 1125, Y: 930 })
        {
            config.DifficultyConfirmClick = new ConfigPoint(1140, 975);
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

        // 默认优先级迁移：钻石 → 盾 → 剑 → 心 → 骷髅 → 鞋子（闪光殿后）。
        string[] desired = ["diamond", "shield", "sword", "heart", "skull", "shoe", "sparkle"];
        string[][] legacyDefaults =
        [
            ["diamond", "sparkle", "shield", "sword", "heart"],
            ["diamond", "skull", "sword", "sparkle", "shield", "heart"],
            ["diamond", "skull", "sword", "shield", "sparkle", "heart"],
            ["sparkle", "sword", "shield", "diamond", "heart", "skull"],
            ["diamond", "shield", "sword", "heart", "skull", "sparkle"]
        ];
        bool isLegacy = legacyDefaults.Any(legacy =>
            config.TreasurePriority.SequenceEqual(legacy, StringComparer.OrdinalIgnoreCase));
        if (isLegacy)
        {
            config.TreasurePriority = [.. desired];
            changed = true;
        }
        else
        {
            if (!config.TreasurePriority.Any(k => k.Equals("skull", StringComparison.OrdinalIgnoreCase)))
            {
                config.TreasurePriority.Add("skull");
                changed = true;
            }
            if (!config.TreasurePriority.Any(k => k.Equals("shoe", StringComparison.OrdinalIgnoreCase)))
            {
                // 插在闪光前；若无闪光则追加末尾。
                int sparkleIdx = config.TreasurePriority.FindIndex(
                    k => k.Equals("sparkle", StringComparison.OrdinalIgnoreCase));
                if (sparkleIdx >= 0)
                    config.TreasurePriority.Insert(sparkleIdx, "shoe");
                else
                    config.TreasurePriority.Add("shoe");
                changed = true;
            }
        }

        // 配置基准改为 1080p；旧 4K 坐标整表迁移。
        if (config.ReferenceWidth >= 3000 ||
            config.SearchTopLeft is { X: 2230, Y: 1836 } or { X: 2198, Y: 1804 } or { X: 1099, Y: 902 } ||
            config.FourthSearchTopLeft is { X: 3204, Y: 1854 } or { X: 3254, Y: 1856 })
        {
            ApplyFixedSearchRegions(config);
            changed = true;
        }

        // 主页底栏「クエスト」：旧 65×50 小于模板(~103×72)且起点偏右会裁切枪图标。
        if (config.FirstSearchSize.Width < 180 ||
            config.FirstSearchSize.Height < 100 ||
            config.SearchTopLeft.X > 1080 ||
            config.SearchTopLeft.Y > 940)
        {
            config.SearchTopLeft = new ConfigPoint(1000, 910);
            config.FirstSearchSize = new ConfigSize(260, 150);
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

        // 任务页「メイズ探索」：旧 220×59 小于新模板逻辑尺寸，且易被校准偏移扫空。
        if (config.SecondSearchTopLeft is { X: 1501, Y: 444 } ||
            config.SecondSearchSize.Width < 280 ||
            config.SecondSearchSize.Height < 90)
        {
            config.SecondSearchTopLeft = new ConfigPoint(1470, 420);
            config.SecondSearchSize = new ConfigSize(300, 100);
            changed = true;
        }

        // 底栏ホーム粉：旧 ROI (30,900) 偏到屏幕左侧，扫不到真正粉钮（约 612,930）。
        if (config.HomeNavTopLeft is { X: 30, Y: 900 } ||
            config.HomeNavTopLeft.X < 400 ||
            config.HomeNavSize.Width < 180)
        {
            config.HomeNavTopLeft = new ConfigPoint(590, 910);
            config.HomeNavSize = new ConfigSize(220, 140);
            changed = true;
        }

        if (config.QuestNavTopLeft is { X: 1000, Y: 900 } ||
            config.QuestNavSize.Width < 200)
        {
            config.QuestNavTopLeft = new ConfigPoint(1000, 910);
            config.QuestNavSize = new ConfigSize(260, 150);
            changed = true;
        }

        // 结算大类左栏：旧点击点偏上/偏边；2026-09 再标定为选中蓝心。
        if (config.SettlementCategoryDaily is { X: 70, Y: 338 } or { X: 77, Y: 304 } ||
            config.SettlementCategoryEquipment is { X: 58, Y: 456 } or { X: 78, Y: 458 } ||
            config.SettlementCategoryExcavation is { X: 96, Y: 572 } or { X: 80, Y: 586 } ||
            config.SettlementCategoryArtifactor is { X: 67, Y: 674 } or { X: 80, Y: 722 } ||
            config.SettlementCategoryProbeHalfSize.Width < 16 ||
            config.SettlementCategoryProbeHalfSize.Height < 16)
        {
            config.SettlementCategoryDaily = new ConfigPoint(101, 323);
            config.SettlementCategoryEquipment = new ConfigPoint(108, 470);
            config.SettlementCategoryExcavation = new ConfigPoint(100, 594);
            config.SettlementCategoryArtifactor = new ConfigPoint(99, 731);
            config.SettlementCategoryProbeHalfSize = new ConfigSize(24, 22);
            changed = true;
        }

        // 大类模板已收紧，搜索区随之缩小（仅迁移旧 200×160）。
        if (config.SettlementCategorySearchSize is { Width: 200, Height: 160 })
        {
            config.SettlementCategorySearchSize = new ConfigSize(180, 80);
            changed = true;
        }

        // 结算小类顶栏：旧 (416/625/840/1050,200) 偏离实机蓝心。
        if (config.SettlementSubcategoryTabs is [{ X: 416, Y: 200 }, _, _, _] or
            [{ X: 416, Y: 200 }, { X: 625, Y: 200 }, { X: 840, Y: 200 }, { X: 1050, Y: 200 }])
        {
            config.SettlementSubcategoryTabs =
            [
                new(370, 186),
                new(600, 186),
                new(830, 186),
                new(1060, 186)
            ];
            changed = true;
        }

        // LvMAX / 素材不足：对齐实机模板中心（891/1673 列；tip 中心约 962,411）。
        var defaults = new AutomationConfig();
        if (config.SettlementBuyButtons is
            [{ X: 890, Y: 355 }, { X: 1630, Y: 355 }, { X: 890, Y: 545 }, { X: 1630, Y: 545 }, { X: 890, Y: 720 }, { X: 1630, Y: 720 }] or
            [{ X: 893, Y: 354 }, { X: 1672, Y: 354 }, { X: 893, Y: 552 }, { X: 1672, Y: 552 }, { X: 893, Y: 750 }, { X: 1672, Y: 750 }])
        {
            config.SettlementBuyButtons = [.. defaults.SettlementBuyButtons];
            changed = true;
        }

        if (config.SettlementBuyButtonSearchInset is { X: 40, Y: 20 } or { X: 160, Y: 60 } ||
            config.SettlementBuyButtonSearchSize is { Width: 380, Height: 140 })
        {
            config.SettlementBuyButtonSearchInset = defaults.SettlementBuyButtonSearchInset;
            config.SettlementBuyButtonSearchSize = defaults.SettlementBuyButtonSearchSize;
            changed = true;
        }

        if (config.SettlementShopTipTopLeft is { X: 720, Y: 300 } or { X: 780, Y: 340 } ||
            config.SettlementShopTipSize is { Width: 520, Height: 200 } ||
            config.SettlementShopTipSize.Width > 480 ||
            config.SettlementShopTipSize.Height > 160)
        {
            config.SettlementShopTipTopLeft = defaults.SettlementShopTipTopLeft;
            config.SettlementShopTipSize = defaults.SettlementShopTipSize;
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
        config.HomeNavTopLeft = defaults.HomeNavTopLeft;
        config.HomeNavSize = defaults.HomeNavSize;
        config.QuestNavTopLeft = defaults.QuestNavTopLeft;
        config.QuestNavSize = defaults.QuestNavSize;
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
        config.SettlementCategoryProbeHalfSize = defaults.SettlementCategoryProbeHalfSize;
        config.SettlementCategorySearchSize = defaults.SettlementCategorySearchSize;
        config.SettlementSubcategoryTabs = [.. defaults.SettlementSubcategoryTabs];
        config.SettlementPurchaseCountTopLeft = defaults.SettlementPurchaseCountTopLeft;
        config.SettlementPurchaseCountSize = defaults.SettlementPurchaseCountSize;
        config.SettlementCurrencyTopLeft = defaults.SettlementCurrencyTopLeft;
        config.SettlementCurrencySize = defaults.SettlementCurrencySize;
        config.SettlementBuyButtons = [.. defaults.SettlementBuyButtons];
        config.SettlementMultiplierToggle = defaults.SettlementMultiplierToggle;
        config.SettlementMultiplierTopLeft = defaults.SettlementMultiplierTopLeft;
        config.SettlementMultiplierSize = defaults.SettlementMultiplierSize;
        config.SettlementBuyButtonSearchInset = defaults.SettlementBuyButtonSearchInset;
        config.SettlementBuyButtonSearchSize = defaults.SettlementBuyButtonSearchSize;
        config.SettlementShopTipTopLeft = defaults.SettlementShopTipTopLeft;
        config.SettlementShopTipSize = defaults.SettlementShopTipSize;
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
