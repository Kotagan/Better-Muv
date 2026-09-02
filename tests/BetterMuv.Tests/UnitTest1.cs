using BetterMuv.Core;
using BetterMuv.Services;

namespace BetterMuv.Tests;

public class CaptureGeometryTests
{
    [Fact]
    public void ReferenceCoordinatesMapToExpected1080Points()
    {
        var geometry = new CaptureGeometry(new ScreenRect(0, 0, 1920, 1080));

        var first = geometry.ReferenceToScreen(new ConfigPoint(2296, 1930), 3840, 2160);
        var second = geometry.ReferenceToScreen(new ConfigPoint(3312, 694), 3840, 2160);
        var third = geometry.ReferenceToScreen(new ConfigPoint(3344, 1692), 3840, 2160);

        Assert.Equal(1148, first.X);
        Assert.Equal(965, first.Y);
        Assert.Equal(1656, second.X);
        Assert.Equal(347, second.Y);
        Assert.Equal(1672, third.X);
        Assert.Equal(846, third.Y);
    }

    [Theory]
    [InlineData(1920, 1080, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(1920, 1200, false)]
    public void AspectRatioValidationIsStrict(int width, int height, bool expected)
    {
        Assert.Equal(expected, CaptureGeometry.CheckSixteenByNine(width, height));
    }

    [Fact]
    public void SearchRegionMapsFrom4kTo1080()
    {
        var geometry = new CaptureGeometry(new ScreenRect(100, 50, 1920, 1080));

        ScreenRect result = geometry.ReferenceRegionToScreen(
            new ConfigPoint(2296, 1930), new ConfigSize(500, 300), 3840, 2160);

        Assert.Equal(new ScreenRect(1123, 940, 250, 150), result);
    }

    [Fact]
    public void TopLeftSearchRegionMapsFrom4kTo1080()
    {
        var geometry = new CaptureGeometry(new ScreenRect(100, 50, 1920, 1080));

        ScreenRect result = geometry.ReferenceRegionFromTopLeftToScreen(
            new ConfigPoint(2240, 1844), new ConfigSize(226, 136), 3840, 2160);

        Assert.Equal(new ScreenRect(1220, 972, 113, 68), result);
    }
}

public class AutomationConfigTests
{
    [Fact]
    public void RouteSelectionDefaultsUseRequested4kRegionsAndPriority()
    {
        var config = new AutomationConfig();

        Assert.Equal(new ConfigPoint(3190, 1848), config.RouteSelectionTopLeft);
        Assert.Equal(new ConfigPoint(1322, 486), config.RouteTreasureOptionsTopLeft);
        Assert.Equal(new ConfigSize(418, 1108), config.RouteTreasureOptionsSize);
        Assert.Equal(
            ["diamond", "sparkle", "shield", "sword", "heart"],
            config.TreasurePriority);
        Assert.Equal("F10", config.ToggleHotkey);
    }

    [Fact]
    public void SettlementDefaultsMatchShopLayout()
    {
        var config = new AutomationConfig();

        Assert.Equal(new ConfigPoint(3284, 1866), config.SettlementTopLeft);
        Assert.Equal(new ConfigPoint(3220, 400), config.SettlementMultiplierToggle);
        Assert.Equal(new ConfigPoint(3370, 386), config.SettlementMultiplierTopLeft);
        Assert.Equal(6, config.SettlementBuyButtons.Count);
        Assert.Equal(4, config.SettlementSubcategoryTabs.Count);
        Assert.False(config.SettlementPurchases.HasAnyPurchase());
        Assert.True(config.SettlementConfirmLeftover);
    }

    [Fact]
    public void PickPriorityTreasureRejectsWeakFalsePositiveDiamond()
    {
        // 复现 01:14:00：说明区弱钻石 0.56 与图标条强剑/盾重叠位置。
        var scored = new List<MazeAutomation.TreasureCandidate>
        {
            new("diamond", new TemplateMatchResult(0.5645, 11, 285, 60, 60)),
            new("sparkle", new TemplateMatchResult(0.4521, 544, 147, 54, 55)),
            new("shield", new TemplateMatchResult(0.8337, 11, 3, 70, 70)),
            new("sword", new TemplateMatchResult(0.8893, 21, 12, 58, 61)),
            new("heart", new TemplateMatchResult(0.4520, 544, 141, 58, 55))
        };
        string[] priority = ["diamond", "sparkle", "shield", "sword", "heart"];

        // 旧阈值 0.55 会误选钻石；新逻辑提高阈值后应选剑。
        MazeAutomation.TreasureCandidate? winner =
            MazeAutomation.PickPriorityTreasure(scored, priority, threshold: 0.62);

        Assert.NotNull(winner);
        Assert.Equal("sword", winner.Key);
    }

