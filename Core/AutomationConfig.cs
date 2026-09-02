using System.Text.Json;

namespace BetterMuv.Core;

public sealed class AutomationConfig
{
    public string WindowTitleKeyword { get; set; } = "マブラヴ";
    public double MatchThreshold { get; set; } = 0.78;
    public bool SaveDiagnostics { get; set; } = true;
    public string DiagnosticDirectory { get; set; } = "diagnostics";
    public int ReferenceWidth { get; set; } = 3840;
    public int ReferenceHeight { get; set; } = 2160;
    public ConfigPoint SearchTopLeft { get; set; } = new(2240, 1844);
    public int FirstSearchPadding { get; set; } = 32;
    public ConfigPoint SecondSearchTopLeft { get; set; } = new(3026, 912);
    public int SecondSearchPadding { get; set; } = 24;
    public ConfigPoint ThirdSearchTopLeft { get; set; } = new(3200, 1657);
    public int ThirdSearchPadding { get; set; } = 64;
    public ConfigPoint FourthSearchTopLeft { get; set; } = new(3296, 1900);
    public int FourthSearchPadding { get; set; } = 32;
    public ConfigPoint FifthSearchTopLeft { get; set; } = new(3274, 1886);
    public int FifthSearchPadding { get; set; } = 32;
    public ConfigPoint PartnerSelectionTopLeft { get; set; } = new(3314, 1886);
    public int PartnerSelectionPadding { get; set; } = 32;
    public ConfigPoint BattleSkipTopLeft { get; set; } = new(3552, 112);
    public int BattleSkipPadding { get; set; } = 32;
    public ConfigPoint EventChoiceTopLeft { get; set; } = new(2100, 1320);
    public int EventChoicePadding { get; set; } = 64;
    public ConfigPoint EventChoiceFirstOption { get; set; } = new(2600, 1400);
    public ConfigPoint EventChoiceSecondOption { get; set; } = new(2600, 1650);
    public ConfigPoint SettlementTopLeft { get; set; } = new(3284, 1866);
    public int SettlementPadding { get; set; } = 32;
    public ConfigPoint SettlementCategoryDaily { get; set; } = new(140, 676);
    public ConfigPoint SettlementCategoryEquipment { get; set; } = new(116, 912);
    public ConfigPoint SettlementCategoryExcavation { get; set; } = new(192, 1144);
    public ConfigPoint SettlementCategoryArtifactor { get; set; } = new(134, 1348);
    public List<ConfigPoint> SettlementSubcategoryTabs { get; set; } =
    [
        new(832, 400),
        new(1250, 400),
        new(1680, 400),
        new(2100, 400)
    ];
    public List<ConfigPoint> SettlementBuyButtons { get; set; } =
    [
        new(1780, 710),
        new(3260, 710),
        new(1780, 1090),
        new(3260, 1090),
        new(1780, 1440),
        new(3260, 1440)
    ];
    public ConfigPoint SettlementMultiplierToggle { get; set; } = new(3220, 400);
    public ConfigPoint SettlementMultiplierTopLeft { get; set; } = new(3370, 386);
    public int SettlementMultiplierPadding { get; set; } = 24;
    public int SettlementBuyButtonPadding { get; set; } = 24;
    public ConfigPoint SettlementConfirmTopLeft { get; set; } = new(1450, 1120);
    public int SettlementConfirmPadding { get; set; } = 80;
    public ConfigPoint SettlementConfirmCancel { get; set; } = new(1620, 1580);
    public ConfigPoint SettlementConfirmOk { get; set; } = new(2230, 1580);
    public bool SettlementConfirmLeftover { get; set; } = true;
    public SettlementPurchases SettlementPurchases { get; set; } = new();
    public ConfigPoint TreasureStateTopLeft { get; set; } = new(1582, 100);
    public int TreasureStatePadding { get; set; } = 32;
    // 原用户标定：956,632 → 3118,792（高约 160）；略放宽避免裁切图标。
    public ConfigPoint TreasureOptionsTopLeft { get; set; } = new(956, 632);
    public ConfigSize TreasureOptionsSize { get; set; } = new(2162, 180);
    // 真图标实机常见 ≥0.80；过低易假阳，过高（0.62）会漏掉 0.61 档。
    public double TreasureMatchThreshold { get; set; } = 0.58;
    public int TreasureMatchRetryCount { get; set; } = 3;
    public int TreasureMatchRetryDelayMs { get; set; } = 150;
    // 相对图标中心略向左下，点在卡片可点区域。
    public ConfigPoint TreasureClickOffset { get; set; } = new(-80, 110);
    public ConfigPoint RouteSelectionTopLeft { get; set; } = new(3190, 1848);
    public int RouteSelectionPadding { get; set; } = 80;
    public ConfigPoint RouteTreasureOptionsTopLeft { get; set; } = new(1322, 486);
    public ConfigSize RouteTreasureOptionsSize { get; set; } = new(418, 1108);
    public List<string> TreasurePriority { get; set; } = ["diamond", "shield", "sword", "heart", "skull", "sparkle"];
    public ConfigPoint FirstClick { get; set; } = new(2296, 1930);
    public int DoubleClickIntervalMs { get; set; } = 100;
    public int DetectionPollIntervalMs { get; set; } = 250;
    public int DetectionTimeoutMs { get; set; } = 10000;
    /// <summary>迷宫轮次上限；0 表示无限。</summary>
    public int MazeRunLimit { get; set; }
    public string ToggleHotkey { get; set; } = "F10";

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
        if (FirstSearchPadding < 0 || SecondSearchPadding < 0 ||
            ThirdSearchPadding < 0 || FourthSearchPadding < 0 ||
            FifthSearchPadding < 0 || PartnerSelectionPadding < 0 || BattleSkipPadding < 0 ||
            EventChoicePadding < 0 || SettlementPadding < 0 || SettlementMultiplierPadding < 0 ||
            SettlementBuyButtonPadding < 0 || SettlementConfirmPadding < 0 ||
            TreasureStatePadding < 0 || RouteSelectionPadding < 0)
            throw new InvalidDataException("模板搜索余量不能为负数。");
        if (SettlementSubcategoryTabs.Count < 4 || SettlementBuyButtons.Count < 6)
            throw new InvalidDataException("结算小类页签至少 4 个，购买格至少 6 个。");
        if (TreasureOptionsSize.Width <= 0 || TreasureOptionsSize.Height <= 0 ||
            RouteTreasureOptionsSize.Width <= 0 || RouteTreasureOptionsSize.Height <= 0)
            throw new InvalidDataException("宝物选项区域和路线宝物区域尺寸必须大于零。");
        if (TreasurePriority.Count == 0)
            throw new InvalidDataException("宝物优先级不能为空。");
        TreasurePriority = NormalizeTreasurePriority(TreasurePriority);
        if (DoubleClickIntervalMs < 0 || DetectionPollIntervalMs <= 0 || DetectionTimeoutMs <= 0)
            throw new InvalidDataException("点击间隔不能为负，检测间隔和超时必须大于零。");
        if (MazeRunLimit < 0)
            throw new InvalidDataException("mazeRunLimit 不能为负数（0 表示无限）。");
        if (ToggleHotkey is not ("F1" or "F2" or "F3" or "F4" or "F5" or "F6" or "F7" or
                                 "F8" or "F9" or "F10" or "F11" or "F12"))
            throw new InvalidDataException("toggleHotkey 仅支持 F1 至 F12。");
        NormalizeSettlementPurchases();
    }

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
