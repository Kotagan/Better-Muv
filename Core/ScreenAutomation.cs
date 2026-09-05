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
    private int _calibrationOffsetX;
    private int _calibrationOffsetY;

    public ScreenAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _capture = new WindowCaptureService();
        _mouse = new MouseInputService();
    }

    public WindowCaptureService Capture => _capture;
    public MouseInputService Mouse => _mouse;

    public ScreenRect Viewport(GameWindow window)
    {
        // 游戏始终以参考宽高比铺满窗口（cover），非 16:9 客户区会居中裁掉一部分。
        // 因此映射基准可能略大于客户区，原点也可能位于客户区之外。
        double scale = Math.Max(
            window.ClientRect.Width / (double)_config.ReferenceWidth,
            window.ClientRect.Height / (double)_config.ReferenceHeight);
        int width = Math.Max(1, (int)Math.Round(_config.ReferenceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(_config.ReferenceHeight * scale));
        return new ScreenRect(
            window.ClientRect.Left + (window.ClientRect.Width - width) / 2 + _calibrationOffsetX,
            window.ClientRect.Top + (window.ClientRect.Height - height) / 2 + _calibrationOffsetY,
            width,
            height);
    }

    public void CalibrateOffset(int deltaX, int deltaY)
    {
        _calibrationOffsetX += deltaX;
        _calibrationOffsetY += deltaY;
    }

    /// <summary>清除累计校准偏移（每轮开局前调用，避免脏偏移残留）。</summary>
    public void ResetCalibration()
    {
        _calibrationOffsetX = 0;
        _calibrationOffsetY = 0;
    }

    public CaptureGeometry Geometry(GameWindow window) =>
        new(Viewport(window), _config.ReferenceWidth, _config.ReferenceHeight);

    public GameWindow FindWindow(string titleKeyword) =>
        _config.WindowSelectionMode.Equals("selected", StringComparison.OrdinalIgnoreCase)
            ? _capture.FindWindow(_config)
            : _capture.FindWindow(titleKeyword);

    public GameWindow Refresh(GameWindow window) =>
        _capture.Refresh(window);

    public Task<bool> FocusAsync(nint windowHandle, CancellationToken cancellationToken) =>
        _mouse.FocusWindowAsync(windowHandle, cancellationToken);

    public void EnsureUsableViewport(GameWindow window)
    {
        ScreenRect viewport = Viewport(window);
        if (viewport.Width <= 0 || viewport.Height <= 0)
            throw new InvalidOperationException("游戏窗口已最小化或映射区域尺寸无效。");
        if (!_config.WindowSelectionMode.Equals("selected", StringComparison.OrdinalIgnoreCase) &&
            !CaptureGeometry.CheckSixteenByNine(viewport.Width, viewport.Height))
            throw new InvalidOperationException(
                $"游戏所在显示器必须为 16:9，当前为 {viewport.Width}×{viewport.Height}。");
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

    /// <summary>顺序执行一组模板探测，并以不区分大小写的键返回结果。</summary>
    public async Task<IReadOnlyDictionary<string, TemplateProbeResult>> ProbeManyAsync(
        GameWindow window,
        IEnumerable<TemplateProbe> probes,
        CancellationToken cancellationToken)
    {
        window = Refresh(window);
        var results = new Dictionary<string, TemplateProbeResult>(StringComparer.OrdinalIgnoreCase);
        foreach (TemplateProbe probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[probe.Key] = await ProbeAsync(
                window, probe.Matcher, probe.TopLeft, probe.Size,
                cancellationToken, probe.Threshold);
        }

        return results;
    }

    /// <summary>轮询模板直到命中或超时；返回期间得分最高的一次结果。</summary>
    public async Task<TemplateProbeResult> WaitForProbeAsync(
        GameWindow window,
        TemplateMatcher matcher,
        ConfigPoint topLeft,
        ConfigSize size,
        CancellationToken cancellationToken,
        int timeoutMs,
        double? matchThreshold = null)
    {
        var timer = Stopwatch.StartNew();
        TemplateProbeResult best = TemplateProbes.Empty;
        do
        {
            TemplateProbeResult probe = await ProbeAsync(
                window, matcher, topLeft, size, cancellationToken, matchThreshold);
            if (probe.Score > best.Score) best = probe;
            if (probe.IsMatch) return probe;
            if (timer.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
        while (timer.ElapsedMilliseconds < timeoutMs);
        return best;
    }

    /// <summary>在配置 ROI 内匹配；命中后点击写死配置点。</summary>
    public async Task<(bool Matched, TemplateMatchResult Match, BitmapSource Image)> MatchRegionAsync(
        GameWindow window,
        TemplateMatcher matcher,
        ConfigPoint topLeft,
        ConfigSize size,
        CancellationToken cancellationToken,
        int? timeoutMs = null,
        double? matchThreshold = null)
    {
        double threshold = matchThreshold ?? _config.MatchThreshold;
        window = Refresh(window);
        EnsureUsableViewport(window);
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
            if (match.Score >= threshold ||
                timer.ElapsedMilliseconds >= effectiveTimeoutMs)
                break;
        }

        return (match.Score >= threshold, match, image);
    }

    // ---- 点击 ----

    /// <summary>点击配置点（1080p）：screen = Display原点 + point × Scale。</summary>
    public async Task<GameWindow> ClickAsync(
        GameWindow window, ConfigPoint referencePoint, string reason, CancellationToken cancellationToken)
    {
        window = Refresh(window);
        EnsureUsableViewport(window);
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

    /// <summary>点击模板命中的中心，并统一记录置信度和点击后的稳定等待。</summary>
    public async Task ClickProbeAsync(
        GameWindow window,
        TemplateProbeResult probe,
        string reason,
        CancellationToken cancellationToken,
        int settleDelayMs = 250)
    {
        _log($"{reason}命中 {probe.Score:F3}，点击 ({probe.Center.X:F0},{probe.Center.Y:F0})");
        await ClickScreenAsync(window, probe.Center, cancellationToken);
        if (settleDelayMs > 0)
            await Task.Delay(settleDelayMs, cancellationToken);
    }
}

public readonly record struct RegionCapture(
    BitmapSource Image,
    ScreenRect ScreenRect,
    int LogicalWidth,
    int LogicalHeight,
    CaptureGeometry Geometry);
