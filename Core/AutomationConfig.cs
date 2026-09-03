using System.Text.Json;

namespace BetterMuv.Core;

public sealed class AutomationConfig
{
    public string WindowTitleKeyword { get; set; } = "マブラヴ";
    /// <summary>截图器启动时是否一并启动游戏。</summary>
    public bool LaunchGameWithCapture { get; set; }
    public bool CaptureTaskEnabled { get; set; } = true;
    public bool DesktopCloneTaskEnabled { get; set; }
    public bool MazeTaskEnabled { get; set; } = true;
    public string GameExecutablePath { get; set; } = "";
    public string GameLaunchArguments { get; set; } = "";
    public int GameLaunchTimeoutSeconds { get; set; } = 90;
    public double MatchThreshold { get; set; } = 0.78;
    public bool SaveDiagnostics { get; set; } = false;
    public string DiagnosticDirectory { get; set; } = "diagnostics";
    /// <summary>配置坐标基准分辨率（1080p）。屏幕点击 = Display 原点 + 点 × (Display/Reference)。</summary>
    public int ReferenceWidth { get; set; } = 1920;
    public int ReferenceHeight { get; set; } = 1080;

    // 搜索区：1080p 写死矩形（左上 + 宽高）。主页 ROI 对应 4K (2226,1838)→(2356,1938)。
    public ConfigPoint SearchTopLeft { get; set; } = new(1113, 919);
    public ConfigSize FirstSearchSize { get; set; } = new(65, 50);
    public ConfigPoint SecondSearchTopLeft { get; set; } = new(1501, 444);
    public ConfigSize SecondSearchSize { get; set; } = new(220, 59);
    public ConfigPoint ThirdSearchTopLeft { get; set; } = new(1568, 796);
    public ConfigSize ThirdSearchSize { get; set; } = new(208, 98);
    public ConfigPoint FourthSearchTopLeft { get; set; } = new(1602, 927);
    /// <summary>4K 用户框高 62，但模板 76→逻辑 38，搜索高至少盖住模板。</summary>
    public ConfigSize FourthSearchSize { get; set; } = new(182, 38);
    public ConfigPoint FifthSearchTopLeft { get; set; } = new(1621, 927);
    public ConfigSize FifthSearchSize { get; set; } = new(103, 64);
    public ConfigPoint PartnerSelectionTopLeft { get; set; } = new(1633, 919);
    public ConfigSize PartnerSelectionSize { get; set; } = new(183, 75);
    public ConfigPoint BattleSkipTopLeft { get; set; } = new(1760, 40);
    public ConfigSize BattleSkipSize { get; set; } = new(112, 51);
    public ConfigPoint EventChoiceTopLeft { get; set; } = new(1018, 628);
    public ConfigSize EventChoiceSize { get; set; } = new(814, 367);
    public ConfigPoint EventChoiceFirstOption { get; set; } = new(1300, 700);
    public ConfigPoint EventChoiceSecondOption { get; set; } = new(1300, 825);
    /// <summary>结算「完了」点击坐标（1080p）。</summary>
    public ConfigPoint SettlementTopLeft { get; set; } = new(1674, 948);
    public ConfigPoint SettlementSearchTopLeft { get; set; } = new(1626, 917);
    public ConfigSize SettlementSearchSize { get; set; } = new(97, 62);
    public ConfigPoint SettlementCategoryDaily { get; set; } = new(70, 338);
    public ConfigPoint SettlementCategoryEquipment { get; set; } = new(58, 456);
    public ConfigPoint SettlementCategoryExcavation { get; set; } = new(96, 572);
    public ConfigPoint SettlementCategoryArtifactor { get; set; } = new(67, 674);
    public List<ConfigPoint> SettlementSubcategoryTabs { get; set; } =
    [
        new(416, 200),
        new(625, 200),
        new(840, 200),
        new(1050, 200)
    ];
    public List<ConfigPoint> SettlementBuyButtons { get; set; } =
    [
        new(890, 355),
        new(1630, 355),
        new(890, 545),
        new(1630, 545),
        new(890, 720),
        new(1630, 720)
    ];
    public ConfigPoint SettlementMultiplierToggle { get; set; } = new(1610, 200);
    public ConfigPoint SettlementMultiplierTopLeft { get; set; } = new(1673, 181);
    public ConfigSize SettlementMultiplierSize { get; set; } = new(81, 49);
    public ConfigPoint SettlementBuyButtonSearchInset { get; set; } = new(12, 12);
    public ConfigSize SettlementBuyButtonSearchSize { get; set; } = new(301, 119);
    public ConfigPoint SettlementConfirmTopLeft { get; set; } = new(685, 520);
    public ConfigSize SettlementConfirmSize { get; set; } = new(592, 424);
    public ConfigPoint SettlementConfirmCancel { get; set; } = new(810, 790);
    public ConfigPoint SettlementConfirmOk { get; set; } = new(1115, 790);
    public bool SettlementConfirmLeftover { get; set; } = true;
    public SettlementPurchases SettlementPurchases { get; set; } = new();
    public ConfigPoint TreasureStateTopLeft { get; set; } = new(775, 34);
    public ConfigSize TreasureStateSize { get; set; } = new(376, 82);
    public ConfigPoint TreasureOptionsTopLeft { get; set; } = new(478, 316);
    public ConfigSize TreasureOptionsSize { get; set; } = new(1081, 90);
    public double TreasureMatchThreshold { get; set; } = 0.58;
    public int TreasureMatchRetryCount { get; set; } = 3;
    public int TreasureMatchRetryDelayMs { get; set; } = 150;
    public ConfigPoint TreasureClickOffset { get; set; } = new(-40, 55);
    public ConfigPoint RouteSelectionTopLeft { get; set; } = new(1595, 924);
    public ConfigSize RouteSelectionSize { get; set; } = new(181, 37);
    public ConfigPoint RouteTreasureOptionsTopLeft { get; set; } = new(661, 243);
    public ConfigSize RouteTreasureOptionsSize { get; set; } = new(209, 554);
    public List<string> TreasurePriority { get; set; } = ["diamond", "shield", "sword", "heart", "skull", "sparkle"];
    // 写死点击点（1080p）；实际点击 = Display 原点 + 点 × (Display宽高 / Reference宽高)。
    public ConfigPoint FirstClick { get; set; } = new(1143, 961);
    public ConfigPoint SecondClick { get; set; } = new(1611, 473);
    public ConfigPoint ThirdClick { get; set; } = new(1667, 836);
    public ConfigPoint FourthClick { get; set; } = new(1693, 942);
    public ConfigPoint FifthClick { get; set; } = new(1672, 959);
    public ConfigPoint PartnerClick { get; set; } = new(1724, 956);
    public ConfigPoint BattleSkipClick { get; set; } = new(1816, 65);
    public ConfigPoint RouteClick { get; set; } = new(1680, 940);
    /// <summary>迷宫难度：keep=保持不变，custom=自选数字。</summary>
    public string MazeDifficultyMode { get; set; } = "keep";
    /// <summary>自选难度目标（仅 custom 生效）。</summary>
    public int MazeDifficultyTarget { get; set; } = 1;
    // 4K (2658,652)→(3406,902) → 1080p
    public ConfigPoint DifficultyDigitTopLeft { get; set; } = new(1329, 326);
    public ConfigSize DifficultyDigitSize { get; set; } = new(374, 125);
    // 4K (1792,754) / (3652,754)
    public ConfigPoint DifficultyDecreaseClick { get; set; } = new(896, 377);
    public ConfigPoint DifficultyIncreaseClick { get; set; } = new(1826, 377);
    // 4K (3266,522)
    public ConfigPoint DifficultyOpenSliderClick { get; set; } = new(1633, 261);
    // 4K (1158,584)→(1522,1706)
    public ConfigPoint DifficultyListTopLeft { get; set; } = new(579, 292);
    public ConfigSize DifficultyListSize { get; set; } = new(182, 561);
    // 4K (2250,1860)
    public ConfigPoint DifficultyConfirmClick { get; set; } = new(1125, 930);
    public int DoubleClickIntervalMs { get; set; } = 100;
    public int DetectionPollIntervalMs { get; set; } = 250;
    public int DetectionTimeoutMs { get; set; } = 10000;
    /// <summary>迷宫轮次上限；0 表示无限。</summary>
    public int MazeRunLimit { get; set; }
    /// <summary>自动主线循环次数（至少 1）。</summary>
    public int MainQuestRunLimit { get; set; } = 10;
    public bool MainQuestTaskEnabled { get; set; } = true;
    public bool HardMainQuestTaskEnabled { get; set; }
    /// <summary>一条龙任务顺序，项为 maze / mainQuest / hardMainQuest。</summary>
    public List<string> PipelineTaskOrder { get; set; } = ["maze", "mainQuest", "hardMainQuest"];
    // 主线 ROI（1080p）；模板匹配后点中心。
    public ConfigPoint MainQuestHomeTopLeft { get; set; } = new(1000, 880);
    public ConfigSize MainQuestHomeSize { get; set; } = new(340, 160);
    public ConfigPoint MainQuestBannerTopLeft { get; set; } = new(1050, 380);
    public ConfigSize MainQuestBannerSize { get; set; } = new(520, 240);
    public ConfigPoint MainQuestStartTopLeft { get; set; } = new(1400, 880);
    public ConfigSize MainQuestStartSize { get; set; } = new(480, 160);
    public ConfigPoint MainQuestSortieTopLeft { get; set; } = new(1400, 860);
    public ConfigSize MainQuestSortieSize { get; set; } = new(480, 180);
    public ConfigPoint MainQuestSkipTopLeft { get; set; } = new(1600, 10);
    public ConfigSize MainQuestSkipSize { get; set; } = new(300, 120);
    public ConfigPoint MainQuestNextTopLeft { get; set; } = new(1500, 880);
    public ConfigSize MainQuestNextSize { get; set; } = new(360, 180);
    public ConfigPoint MainQuestRematchTopLeft { get; set; } = new(1480, 900);
    public ConfigSize MainQuestRematchSize { get; set; } = new(360, 120);
    public ConfigPoint MainQuestToHomeTopLeft { get; set; } = new(40, 900);
    public ConfigSize MainQuestToHomeSize { get; set; } = new(400, 120);
    public ConfigPoint HardQuestDifficultyTopLeft { get; set; } = new(900, 20);
    public ConfigSize HardQuestDifficultySize { get; set; } = new(900, 220);
    public ConfigPoint HardQuestBattleTopLeft { get; set; } = new(20, 40);
    public ConfigSize HardQuestBattleSize { get; set; } = new(1100, 480);
    /// <summary>左上角主界面房子按钮搜索区（1080p）。</summary>
    public ConfigPoint HudHomeTopLeft { get; set; } = new(0, 0);
    public ConfigSize HudHomeSize { get; set; } = new(220, 160);
    public string ToggleHotkey { get; set; } = "F10";
    /// <summary>全局暂停/继续快捷键。</summary>
    public string PauseHotkey { get; set; } = "F10";
    /// <summary>全局停止快捷键。</summary>
    public string StopHotkey { get; set; } = "F11";
    /// <summary>是否已确认过首次运行提示弹窗。</summary>
    public bool FirstRunNoticeAccepted { get; set; }
    /// <summary>上次日常已买够配置数量的游戏日（yyyy-MM-dd，每天 4:00 起算新一日）。</summary>
    public string? LastDailyShopDay { get; set; }

