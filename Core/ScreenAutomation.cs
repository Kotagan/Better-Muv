using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 截图 / 识别 / 点击的唯一业务入口；坐标缩放一律经 <see cref="CaptureGeometry"/>。
/// </summary>
public sealed class ScreenAutomation
{
    private readonly AutomationConfig _config;
    private readonly WindowCaptureService _capture;
    private readonly MouseInputService _mouse;
    private readonly Action<string> _log;

    public ScreenAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _capture = new WindowCaptureService();
        _mouse = new MouseInputService();
    }

    public WindowCaptureService Capture => _capture;
    public MouseInputService Mouse => _mouse;

    public CaptureGeometry Geometry(GameWindow window) =>
        new(window.DisplayRect, _config.ReferenceWidth, _config.ReferenceHeight);

    public GameWindow FindWindow(string titleKeyword) =>
        _capture.FindWindow(titleKeyword);

    public GameWindow Refresh(GameWindow window) =>
        _capture.Refresh(window);

    public Task<bool> FocusAsync(nint windowHandle, CancellationToken cancellationToken) =>
        _mouse.FocusWindowAsync(windowHandle, cancellationToken);

    public void EnsureSixteenByNine(GameWindow window)
    {
        ScreenRect display = window.DisplayRect;
        if (!CaptureGeometry.CheckSixteenByNine(display.Width, display.Height))
            throw new InvalidOperationException(
                $"游戏所在显示器必须为 16:9，当前为 {display.Width}×{display.Height}。");
    }

    // ---- 截图 ----

    public static ConfigSize EnsureFitsTemplate(ConfigSize size, TemplateMatcher matcher) =>
        new(
            Math.Max(size.Width, matcher.LogicalWidth),
            Math.Max(size.Height, matcher.LogicalHeight));

    /// <summary>按配置矩形截取屏幕 ROI（已乘缩放系数）。</summary>
    public RegionCapture CaptureRegion(GameWindow window, ConfigPoint topLeft, ConfigSize size)
    {
        CaptureGeometry geometry = Geometry(window);
        ScreenRect rect = geometry.RegionFromTopLeftToScreen(topLeft, size);
        (int logicalWidth, int logicalHeight) = geometry.ToLogicalSize(size);
        BitmapSource image = _capture.Capture(window, rect);
        return new RegionCapture(image, rect, logicalWidth, logicalHeight, geometry);
    }

    /// <summary>截取并保证搜索区不小于模板逻辑尺寸。</summary>
    public RegionCapture CaptureRegion(
        GameWindow window, ConfigPoint topLeft, ConfigSize size, TemplateMatcher matcher)
    {
        ConfigSize fitted = EnsureFitsTemplate(size, matcher);
        return CaptureRegion(window, topLeft, fitted);
    }

    /// <summary>截取游戏客户区整图。</summary>
    public BitmapSource CaptureClient(GameWindow window) =>
        _capture.CaptureClient(window);

    /// <summary>从已截客户区整图裁出配置矩形 ROI。</summary>
    public RegionCapture CropRegion(
        GameWindow window, BitmapSource fullClient, ConfigPoint topLeft, ConfigSize size)
    {
        CaptureGeometry geometry = Geometry(window);
        ScreenRect rect = geometry.RegionFromTopLeftToScreen(topLeft, size);
        (int logicalWidth, int logicalHeight) = geometry.ToLogicalSize(size);
        BitmapSource image = _capture.CropFromClient(window, fullClient, rect);
        return new RegionCapture(image, rect, logicalWidth, logicalHeight, geometry);
    }

    public RegionCapture CropRegion(
        GameWindow window, BitmapSource fullClient, ConfigPoint topLeft, ConfigSize size,
        TemplateMatcher matcher)
    {
        ConfigSize fitted = EnsureFitsTemplate(size, matcher);
        return CropRegion(window, fullClient, topLeft, fitted);
    }

    public static bool FitsInClient(ScreenRect rect, ScreenRect client) =>
        rect.Left >= client.Left &&
        rect.Top >= client.Top &&
        rect.Right <= client.Right &&
        rect.Bottom <= client.Bottom;

    // ---- 识别 ----

    public TemplateMatchResult Match(
        TemplateMatcher matcher, BitmapSource image, int logicalWidth, int logicalHeight) =>
        matcher.Match(image, logicalWidth, logicalHeight);

    public TemplateMatchResult MatchPrepared(
        TemplateMatcher matcher, byte[] sourceGray, int logicalWidth, int logicalHeight) =>
        matcher.MatchPrepared(sourceGray, logicalWidth, logicalHeight);

    public Point MatchCenterToScreen(
        RegionCapture region, TemplateMatchResult match) =>
        region.Geometry.MatchCenterToScreen(
            region.ScreenRect, match, region.LogicalWidth, region.LogicalHeight);

    public Point MatchCenterToScreen(
        CaptureGeometry geometry, ScreenRect searchRect, TemplateMatchResult match,
        int logicalWidth, int logicalHeight) =>
        geometry.MatchCenterToScreen(searchRect, match, logicalWidth, logicalHeight);

    /// <summary>截取配置 ROI 并做模板匹配，返回是否命中与屏幕中心。</summary>
    public async Task<TemplateProbeResult> ProbeAsync(
        GameWindow window,
        TemplateMatcher matcher,
        ConfigPoint topLeft,
        ConfigSize size,
        CancellationToken cancellationToken,
        double? matchThreshold = null)
    {
        double threshold = matchThreshold ?? _config.MatchThreshold;
        window = Refresh(window);
        RegionCapture region = CaptureRegion(window, topLeft, size, matcher);
        TemplateMatchResult match = await Task.Run(
            () => Match(matcher, region.Image, region.LogicalWidth, region.LogicalHeight),
            cancellationToken);
        Point center = MatchCenterToScreen(region, match);
        return new TemplateProbeResult(match.Score >= threshold, match.Score, center);
    }

    /// <summary>在配置 ROI 内匹配；命中后点击写死配置点。</summary>
    public async Task<(bool Matched, TemplateMatchResult Match, BitmapSource Image)> MatchRegionAsync(
        GameWindow window,
        TemplateMatcher matcher,
        ConfigPoint topLeft,
        ConfigSize size,
        CancellationToken cancellationToken,
        int? timeoutMs = null)
    {
        window = Refresh(window);
        EnsureSixteenByNine(window);
        RegionCapture region = CaptureRegion(window, topLeft, size, matcher);
        BitmapSource image = region.Image;
        TemplateMatchResult match = null!;
        var timer = Stopwatch.StartNew();
        int effectiveTimeoutMs = timeoutMs ?? _config.DetectionTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image = _capture.Capture(window, region.ScreenRect);
            match = await Task.Run(
                () => Match(matcher, image, region.LogicalWidth, region.LogicalHeight),
                cancellationToken);
            if (match.Score >= _config.MatchThreshold ||
                timer.ElapsedMilliseconds >= effectiveTimeoutMs)
                break;
        }

        return (match.Score >= _config.MatchThreshold, match, image);
    }

    // ---- 点击 ----

    /// <summary>点击配置点（1080p）：screen = Display原点 + point × Scale。</summary>
    public async Task<GameWindow> ClickAsync(
        GameWindow window, ConfigPoint referencePoint, string reason, CancellationToken cancellationToken)
    {
        window = Refresh(window);
        EnsureSixteenByNine(window);
        CaptureGeometry geometry = Geometry(window);
        Point point = geometry.ToScreen(referencePoint);
        _log($"{reason}：1080p({referencePoint.X},{referencePoint.Y}) ×({geometry.ScaleX:F3},{geometry.ScaleY:F3}) → screen({point.X:F0},{point.Y:F0})");
        await _mouse.ClickAsync(window.Handle, point, cancellationToken);
        return window;
    }

    /// <summary>点击已换算好的屏幕坐标（仅匹配结果中心等场景）。</summary>
    public Task ClickScreenAsync(
        GameWindow window, Point screenPoint, CancellationToken cancellationToken) =>
        _mouse.ClickAsync(window.Handle, screenPoint, cancellationToken);
}

public readonly record struct RegionCapture(
    BitmapSource Image,
    ScreenRect ScreenRect,
    int LogicalWidth,
    int LogicalHeight,
    CaptureGeometry Geometry);
