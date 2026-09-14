using System.Windows;
using BetterMuv.Core;
using BetterMuv.Services;

namespace BetterMuv.Tests;

public class FullscreenYMappingTests
{
    [Theory]
    [InlineData(1280, 720, 1920, 1080, false)]
    [InlineData(1920, 1080, 1920, 1080, true)]
    [InlineData(2560, 1417, 2560, 1440, false)]
    [InlineData(3840, 2160, 3840, 2160, true)]
    public void FullscreenRequiresClientToFillDisplay(
        int clientWidth, int clientHeight, int displayWidth, int displayHeight, bool expected)
    {
        Assert.Equal(expected, ScreenAutomation.IsFullscreenSized(
            new ScreenRect(10, 20, clientWidth, clientHeight),
            new ScreenRect(0, 0, displayWidth, displayHeight)));
    }

    [Theory]
    [InlineData("game")]
    [InlineData("selected")]
    public void WindowChromeDoesNotShiftVerticalCoordinates(string mode)
    {
        var screen = new ScreenAutomation(new AutomationConfig { WindowSelectionMode = mode }, _ => { });
        var window = new GameWindow(0, "test",
            new ScreenRect(0, 23, 2560, 1417), new ScreenRect(0, 0, 2560, 1440));

        CaptureGeometry geometry = screen.Geometry(window);

        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), geometry.ViewportRect);
        Assert.Equal(new Point(1280, 720), geometry.ToScreen(new ConfigPoint(960, 540)));
        Assert.Equal(1440, geometry.ToScreen(new ConfigPoint(0, 1080)).Y);
        screen.EnsureUsableViewport(window);
    }

    [Fact]
    public void SecondaryDisplayOriginAppliesToPointsRegionsAndOffsets()
    {
        var screen = new ScreenAutomation(new AutomationConfig(), _ => { });
        var window = new GameWindow(0, "test",
            new ScreenRect(-3840, -177, 3840, 2100), new ScreenRect(-3840, -200, 3840, 2160));
        CaptureGeometry geometry = screen.Geometry(window);

        Assert.Equal(new Point(-1554, 1722), geometry.ToScreen(new ConfigPoint(1143, 961)));
        Assert.Equal(new ScreenRect(-840, 1560, 720, 360),
            geometry.RegionFromTopLeftToScreen(new ConfigPoint(1500, 880), new ConfigSize(360, 180)));
        Assert.Equal(new Point(-80, 110), geometry.ScaleDelta(new ConfigPoint(-40, 55)));
        Assert.Equal(new Point(-780, 1630), geometry.MatchCenterToScreen(
            new ScreenRect(-840, 1560, 720, 360), new TemplateMatchResult(0.9, 10, 20, 40, 30), 360, 180));
    }

    [Fact]
    public void HorizontalMappingIsPreservedAndCalibrationCanBeReset()
    {
        var screen = new ScreenAutomation(new AutomationConfig(), _ => { });
        var window = new GameWindow(0, "test",
            new ScreenRect(100, 80, 1600, 1000), new ScreenRect(0, 0, 1920, 1080));

        Assert.Equal(new ScreenRect(11, 0, 1778, 1080), screen.Viewport(window));
        screen.EnsureUsableViewport(window);
        screen.CalibrateOffset(7, -9);
        Assert.Equal(new ScreenRect(18, -9, 1778, 1080), screen.Viewport(window));
        screen.ResetCalibration();
        Assert.Equal(new ScreenRect(11, 0, 1778, 1080), screen.Viewport(window));
    }

    [Fact]
    public void Fullscreen4KCoordinatesRemainUnchanged()
    {
        var screen = new ScreenAutomation(new AutomationConfig(), _ => { });
        var rect = new ScreenRect(0, 0, 3840, 2160);
        var geometry = screen.Geometry(new GameWindow(0, "test", rect, rect));

        Assert.Equal(rect, geometry.ViewportRect);
        Assert.Equal(new Point(3494, 1894), geometry.ToScreen(new ConfigPoint(1747, 947)));
    }
}