    public static AutomationConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到配置文件。", path);
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        AutomationConfig config = JsonSerializer.Deserialize<AutomationConfig>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("配置文件内容为空。");
        config.Validate();
        return config;
    }

    public void Save(string path)
    {
        Validate();
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(WindowTitleKeyword))
            throw new InvalidDataException("windowTitleKeyword 不能为空。");
        if (MatchThreshold is <= 0 or > 1)
            throw new InvalidDataException("matchThreshold 必须在 (0, 1] 内。");
        if (TreasureMatchThreshold is <= 0 or > 1)
            throw new InvalidDataException("treasureMatchThreshold 必须在 (0, 1] 内。");
        if (TreasureMatchRetryCount < 1)
            throw new InvalidDataException("treasureMatchRetryCount 必须大于等于 1。");
        if (TreasureMatchRetryDelayMs < 0)
            throw new InvalidDataException("treasureMatchRetryDelayMs 不能为负。");
        if (ReferenceWidth <= 0 || ReferenceHeight <= 0)
            throw new InvalidDataException("参考分辨率必须大于零。");
        if (SettlementSubcategoryTabs.Count < 4 || SettlementBuyButtons.Count < 6)
            throw new InvalidDataException("结算小类页签至少 4 个，购买格至少 6 个。");
        ConfigSize[] requiredSizes =
        [
            FirstSearchSize, SecondSearchSize, ThirdSearchSize, FourthSearchSize, FifthSearchSize,
            PartnerSelectionSize, BattleSkipSize, EventChoiceSize, SettlementSearchSize,
            SettlementMultiplierSize, SettlementBuyButtonSearchSize, SettlementConfirmSize,
            TreasureStateSize, TreasureOptionsSize, RouteSelectionSize, RouteTreasureOptionsSize,
            DifficultyDigitSize, DifficultyListSize,
            MainQuestHomeSize, MainQuestBannerSize, MainQuestStartSize, MainQuestSortieSize,
            MainQuestSkipSize, MainQuestNextSize, MainQuestRematchSize, MainQuestToHomeSize,
            HardQuestDifficultySize, HardQuestBattleSize, HudHomeSize
        ];
        if (requiredSizes.Any(s => s.Width <= 0 || s.Height <= 0))
            throw new InvalidDataException("所有搜索区域尺寸必须大于零。");
        if (SettlementBuyButtonSearchInset.X < 0 || SettlementBuyButtonSearchInset.Y < 0)
            throw new InvalidDataException("settlementBuyButtonSearchInset 不能为负。");
        if (TreasurePriority.Count == 0)
            throw new InvalidDataException("宝物优先级不能为空。");
        TreasurePriority = NormalizeTreasurePriority(TreasurePriority);
        if (DoubleClickIntervalMs < 0 || DetectionPollIntervalMs <= 0 || DetectionTimeoutMs <= 0)
            throw new InvalidDataException("点击间隔不能为负，检测间隔和超时必须大于零。");
        if (MainQuestRunLimit < 1)
            throw new InvalidDataException("mainQuestRunLimit 必须大于等于 1。");
        PipelineTaskOrder = NormalizePipelineTaskOrder(PipelineTaskOrder).ToList();
        if (MazeRunLimit < 0)
            throw new InvalidDataException("mazeRunLimit 不能为负数（0 表示无限）。");
        MazeDifficultyMode = MazeDifficultyRunner.NormalizeMode(MazeDifficultyMode);
        if (MazeDifficultyTarget < 1 || MazeDifficultyTarget > 999)
            throw new InvalidDataException("mazeDifficultyTarget 必须在 1–999。");
        if (ToggleHotkey is not ("F1" or "F2" or "F3" or "F4" or "F5" or "F6" or "F7" or
                                 "F8" or "F9" or "F10" or "F11" or "F12"))
            throw new InvalidDataException("toggleHotkey 仅支持 F1 至 F12。");
        if (!IsSupportedFunctionKey(PauseHotkey) || !IsSupportedFunctionKey(StopHotkey))
            throw new InvalidDataException("暂停和停止快捷键仅支持 F1 至 F12。");
        if (PauseHotkey.Equals(StopHotkey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("暂停和停止快捷键不能相同。");
        if (GameLaunchTimeoutSeconds is < 5 or > 600)
            throw new InvalidDataException("gameLaunchTimeoutSeconds 必须在 5–600 秒。");
        NormalizeSettlementPurchases();
    }

    public static IReadOnlyList<string> NormalizePipelineTaskOrder(IEnumerable<string>? order)
    {
        string[] known = ["maze", "mainQuest", "hardMainQuest"];
        var result = new List<string>();
        if (order is not null)
        {
            foreach (string raw in order)
            {
                string id = raw.Trim();
                if (known.Contains(id, StringComparer.OrdinalIgnoreCase) &&
                    !result.Contains(id, StringComparer.OrdinalIgnoreCase))
                    result.Add(known.First(item => item.Equals(id, StringComparison.OrdinalIgnoreCase)));
            }
        }

        foreach (string id in known)
        {
            if (!result.Contains(id, StringComparer.OrdinalIgnoreCase))
                result.Add(id);
        }

        return result;
    }

    public static bool IsSupportedFunctionKey(string? key) =>
        key is "F1" or "F2" or "F3" or "F4" or "F5" or "F6" or "F7" or
               "F8" or "F9" or "F10" or "F11" or "F12";

    private static List<string> NormalizeTreasurePriority(IEnumerable<string> priority)
    {
        var result = new List<string>();
        foreach (string raw in priority)
        {
            string key = raw.Equals("greatsword", StringComparison.OrdinalIgnoreCase) ? "sword" : raw;
            if (result.Any(existing => existing.Equals(key, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(key);
        }
        return result;
    }

    private void NormalizeSettlementPurchases()
    {
        SettlementPurchases.Daily.SkillBook1 = NormalizeSlots(SettlementPurchases.Daily.SkillBook1, 6);
        SettlementPurchases.Daily.SkillBook2 = NormalizeSlots(SettlementPurchases.Daily.SkillBook2, 6);
        SettlementPurchases.Daily.Disk = NormalizeSlots(SettlementPurchases.Daily.Disk, 6);
        SettlementPurchases.Daily.Unit = NormalizeSlots(SettlementPurchases.Daily.Unit, 6);
        SettlementPurchases.Equipment.Physics = NormalizeSlots(SettlementPurchases.Equipment.Physics, 4);
        SettlementPurchases.Equipment.En = NormalizeSlots(SettlementPurchases.Equipment.En, 4);
        SettlementPurchases.Equipment.Agility = NormalizeSlots(SettlementPurchases.Equipment.Agility, 4);
        SettlementPurchases.Artifactor.Physics = NormalizeSlots(SettlementPurchases.Artifactor.Physics, 5);
        SettlementPurchases.Artifactor.En = NormalizeSlots(SettlementPurchases.Artifactor.En, 5);
        SettlementPurchases.Artifactor.Agility = NormalizeSlots(SettlementPurchases.Artifactor.Agility, 5);
    }

    private static int[] NormalizeSlots(int[]? values, int length)
    {
        var result = new int[length];
        if (values is null)
            return result;
        Array.Copy(values, result, Math.Min(values.Length, length));
        return result;
    }
}

public sealed record ConfigPoint(int X, int Y);
public sealed record ConfigSize(int Width, int Height);
