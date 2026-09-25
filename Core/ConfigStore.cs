namespace BetterMuv.Core;

public static class ConfigStore
{
    public static string UserDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Better-Muv");

    public static string UserConfigPath { get; } = Path.Combine(UserDirectory, "config.json");

    public static string BundledConfigPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static string LogsDirectory { get; } = Path.Combine(UserDirectory, "logs");

    public static string ExportsDirectory { get; } = Path.Combine(UserDirectory, "exports");

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
        if (MigrateTreasureRecognitionDefaults(config) || MigrateExecutionDefaults(config) ||
            MigrateDiagnosticDirectory(config))
            Save(config);
        return config;
    }

    /// <summary>
    /// 诊断根目录（相对名或绝对路径）。会剥掉误写入的 run-/task- 嵌套。
    /// </summary>
    public static string CanonicalDiagnosticDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return "diagnostics";

        string value = configured.Trim();
        if (!Path.IsPathRooted(value))
        {
            int runRel = IndexOfPathSegment(value, "run-");
            if (runRel > 0)
                value = value[..runRel].TrimEnd('\\', '/');
            return string.IsNullOrWhiteSpace(value) ? "diagnostics" : value;
        }

        string full = Path.GetFullPath(value);
        string diagToken = $"{Path.DirectorySeparatorChar}diagnostics{Path.DirectorySeparatorChar}";
        int diagIdx = full.IndexOf(diagToken, StringComparison.OrdinalIgnoreCase);
        if (diagIdx >= 0)
            return full[..(diagIdx + "diagnostics".Length + 1)];

        string diagEnd = $"{Path.DirectorySeparatorChar}diagnostics";
        if (full.EndsWith(diagEnd, StringComparison.OrdinalIgnoreCase))
            return full;

        int runIdx = full.IndexOf($"{Path.DirectorySeparatorChar}run-", StringComparison.OrdinalIgnoreCase);
        if (runIdx > 0)
            return full[..runIdx];

        return full;
    }

    /// <summary>解析为绝对诊断根目录。</summary>
    public static string ResolveDiagnosticRoot(string? configured)
    {
        string canonical = CanonicalDiagnosticDirectory(configured);
        return Path.IsPathRooted(canonical)
            ? Path.GetFullPath(canonical)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, canonical));
    }

    private static int IndexOfPathSegment(string path, string prefix)
    {
        string normalized = path.Replace('/', Path.DirectorySeparatorChar);
        string token = Path.DirectorySeparatorChar + prefix;
        int idx = normalized.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return idx;
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return 0;
        return -1;
    }

    private static bool MigrateDiagnosticDirectory(AutomationConfig config)
    {
        string canonical = CanonicalDiagnosticDirectory(config.DiagnosticDirectory);
        string preferred = canonical;
        try
        {
            string absolute = ResolveDiagnosticRoot(canonical);
            string defaultRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "diagnostics"));
            if (string.Equals(absolute, defaultRoot, StringComparison.OrdinalIgnoreCase))
                preferred = "diagnostics";
        }
        catch
        {
            /* 保持 canonical */
        }

        if (string.Equals(preferred, config.DiagnosticDirectory, StringComparison.OrdinalIgnoreCase))
            return false;
        config.DiagnosticDirectory = preferred;
        return true;
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
        // 余矿 OK/取消：旧默认偏上，按 4K 实机截图校正到按钮中心。
        if (config.SettlementConfirmOk is { X: 1115, Y: 790 })
        {
            config.SettlementConfirmOk = new ConfigPoint(1130, 820);
            changed = true;
        }
        if (config.SettlementConfirmCancel is { X: 810, Y: 790 })
        {
            config.SettlementConfirmCancel = new ConfigPoint(809, 820);
            changed = true;
        }
        if (config.SettlementConfirmTopLeft is { X: 685, Y: 520 } &&
            config.SettlementConfirmSize is { Width: 592, Height: 424 })
        {
            config.SettlementConfirmTopLeft = new ConfigPoint(650, 500);
            config.SettlementConfirmSize = new ConfigSize(680, 450);
            changed = true;
        }
        return changed;
    }

    public static void Save(AutomationConfig config)
    {
        // 运行中 DiagnosticDirectory 可能被设为 task 子目录；落盘只保留根路径，避免嵌套污染。
        string runtime = config.DiagnosticDirectory;
        config.DiagnosticDirectory = CanonicalDiagnosticDirectory(runtime);
        try
        {
            config.Save(EnsureUserConfigPath());
        }
        finally
        {
            config.DiagnosticDirectory = runtime;
        }
    }

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

        // 难度数字 ROI：须对齐 DigitTemplateReader（归一化 840×300 → 1080p 420×150）。
        // 旧 374×125 / 1329,326 在 1080p 上读不出；1200,340 更旧。
        if (config.DifficultyDigitTopLeft is { X: 1200, Y: 340 } or { X: 1329, Y: 326 } ||
            config.DifficultyDigitSize.Width < 400 ||
            config.DifficultyDigitSize.Height < 140)
        {
            config.DifficultyDigitTopLeft = new ConfigPoint(1320, 310);
            config.DifficultyDigitSize = new ConfigSize(420, 150);
            changed = true;
        }

        // 区域选择「選択」：旧 (1125,930) 落在粉钮上方空白，等于没点确定随后被取消。
        if (config.DifficultyConfirmClick.Y < 950 ||
            config.DifficultyConfirmClick is { X: 1125, Y: 930 })
        {
            config.DifficultyConfirmClick = new ConfigPoint(1140, 975);
            changed = true;
        }

        // 识别轮询：旧默认 120ms 偏慢；统一收到 50ms。双击过短仍抬到更稳节奏。
        if (config.DetectionPollIntervalMs != 50)
        {
            config.DetectionPollIntervalMs = 50;
            changed = true;
        }
        if (config.DoubleClickIntervalMs < 150)
        {
            config.DoubleClickIntervalMs = 180;
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

        // 主页「クエスト」改用文字紧裁模板；旧 POI 对枪图标过紧，跨 4K/1080p 时容易掉分。
        if (config.SearchTopLeft is { X: 1091, Y: 957 } or { X: 1122, Y: 961 } or { X: 1000, Y: 910 } ||
            config.FirstSearchSize is { Width: 126, Height: 95 } or { Width: 120, Height: 90 } or { Width: 260, Height: 150 })
        {
            config.SearchTopLeft = new ConfigPoint(1080, 950);
            config.FirstSearchSize = new ConfigSize(150, 110);
            config.QuestNavTopLeft = new ConfigPoint(1080, 950);
            config.QuestNavSize = new ConfigSize(150, 110);
            changed = true;
        }

        // 探索按钮 ROI：需盖住完整粉钮「探索」。
        // 旧 182×38 / 1560,910 320×120 在 1080p 只擦到上沿，匹配约 0.25。
        if (config.FourthSearchTopLeft is { X: 1560, Y: 910 } or { X: 1602, Y: 927 } ||
            config.FourthSearchSize.Width < 360 ||
            config.FourthSearchSize.Height < 130 ||
            config.FourthClick.Y < 1000)
        {
            config.FourthSearchTopLeft = new ConfigPoint(1540, 940);
            config.FourthSearchSize = new ConfigSize(380, 140);
            config.FourthClick = new ConfigPoint(1730, 1015);
            changed = true;
        }

        // 战斗 SKIP：旧 ROI 偏上只擦到钮上沿（分数 ~0.23）；下移并加大盖住完整「SKIP」。
        if (config.BattleSkipTopLeft is { X: 1760, Y: 40 } or { X: 1720, Y: 20 } or { X: 1700, Y: 50 } ||
            config.BattleSkipSize.Height < 70 ||
            config.BattleSkipSize.Width < 160 ||
            config.BattleSkipClick is { X: 1816, Y: 65 } or { X: 1825, Y: 88 })
        {
            config.BattleSkipTopLeft = new ConfigPoint(1720, 70);
            config.BattleSkipSize = new ConfigSize(180, 70);
            config.BattleSkipClick = new ConfigPoint(1805, 100);
            changed = true;
        }

        // 遗物选择标题 ROI：旧 775,34 376×82 裁掉「レ」侧，1080p 分数约 0.11。
        if (config.TreasureStateTopLeft is { X: 775, Y: 34 } ||
            config.TreasureStateSize.Width < 480 ||
            config.TreasureStateSize.Height < 100)
        {
            config.TreasureStateTopLeft = new ConfigPoint(700, 15);
            config.TreasureStateSize = new ConfigSize(520, 120);
            changed = true;
        }

        // 伙伴/助っ人「立ち去る」：旧 1633,919 183×75 偏右上，类型选择页约 0.09。
        if (config.PartnerSelectionTopLeft is { X: 1633, Y: 919 } ||
            config.PartnerSelectionSize.Width < 300 ||
            config.PartnerSelectionSize.Height < 110 ||
            config.PartnerClick is { X: 1724, Y: 956 })
        {
            config.PartnerSelectionTopLeft = new ConfigPoint(1540, 930);
            config.PartnerSelectionSize = new ConfigSize(360, 130);
            config.PartnerClick = new ConfigPoint(1655, 990);
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

        // 主线出击钮：旧 ROI (1400,860) 偏上，匹配点易落在粉钮上方白区。
        if (config.MainQuestSortieTopLeft is { X: 1400, Y: 860 } ||
            config.MainQuestSortieTopLeft.Y < 920 ||
            config.MainQuestSortieSize.Height > 150)
        {
            config.MainQuestSortieTopLeft = new ConfigPoint(1550, 960);
            config.MainQuestSortieSize = new ConfigSize(400, 120);
            changed = true;
        }

        // 困难「難易度変更」：旧 ROI (900,20) 在屏幕顶部，实际在关卡开始钮上方。
        if (config.HardQuestDifficultyTopLeft is { X: 900, Y: 20 } ||
            config.HardQuestDifficultyTopLeft.Y < 700 ||
            config.HardQuestDifficultySize.Width > 500)
        {
            config.HardQuestDifficultyTopLeft = new ConfigPoint(1600, 780);
            config.HardQuestDifficultySize = new ConfigSize(300, 100);
            changed = true;
        }

        // 困难红底 MAIN QUEST BATTLE：旧 ROI 在顶部或 y=740 偏窄，实机横幅约 (40,700)。
        if (config.HardQuestBattleTopLeft is { X: 20, Y: 40 } or { X: 40, Y: 740 } ||
            config.HardQuestBattleTopLeft.Y < 650 ||
            config.HardQuestBattleSize.Height > 200 ||
            config.HardQuestBattleSize.Width < 450)
        {
            config.HardQuestBattleTopLeft = new ConfigPoint(40, 700);
            config.HardQuestBattleSize = new ConfigSize(500, 120);
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
        config.EventRestTopLeft = defaults.EventRestTopLeft;
        config.EventRestSize = defaults.EventRestSize;
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
        config.SettlementConfirmOkOffset = defaults.SettlementConfirmOkOffset;
        config.SettlementConfirmCancelOffset = defaults.SettlementConfirmCancelOffset;
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