    [Fact]
    public void PickPriorityTreasurePrefersDiamondWhenTrulyPresent()
    {
        var scored = new List<MazeAutomation.TreasureCandidate>
        {
            new("diamond", new TemplateMatchResult(0.91, 100, 10, 60, 60)),
            new("shield", new TemplateMatchResult(0.88, 400, 12, 70, 70)),
            new("sword", new TemplateMatchResult(0.85, 700, 8, 58, 61))
        };
        string[] priority = ["diamond", "sparkle", "shield", "sword", "heart"];

        MazeAutomation.TreasureCandidate? winner =
            MazeAutomation.PickPriorityTreasure(scored, priority, threshold: 0.62);

        Assert.NotNull(winner);
        Assert.Equal("diamond", winner.Key);
    }

    [Fact]
    public void TreasureOptionDefaultsUseTightIconStrip()
    {
        var config = new AutomationConfig();
        Assert.Equal(new ConfigSize(2162, 200), config.TreasureOptionsSize);
        Assert.Equal(0.62, config.TreasureMatchThreshold);
        Assert.Equal(new ConfigPoint(0, 70), config.TreasureClickOffset);
    }

    [Fact]
    public void SettlementPurchasesRoundTripThroughJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"better-muv-shop-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AutomationConfig();
            config.SettlementPurchases.Daily.SkillBook1 = [-1, 0, 2, 0, 0, 1];
            config.SettlementPurchases.Equipment.Physics = [3, 0, -1, 0];
            config.SettlementPurchases.Excavation.Funds = -1;
            config.SettlementPurchases.Excavation.Training = 5;
            config.SettlementPurchases.Artifactor.Agility = [0, 1, 0, 0, -1];
            config.Save(path);

            AutomationConfig loaded = AutomationConfig.Load(path);
            Assert.Equal([-1, 0, 2, 0, 0, 1], loaded.SettlementPurchases.Daily.SkillBook1);
            Assert.Equal([3, 0, -1, 0], loaded.SettlementPurchases.Equipment.Physics);
            Assert.Equal(-1, loaded.SettlementPurchases.Excavation.Funds);
            Assert.Equal(5, loaded.SettlementPurchases.Excavation.Training);
            Assert.Equal([0, 1, 0, 0, -1], loaded.SettlementPurchases.Artifactor.Agility);
            Assert.True(loaded.SettlementPurchases.HasAnyPurchase());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ToggleHotkeyRoundTripsThroughJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"better-muv-hotkey-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AutomationConfig { ToggleHotkey = "F9" };
            config.Save(path);

            AutomationConfig loaded = AutomationConfig.Load(path);
            Assert.Equal("F9", loaded.ToggleHotkey);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("A")]
    [InlineData("")]
    public void ToggleHotkeyRejectsUnsupportedKeys(string hotkey)
    {
        var config = new AutomationConfig { ToggleHotkey = hotkey };
        string path = Path.Combine(Path.GetTempPath(), $"better-muv-hotkey-bad-{Guid.NewGuid():N}.json");
        Assert.Throws<InvalidDataException>(() => config.Save(path));
        if (File.Exists(path))
            File.Delete(path);
    }

    [Fact]
    public void RouteTreasureRegionMapsFromRequested4kRectangle()
    {
        var geometry = new CaptureGeometry(new ScreenRect(0, 0, 1920, 1080));

        ScreenRect result = geometry.ReferenceRegionFromTopLeftToScreen(
            new ConfigPoint(1322, 486), new ConfigSize(418, 1108), 3840, 2160);

        Assert.Equal(new ScreenRect(661, 243, 209, 554), result);
    }
}

public class TemplateMatcherTests
{
    [Fact]
    public void FindsTemplateInsideSyntheticImage()
    {
        const int sourceWidth = 24;
        const int sourceHeight = 18;
        const int templateWidth = 6;
        const int templateHeight = 5;
        byte[] source = new byte[sourceWidth * sourceHeight];
        byte[] template = new byte[templateWidth * templateHeight];
        for (int y = 0; y < templateHeight; y++)
        for (int x = 0; x < templateWidth; x++)
            template[y * templateWidth + x] = (byte)(20 + x * 17 + y * 23);

        const int expectedX = 11;
        const int expectedY = 7;
        for (int y = 0; y < templateHeight; y++)
        for (int x = 0; x < templateWidth; x++)
            source[(expectedY + y) * sourceWidth + expectedX + x] = template[y * templateWidth + x];

        TemplateMatchResult result = TemplateMatcher.MatchGray(
            source, sourceWidth, sourceHeight, template, templateWidth, templateHeight);

        Assert.Equal(expectedX, result.X);
        Assert.Equal(expectedY, result.Y);
        Assert.True(result.Score > 0.999);
    }
}
