using System.Windows;
using BetterMuv.Core;
using BetterMuv.Services;

namespace BetterMuv.Tests;

public class CaptureGeometryTests
{
    [Fact]
    public void ScaleCoefficientsComeFromDisplayOverReference()
    {
        var geometry = new CaptureGeometry(new ScreenRect(0, 0, 3840, 2160), 1920, 1080);
        Assert.Equal(2.0, geometry.ScaleX);
        Assert.Equal(2.0, geometry.ScaleY);
    }

    [Fact]
    public void ToScreenAppliesGlobalScale()
    {
        var geometry = new CaptureGeometry(new ScreenRect(0, 0, 3840, 2160), 1920, 1080);

        var first = geometry.ToScreen(new ConfigPoint(1143, 961));
        var fourth = geometry.ToScreen(new ConfigPoint(1693, 942));

        Assert.Equal(2286, first.X);
        Assert.Equal(1922, first.Y);
        Assert.Equal(3386, fourth.X);
        Assert.Equal(1884, fourth.Y);
    }

    [Fact]
    public void ScaleDeltaUsesSameCoefficients()
    {
        var geometry = new CaptureGeometry(new ScreenRect(10, 20, 3840, 2160), 1920, 1080);
        Point delta = geometry.ScaleDelta(new ConfigPoint(-40, 55));
        Assert.Equal(-80, delta.X);
        Assert.Equal(110, delta.Y);
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
    public void RegionFromCenterUsesGlobalScale()
    {
        var geometry = new CaptureGeometry(new ScreenRect(100, 50, 1920, 1080), 1920, 1080);

        ScreenRect result = geometry.RegionFromCenterToScreen(
            new ConfigPoint(1148, 965), new ConfigSize(250, 150));

        Assert.Equal(new ScreenRect(1123, 940, 250, 150), result);
    }

    [Fact]
    public void RegionFromTopLeftUsesGlobalScale()
    {
        var geometry = new CaptureGeometry(new ScreenRect(100, 50, 1920, 1080), 1920, 1080);

        ScreenRect result = geometry.RegionFromTopLeftToScreen(
            new ConfigPoint(1120, 922), new ConfigSize(113, 68));

        Assert.Equal(new ScreenRect(1220, 972, 113, 68), result);
    }

    [Fact]
    public void MatchCenterToScreenMapsInsideScaledRoi()
    {
        var geometry = new CaptureGeometry(new ScreenRect(0, 0, 3840, 2160), 1920, 1080);
        var search = new ScreenRect(100, 200, 400, 200);
        var match = new TemplateMatchResult(0.9, 10, 20, 40, 30);

        Point center = geometry.MatchCenterToScreen(search, match, logicalWidth: 200, logicalHeight: 100);

        Assert.Equal(100 + (10 + 20) * 400 / 200.0, center.X);
        Assert.Equal(200 + (20 + 15) * 200 / 100.0, center.Y);
    }
}

public class AutomationConfigTests
{
    [Fact]
    public void RouteSelectionDefaultsUse1080pRegionsAndPriority()
    {
        var config = new AutomationConfig();

        Assert.Equal(1920, config.ReferenceWidth);
        Assert.Equal(1080, config.ReferenceHeight);
        Assert.Equal(new ConfigPoint(1595, 924), config.RouteSelectionTopLeft);
        Assert.Equal(new ConfigSize(181, 37), config.RouteSelectionSize);
        Assert.Equal(new ConfigPoint(661, 243), config.RouteTreasureOptionsTopLeft);
        Assert.Equal(new ConfigSize(209, 554), config.RouteTreasureOptionsSize);
        Assert.Equal(
            ["diamond", "shield", "sword", "heart", "skull", "sparkle"],
            config.TreasurePriority);
        Assert.Equal("F10", config.ToggleHotkey);
    }

    [Fact]
    public void SettlementDefaultsMatchShopLayout()
    {
        var config = new AutomationConfig();

        Assert.Equal(new ConfigPoint(1642, 933), config.SettlementTopLeft);
        Assert.Equal(new ConfigPoint(1626, 917), config.SettlementSearchTopLeft);
        Assert.Equal(new ConfigSize(97, 62), config.SettlementSearchSize);
        Assert.Equal(new ConfigPoint(1610, 200), config.SettlementMultiplierToggle);
        Assert.Equal(new ConfigPoint(1673, 181), config.SettlementMultiplierTopLeft);
        Assert.Equal(new ConfigSize(81, 49), config.SettlementMultiplierSize);
        Assert.Equal(6, config.SettlementBuyButtons.Count);
        Assert.Equal(4, config.SettlementSubcategoryTabs.Count);
        Assert.False(config.SettlementPurchases.HasAnyPurchase());
        Assert.True(config.SettlementConfirmLeftover);
    }

    [Fact]
    public void PickPriorityTreasureRejectsWeakFalsePositiveDiamond()
    {
        var scored = new List<MazeAutomation.TreasureCandidate>
        {
            new("diamond", new TemplateMatchResult(0.5645, 11, 285, 60, 60)),
            new("sparkle", new TemplateMatchResult(0.4521, 544, 147, 54, 55)),
            new("shield", new TemplateMatchResult(0.8337, 11, 3, 70, 70)),
            new("sword", new TemplateMatchResult(0.8893, 21, 12, 58, 61)),
            new("heart", new TemplateMatchResult(0.4520, 544, 141, 58, 55))
        };
        string[] priority = ["diamond", "sparkle", "shield", "sword", "heart"];

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
        Assert.Equal(new ConfigSize(1081, 90), config.TreasureOptionsSize);
        Assert.Equal(0.58, config.TreasureMatchThreshold);
        Assert.Equal(new ConfigPoint(-40, 55), config.TreasureClickOffset);
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
    public void RouteTreasureRegionMapsFrom1080pRectangle()
    {
        var geometry = new CaptureGeometry(new ScreenRect(0, 0, 1920, 1080), 1920, 1080);

        ScreenRect result = geometry.RegionFromTopLeftToScreen(
            new ConfigPoint(661, 243), new ConfigSize(209, 554));

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
        {
            for (int x = 0; x < templateWidth; x++)
            {
                byte value = (byte)(40 + x + y);
                template[y * templateWidth + x] = value;
                source[(y + 4) * sourceWidth + (x + 7)] = value;
            }
        }

        TemplateMatchResult match = TemplateMatcher.MatchGray(
            source, sourceWidth, sourceHeight, template, templateWidth, templateHeight);

        Assert.True(match.Score > 0.99);
        Assert.Equal(7, match.X);
        Assert.Equal(4, match.Y);
    }
}
