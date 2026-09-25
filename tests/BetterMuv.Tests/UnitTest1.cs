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
    public void NonSixteenByNineViewportUsesIndependentAxesAndOrigin()
    {
        var geometry = new CaptureGeometry(new ScreenRect(12, 80, 1600, 1000), 1920, 1080);

        Point bottomRight = geometry.ToScreen(new ConfigPoint(1920, 1080));

        Assert.Equal(1612, bottomRight.X);
        Assert.Equal(1080, bottomRight.Y);
        Assert.Equal(1600 / 1920.0, geometry.ScaleX);
        Assert.Equal(1000 / 1080.0, geometry.ScaleY);
    }

    [Fact]
    public void NonSixteenByNineRegionStaysInsideViewport()
    {
        var geometry = new CaptureGeometry(new ScreenRect(12, 80, 1600, 1000), 1920, 1080);

        ScreenRect region = geometry.RegionFromTopLeftToScreen(
            new ConfigPoint(1420, 0), new ConfigSize(500, 260));

        Assert.True(region.Left >= geometry.ViewportRect.Left);
        Assert.True(region.Top >= geometry.ViewportRect.Top);
        Assert.True(region.Right <= geometry.ViewportRect.Right);
        Assert.True(region.Bottom <= geometry.ViewportRect.Bottom);
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

    [Theory]
    [InlineData(1280, 720, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(2560, 1440, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(5760, 3240, true)]
    [InlineData(2560, 1417, false)]
    [InlineData(1920, 1040, false)]
    [InlineData(1600, 900, false)]
    public void Preferred1080LadderRecognizesCommonClients(int width, int height, bool expected)
    {
        Assert.Equal(expected, ScreenAutomation.IsPreferred1080Ladder(width, height));
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
    public void ExecutionDefaultsUseSeparatePauseAndStopHotkeys()
    {
        var config = new AutomationConfig();

        Assert.False(config.LaunchGameWithCapture);
        Assert.Equal("F10", config.PauseHotkey);
        Assert.Equal("F11", config.StopHotkey);
        Assert.Equal(90, config.GameLaunchTimeoutSeconds);
        Assert.Equal(["maze", "mainQuest", "hardMainQuest", "dailyShop", "dailyFreeGift", "dailyExercises"], AutomationConfig.NormalizePipelineTaskOrder(null));
        Assert.Equal(["mainQuest", "maze", "hardMainQuest", "dailyShop", "dailyFreeGift", "dailyExercises"], AutomationConfig.NormalizePipelineTaskOrder(["mainQuest", "maze", "unknown"]));
        Assert.Equal(new ConfigPoint(1780, 965), new AutomationConfig().DailyShopEntryClick);
        Assert.Equal(new ConfigPoint(1611, 473), new AutomationConfig().SecondClick);
        Assert.Equal(new ConfigPoint(1080, 950), new AutomationConfig().SearchTopLeft);
        Assert.Equal(new ConfigSize(150, 110), new AutomationConfig().FirstSearchSize);
        Assert.Equal(new ConfigPoint(1470, 420), new AutomationConfig().SecondSearchTopLeft);
        Assert.Equal(new ConfigSize(300, 100), new AutomationConfig().SecondSearchSize);
        Assert.Equal(new ConfigPoint(1040, 590), new AutomationConfig().QuestBattleSimulateTopLeft);
        Assert.Equal(new ConfigSize(360, 160), new AutomationConfig().QuestBattleSimulateSize);
        Assert.Equal(new ConfigPoint(1380, 650), new AutomationConfig().QuestExercisesTopLeft);
        Assert.Equal(new ConfigSize(340, 130), new AutomationConfig().QuestExercisesSize);
        Assert.Equal(new ConfigPoint(1410, 820), new AutomationConfig().QuestActivityTopLeft);
        Assert.Equal(new ConfigSize(340, 130), new AutomationConfig().QuestActivitySize);
        Assert.Equal(new ConfigPoint(0, 0), new AutomationConfig().NavBackTopLeft);
        Assert.Equal(new ConfigSize(160, 130), new AutomationConfig().NavBackSize);
        Assert.Equal("muv_luv_girlsgardenx_cl.exe", GamePathLocator.ExecutableName);
        Assert.False(GamePathLocator.IsValid(null));
        Assert.False(GamePathLocator.IsValid(@"C:\missing\muv_luv_girlsgardenx_cl.exe"));
        Assert.False(GamePathLocator.IsValid(@"C:\Windows\System32\schtasks.exe"));
        Assert.Null(GamePathLocator.TryNormalize(@"C:\Windows\System32\schtasks.exe", out string rejectReason));
        Assert.Contains("不是游戏程序", rejectReason);
    }

    [Fact]
    public void RouteSelectionDefaultsUse1080pRegionsAndPriority()
    {
        var config = new AutomationConfig();

        Assert.Equal(1920, config.ReferenceWidth);
        Assert.Equal(1080, config.ReferenceHeight);
        Assert.Equal(new ConfigPoint(1500, 880), config.RouteSelectionTopLeft);
        Assert.Equal(new ConfigSize(360, 180), config.RouteSelectionSize);
        Assert.Equal(new ConfigPoint(661, 243), config.RouteTreasureOptionsTopLeft);
        Assert.Equal(new ConfigSize(209, 554), config.RouteTreasureOptionsSize);
        Assert.Equal(
            ["diamond", "shield", "sword", "heart", "skull", "shoe", "sparkle"],
            config.TreasurePriority);
        Assert.Equal("F10", config.ToggleHotkey);
        Assert.Equal("keep", config.MazeDifficultyMode);
        Assert.Equal(1, config.MazeDifficultyTarget);
        Assert.Equal(new ConfigPoint(1320, 310), config.DifficultyDigitTopLeft);
        Assert.Equal(new ConfigSize(420, 150), config.DifficultyDigitSize);
        Assert.Equal(new ConfigPoint(992, 383), config.DifficultyDecreaseClick);
        Assert.Equal(new ConfigPoint(1880, 381), config.DifficultyIncreaseClick);
        Assert.Equal(new ConfigPoint(1633, 261), config.DifficultyOpenSliderClick);
        Assert.Equal(new ConfigPoint(579, 292), config.DifficultyListTopLeft);
        Assert.Equal(new ConfigSize(182, 561), config.DifficultyListSize);
        Assert.Equal(new ConfigPoint(1140, 975), config.DifficultyConfirmClick);
    }

    [Fact]
    public void SettlementDefaultsMatchShopLayout()
    {
        var config = new AutomationConfig();

        Assert.Equal(new ConfigPoint(1674, 948), config.SettlementTopLeft);
        Assert.Equal(new ConfigPoint(1500, 880), config.SettlementSearchTopLeft);
        Assert.Equal(new ConfigSize(360, 180), config.SettlementSearchSize);
        Assert.Equal(new ConfigPoint(1610, 200), config.SettlementMultiplierToggle);
        Assert.Equal(new ConfigPoint(1640, 165), config.SettlementMultiplierTopLeft);
        Assert.Equal(new ConfigSize(140, 70), config.SettlementMultiplierSize);
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
    public void TreasureOptionDefaultsUseVerticalCardRegion()
    {
        var config = new AutomationConfig();
        Assert.Equal(new ConfigPoint(700, 15), config.TreasureStateTopLeft);
        Assert.Equal(new ConfigSize(520, 120), config.TreasureStateSize);
        Assert.Equal(new ConfigPoint(478, 316), config.TreasureOptionsTopLeft);
        Assert.Equal(new ConfigSize(1081, 90), config.TreasureOptionsSize);
        Assert.Equal(0.58, config.TreasureMatchThreshold);
        Assert.Equal(new ConfigPoint(-40, 55), config.TreasureClickOffset);
    }

    [Fact]
    public void PickPriorityTreasureIgnoresWeakDiamondAgainstStrongerIcons()
    {
        var scored = new List<MazeAutomation.TreasureCandidate>
        {
            new("diamond", new TemplateMatchResult(0.6590, 800, 50, 40, 33)),
            new("shield", new TemplateMatchResult(0.8880, 1200, 40, 70, 70)),
            new("heart", new TemplateMatchResult(0.9399, 500, 56, 58, 55))
        };
        string[] priority = ["diamond", "shield", "sword", "heart", "skull", "shoe", "sparkle"];

        MazeAutomation.TreasureCandidate? winner =
            MazeAutomation.PickPriorityTreasure(scored, priority, threshold: 0.70);

        Assert.NotNull(winner);
        Assert.Equal("shield", winner.Key);
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
    public void DisableSettlementSlotReportsChangeBeforeSharedArrayIsCleared()
    {
        var purchases = new SettlementPurchases();
        int[] quantities = purchases.Equipment.Physics;
        quantities[0] = -1;

        bool changed = SettlementShopCatalog.DisableSlot(
            purchases, "equipment", "physics", 0);

        Assert.True(changed);
        Assert.Equal(0, quantities[0]);
        Assert.False(SettlementShopCatalog.DisableSlot(
            purchases, "equipment", "physics", 0));
    }

    [Fact]
    public void DisablingLimitedSlotAlsoTurnsOffPageAndCategoryBuyAll()
    {
        var purchases = new SettlementPurchases();
        SettlementShopCatalog.SetCategoryBuyAll(purchases, "equipment", true);
        Assert.True(SettlementShopCatalog.IsPageBuyAll(purchases, "equipment", "physics"));
        Assert.True(SettlementShopCatalog.IsCategoryBuyAll(purchases, "equipment"));

        Assert.True(SettlementShopCatalog.DisableSlot(
            purchases, "equipment", "physics", 0));

        Assert.False(SettlementShopCatalog.IsPageBuyAll(purchases, "equipment", "physics"));
        Assert.False(SettlementShopCatalog.IsCategoryBuyAll(purchases, "equipment"));
    }

    [Fact]
    public void DailyShopDayStartsAtFourAm()
    {
        Assert.Equal(new DateOnly(2026, 9, 3), DailyShopSchedule.CurrentShopDay(new DateTime(2026, 9, 3, 4, 0, 0)));
        Assert.Equal(new DateOnly(2026, 9, 2), DailyShopSchedule.CurrentShopDay(new DateTime(2026, 9, 3, 3, 59, 0)));
        Assert.Equal("2026-09-03", DailyShopSchedule.CurrentShopDayKey(new DateTime(2026, 9, 3, 12, 0, 0)));
        Assert.True(DailyShopSchedule.QuotaFilledThisShopDay("2026-09-03", new DateTime(2026, 9, 3, 23, 0, 0)));
        Assert.False(DailyShopSchedule.QuotaFilledThisShopDay("2026-09-03", new DateTime(2026, 9, 4, 4, 0, 0)));
        Assert.Equal(new DateTime(2026, 9, 4, 4, 0, 0), DailyShopSchedule.NextReset(new DateTime(2026, 9, 3, 12, 0, 0)));
        Assert.Equal(new DateTime(2026, 9, 3, 4, 0, 0), DailyShopSchedule.NextReset(new DateTime(2026, 9, 3, 3, 0, 0)));
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

    [Fact]
    public void FirstRunNoticeAcceptedRoundTripsThroughJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"better-muv-firstrun-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AutomationConfig { FirstRunNoticeAccepted = true };
            config.Save(path);

            AutomationConfig loaded = AutomationConfig.Load(path);
            Assert.True(loaded.FirstRunNoticeAccepted);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SelectedWindowRoundTripsThroughJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"better-muv-window-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AutomationConfig
            {
                WindowSelectionMode = "selected",
                SelectedWindowProcessName = "chrome",
                SelectedWindowClassName = "Chrome_WidgetWin_1",
                SelectedWindowTitle = "Muv-Luv - Google Chrome"
            };
            config.Save(path);

            AutomationConfig loaded = AutomationConfig.Load(path);
            Assert.Equal("selected", loaded.WindowSelectionMode);
            Assert.Equal("chrome", loaded.SelectedWindowProcessName);
            Assert.Equal("Chrome_WidgetWin_1", loaded.SelectedWindowClassName);
            Assert.Equal("Muv-Luv - Google Chrome", loaded.SelectedWindowTitle);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
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

public class DigitOcrParseTests
{
    [Theory]
    [InlineData("0 / 4", 0)]
    [InlineData("12/20", 12)]
    [InlineData("日 の 購 入 回 数 : 0 /", 0)]
    [InlineData("日 の 購 入 回 数 : 12 / ・", 12)]
    [InlineData("日 の 購 入 回 数 : O /", 0)]
    [InlineData("購入回数：3／5", 3)]
    public void TryParseRatioLeftAcceptsIncompleteRightSide(string text, int expected)
    {
        Assert.Equal(expected, DigitOcrService.TryParseRatioLeft(text));
    }

    [Theory]
    [InlineData("本日の購入回数：0/4", 4)]
    [InlineData("本日の購入回数：18/24", 6)]
    [InlineData("購入回数：4／4", 0)]
    [InlineData("18/24", 6)]
    [InlineData("0 / 4", 4)]
    [InlineData("回 : 1 8 / 24", 6)]
    [InlineData("本 日 の 購 入 回 数 : 0 / 4", 4)]
    public void TryParseDailyPurchaseRemainingUsesMaxMinusUsed(string text, int expected)
    {
        Assert.Equal(expected, DigitOcrService.TryParseDailyPurchaseRemaining(text));
    }

    [Fact]
    public void TryParseDailyPurchaseRemainingRejectsTruncatedMax()
    {
        // ROI 过窄截成「18/2」时不可信。
        Assert.Null(DigitOcrService.TryParseDailyPurchaseRemaining("本日の購入回数：18/2"));
        Assert.Null(DigitOcrService.TryParseDailyPurchaseRemaining("18 /"));
        Assert.Null(DigitOcrService.TryParseDailyPurchaseRemaining("購入回数"));
    }

    [Fact]
    public void TryParseRatioLeftReturnsNullWhenNoSlashRatio()
    {
        Assert.Null(DigitOcrService.TryParseRatioLeft("購入回数"));
        Assert.Null(DigitOcrService.TryParseRatioLeft(""));
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
