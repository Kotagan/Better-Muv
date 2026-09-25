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
        // X 轴保留居中 cover 映射；Y 轴以所在显示器的完整高度为基准，
        // 不再受标题栏、任务栏或客户区纵向裁切影响。
        double scale = Math.Max(
            window.ClientRect.Width / (double)_config.ReferenceWidth,
            window.ClientRect.Height / (double)_config.ReferenceHeight);
        int width = Math.Max(1, (int)Math.Round(_config.ReferenceWidth * scale));
        return new ScreenRect(
            window.ClientRect.Left + (window.ClientRect.Width - width) / 2 + _calibrationOffsetX,
            window.DisplayRect.Top + _calibrationOffsetY,
            width,
            window.DisplayRect.Height);
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
        if (window.ClientRect.Width <= 0 || window.ClientRect.Height <= 0 ||
            viewport.Width <= 0 || viewport.Height <= 0)
            throw new InvalidOperationException("游戏窗口已最小化或映射区域尺寸无效。");
        if (!_config.WindowSelectionMode.Equals("selected", StringComparison.OrdinalIgnoreCase) &&
            !CaptureGeometry.CheckSixteenByNine(window.DisplayRect.Width, window.DisplayRect.Height))
            throw new InvalidOperationException(
                $"游戏所在显示器必须为 16:9，当前为 {window.DisplayRect.Width}×{window.DisplayRect.Height}。");
    }

    /// <summary>
    /// 只有客户区已铺满所在显示器时才视为全屏；否则发送 Alt+Enter 放大后再校验。
    /// </summary>
    public async Task<GameWindow> EnsurePreferredClientAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        window = Refresh(window);
        if (IsFullscreenSized(window.ClientRect, window.DisplayRect))
        {
            EnsureUsableViewport(window);
            return window;
        }

        _log($"客户区 {window.ClientRect.Width}×{window.ClientRect.Height} 未铺满显示器 " +
             $"{window.DisplayRect.Width}×{window.DisplayRect.Height}，发送 Alt+Enter 尝试全屏。");
        await _mouse.SendAltEnterAsync(window.Handle, cancellationToken);
        await Task.Delay(900, cancellationToken);
        window = Refresh(window);
        _log($"全屏切换后客户区 {window.ClientRect.Width}×{window.ClientRect.Height}，" +
             $"显示器 {window.DisplayRect.Width}×{window.DisplayRect.Height}。");

        if (!IsFullscreenSized(window.ClientRect, window.DisplayRect))
            throw new InvalidOperationException(
                $"Alt+Enter 全屏切换失败：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}，" +
                $"显示器 {window.DisplayRect.Width}×{window.DisplayRect.Height}。任务已停止。");

        EnsureUsableViewport(window);
        return window;
    }

    public static bool IsFullscreenSized(ScreenRect client, ScreenRect display) =>
        client.Width == display.Width && client.Height == display.Height;

    /// <summary>是否为常见 16:9「1080p 及其倍数/阶梯」客户区尺寸。</summary>
    public static bool IsPreferred1080Ladder(int width, int height)
    {
        (int W, int H)[] known =
        [
            (1280, 720),
            (1920, 1080),
            (2560, 1440),
            (3840, 2160)
        ];
        foreach ((int w, int h) in known)
        {
            if (width == w && height == h)
                return true;
        }

        if (width > 0 && height > 0 &&
            width % CaptureGeometry.LogicalWidth == 0 &&
            height % CaptureGeometry.LogicalHeight == 0)
        {
            int sx = width / CaptureGeometry.LogicalWidth;
            int sy = height / CaptureGeometry.LogicalHeight;
            return sx == sy && sx >= 1;
        }

        return false;
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

    /// <summary>
    /// 各 ROI 串行截图（GDI 限制），模板匹配并行。
    /// 原先完全串行时，主线 Home 阶段 6 模板在 4K 上约 2.5~3s/轮。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, TemplateProbeResult>> ProbeManyAsync(
        GameWindow window,
        IEnumerable<TemplateProbe> probes,
        CancellationToken cancellationToken)
    {
        window = Refresh(window);
        TemplateProbe[] list = probes as TemplateProbe[] ?? probes.ToArray();
        if (list.Length == 0)
            return new Dictionary<string, TemplateProbeResult>(StringComparer.OrdinalIgnoreCase);

        var jobs = new (string Key, TemplateMatcher Matcher, RegionCapture Region, double Threshold)[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateProbe probe = list[i];
            RegionCapture region = CaptureRegion(window, probe.TopLeft, probe.Size, probe.Matcher);
            region.Image.Freeze();
            jobs[i] = (probe.Key, probe.Matcher, region, probe.Threshold);
        }

        return await Task.Run(() =>
        {
            var results = new TemplateProbeResult[jobs.Length];
            Parallel.For(0, jobs.Length, i =>
            {
                var job = jobs[i];
                TemplateMatchResult match = Match(
                    job.Matcher, job.Region.Image, job.Region.LogicalWidth, job.Region.LogicalHeight);
                Point center = MatchCenterToScreen(job.Region, match);
                results[i] = new TemplateProbeResult(match.Score >= job.Threshold, match.Score, center);
            });

            var map = new Dictionary<string, TemplateProbeResult>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < jobs.Length; i++)
                map[jobs[i].Key] = results[i];
            return map;
        }, cancellationToken);
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

    /// <summary>点击后光标停靠点（1080p）：避开右下角开始/出击粉钮，防止挡住识别。</summary>
    private static readonly ConfigPoint CursorParkPoint = new(80, 200);

    /// <summary>点击配置点（1080p）：X 按客户区 cover，Y 按完整显示器，经 Geometry 统一换算。</summary>
    public async Task<GameWindow> ClickAsync(
        GameWindow window, ConfigPoint referencePoint, string reason, CancellationToken cancellationToken,
        bool parkCursor = true)
    {
        window = Refresh(window);
        EnsureUsableViewport(window);
        CaptureGeometry geometry = Geometry(window);
        Point point = geometry.ToScreen(referencePoint);
        _log($"{reason}：1080p({referencePoint.X},{referencePoint.Y}) ×({geometry.ScaleX:F3},{geometry.ScaleY:F3}) → screen({point.X:F0},{point.Y:F0})");
        await _mouse.ClickAsync(window.Handle, point, cancellationToken);
        if (parkCursor)
            ParkCursor(window);
        return window;
    }

    /// <summary>点击已换算好的屏幕坐标（仅匹配结果中心等场景）。</summary>
    public async Task ClickScreenAsync(
        GameWindow window, Point screenPoint, CancellationToken cancellationToken,
        bool parkCursor = true)
    {
        await _mouse.ClickAsync(window.Handle, screenPoint, cancellationToken);
        if (parkCursor)
            ParkCursor(window);
    }

    /// <summary>点击模板命中的中心，并统一记录置信度和点击后的稳定等待。</summary>
    public async Task ClickProbeAsync(
        GameWindow window,
        TemplateProbeResult probe,
        string reason,
        CancellationToken cancellationToken,
        int settleDelayMs = 400,
        bool parkCursor = true)
    {
        _log($"{reason}命中 {probe.Score:F3}，点击 ({probe.Center.X:F0},{probe.Center.Y:F0})");
        await ClickScreenAsync(window, probe.Center, cancellationToken, parkCursor);
        if (settleDelayMs > 0)
            await Task.Delay(settleDelayMs, cancellationToken);
    }

    public void ParkCursorAway(GameWindow window) => ParkCursor(window);

    private void ParkCursor(GameWindow window)
    {
        try
        {
            window = Refresh(window);
            CaptureGeometry geometry = Geometry(window);
            Point park = geometry.ToScreen(CursorParkPoint);
            _mouse.MoveTo(window.Handle, park);
        }
        catch
        {
            // 停靠失败不影响主流程。
        }
    }

    /// <summary>在 1080p 逻辑点处滚轮（负值为向下）。</summary>
    public async Task WheelAsync(
        GameWindow window,
        ConfigPoint referencePoint,
        int wheelNotches,
        string reason,
        CancellationToken cancellationToken)
    {
        window = Refresh(window);
        EnsureUsableViewport(window);
        CaptureGeometry geometry = Geometry(window);
        Point point = geometry.ToScreen(referencePoint);
        _log($"{reason}：1080p({referencePoint.X},{referencePoint.Y}) 滚轮 {wheelNotches} → screen({point.X:F0},{point.Y:F0})");
        await _mouse.WheelAsync(window.Handle, point, wheelNotches, cancellationToken);
    }

    /// <summary>点击输入框后 Ctrl+V 粘贴剪贴板文本（调用方先 Clipboard.SetText）。</summary>
    public async Task PasteClipboardAsync(
        GameWindow window, string reason, CancellationToken cancellationToken)
    {
        window = Refresh(window);
        EnsureUsableViewport(window);
        _log($"{reason}：Ctrl+V 粘贴。");
        await _mouse.SendCtrlVAsync(window.Handle, cancellationToken);
    }

    /// <summary>输入框全选 → 删除 → Ctrl+V（下一条兑换码替换旧内容）。</summary>
    public async Task ClearFieldAndPasteAsync(
        GameWindow window, string reason, CancellationToken cancellationToken)
    {
        window = Refresh(window);
        EnsureUsableViewport(window);
        _log($"{reason}：Ctrl+A 全选。");
        await _mouse.SendCtrlAAsync(window.Handle, cancellationToken);
        await Task.Delay(80, cancellationToken);
        _log($"{reason}：Delete 清空。");
        await _mouse.SendDeleteAsync(window.Handle, cancellationToken);
        await Task.Delay(80, cancellationToken);
        _log($"{reason}：Ctrl+V 粘贴。");
        await _mouse.SendCtrlVAsync(window.Handle, cancellationToken);
    }
}

public readonly record struct RegionCapture(
    BitmapSource Image,
    ScreenRect ScreenRect,
    int LogicalWidth,
    int LogicalHeight,
    CaptureGeometry Geometry);
