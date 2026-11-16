using System.Diagnostics;
using System.Windows;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 处理启动后进入主界面：标题页「TOUCH TO START」及后续过渡屏需连点多次，
/// 再清弹窗直到底栏「クエスト」出现。
/// </summary>
public sealed class TitleScreenEnter
{
    private const double TouchThreshold = 0.70;
    private const double WorldBarThreshold = 0.70;
    private const double QuestThreshold = 0.80;
    private const int DetectTimeoutMs = 3500;
    private const int AfterClickSettleMs = 900;
    private const int BetweenClickPollMs = 700;
    private const int MaxClickAttempts = 12;
    private const int LaunchOverallTimeoutMs = 120000;
    private const int PostTitleClickGapMs = 1800;

    /// <summary>1080p：标题页下半「TOUCH TO START」搜索区。</summary>
    private static readonly ConfigPoint TouchRoiTopLeft = new(200, 760);
    private static readonly ConfigSize TouchRoiSize = new(1520, 300);

    /// <summary>1080p：标题页「世界xx-xx」粉条搜索区。</summary>
    private static readonly ConfigPoint WorldBarRoiTopLeft = new(500, 700);
    private static readonly ConfigSize WorldBarRoiSize = new(920, 160);

    /// <summary>1080p：点标题/过渡屏的固定落点（中央偏下，避开底栏）。</summary>
    private static readonly ConfigPoint TitleClick = new(960, 860);

    /// <summary>1080p：部分过渡屏点屏幕正中更稳。</summary>
    private static readonly ConfigPoint CenterClick = new(960, 540);

    private static readonly string[] TouchPhrases =
    [
        "TOUCH TO START",
        "TOUCHTOSTART",
        "TOUCH TO",
        "TO START",
        "TOUCH"
    ];

    private static readonly string[] WorldPhrases =
    [
        "世界01",
        "世界"
    ];

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly PromoPopupDismisser _promo;
    private readonly TemplateMatcher _touchMatcher;
    private readonly TemplateMatcher _worldBarMatcher;
    private readonly TemplateMatcher _questMatcher;
    private readonly TemplateMatcher _hudHomeMatcher;

    public TitleScreenEnter(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _promo = new PromoPopupDismisser(config, screen, log);
        _touchMatcher = TemplateAssets.Load("title-touch-to-start.png");
        _worldBarMatcher = TemplateAssets.Load("title-world-bar.png");
        _questMatcher = TemplateAssets.Load("quest.png");
        _hudHomeMatcher = TemplateAssets.Load("hud-home.png");
    }

    /// <summary>
    /// 若已在主页返回 true；若识别到标题页则连点进入并等到主页；
    /// 既不是主页也不是标题页则返回 false（留给后续逻辑处理）。
    /// </summary>
    public Task<bool> TryPassAsync(GameWindow window, CancellationToken cancellationToken) =>
        EnsureHomeAsync(window, cancellationToken, launchMode: false);

    /// <summary>
    /// 启动/截图器就绪后调用：允许在未见标题特征时也做有限次中央点击，
    /// 以覆盖 logo/黑屏/过渡动画等需连点的阶段。
    /// </summary>
    public Task<bool> EnsureHomeAfterLaunchAsync(GameWindow window, CancellationToken cancellationToken) =>
        EnsureHomeAsync(window, cancellationToken, launchMode: true);

    private async Task<bool> EnsureHomeAsync(
        GameWindow window, CancellationToken cancellationToken, bool launchMode)
    {
        cancellationToken.ThrowIfCancellationRequested();
        window = _screen.Refresh(window);
        await _screen.FocusAsync(window.Handle, cancellationToken);

        if (await IsTrueHomeAsync(window, cancellationToken))
        {
            if (launchMode)
                _log("启动进入：已在主界面。");
            return true;
        }

        TitleHit? hit = await DetectTitleAsync(window, cancellationToken);
        bool seenTitle = hit is not null;
        if (!seenTitle && !launchMode)
            return false;

        if (seenTitle)
            _log($"标题页：识别到「{hit!.Value.Label}」（{hit.Value.Score:F3}），开始连点进入。");
        else
            _log("启动进入：未见标题特征，开始过渡连点并等待主界面。");

        var overall = Stopwatch.StartNew();
        int clicks = 0;
        DateTime nextClickUtc = DateTime.MinValue;

        while (overall.ElapsedMilliseconds < LaunchOverallTimeoutMs && clicks < MaxClickAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            await _screen.FocusAsync(window.Handle, cancellationToken);

            if (await IsTrueHomeAsync(window, cancellationToken))
            {
                await _promo.DismissAllAsync(window, cancellationToken);
                window = _screen.Refresh(window);
                if (await IsTrueHomeAsync(window, cancellationToken))
                {
                    _log(seenTitle ? "标题页：已进入主界面。" : "启动进入：已进入主界面。");
                    return true;
                }
            }

            // 主页前的宣传/お知らせ可顺手清；标题页上的 HUD 误匹配由 PromoPopupDismisser 自行抑制。
            if (clicks > 0 && !await IsLikelyTitleAsync(window, cancellationToken))
            {
                int closed = await _promo.DismissAllAsync(window, cancellationToken);
                window = _screen.Refresh(window);
                if (closed > 0 && await IsTrueHomeAsync(window, cancellationToken))
                {
                    _log("启动进入：清弹窗后已见主界面。");
                    return true;
                }
            }

            TitleHit? now = await DetectTitleQuickAsync(window, cancellationToken)
                ?? await TryOcrTitleAsync(window, cancellationToken);
            if (now is not null)
            {
                seenTitle = true;
                hit = now;
            }

            bool shouldClick = DateTime.UtcNow >= nextClickUtc &&
                (seenTitle || launchMode);
            if (!shouldClick)
            {
                await Task.Delay(BetweenClickPollMs, cancellationToken);
                continue;
            }

            clicks++;
            ConfigPoint clickPoint = PickClickPoint(clicks, hit);
            if (hit?.ScreenClick is Point screenClick && clicks <= 2)
            {
                _log($"标题页：点击 screen({screenClick.X:F0},{screenClick.Y:F0})（{clicks}/{MaxClickAttempts}）。");
                await _screen.ClickScreenAsync(window, screenClick, cancellationToken, parkCursor: false);
            }
            else
            {
                window = await _screen.ClickAsync(
                    window, clickPoint, $"启动进入 {clicks}/{MaxClickAttempts}", cancellationToken,
                    parkCursor: false);
            }

            await Task.Delay(AfterClickSettleMs, cancellationToken);
            _screen.ParkCursorAway(window);
            nextClickUtc = DateTime.UtcNow.AddMilliseconds(PostTitleClickGapMs);
        }

        window = _screen.Refresh(window);
        if (await IsTrueHomeAsync(window, cancellationToken))
        {
            _log(seenTitle ? "标题页：已进入主界面。" : "启动进入：已进入主界面。");
            return true;
        }

        _log(seenTitle
            ? $"标题页：连点 {clicks} 次后仍未见主界面「クエスト」。"
            : $"启动进入：连点 {clicks} 次后仍未见主界面「クエスト」。");
        return false;
    }

    private static ConfigPoint PickClickPoint(int clickIndex, TitleHit? hit)
    {
        // 奇数点偏下（TOUCH 文案），偶数点正中（过渡全屏点击）。
        if (clickIndex % 2 == 0)
            return CenterClick;
        return hit is not null ? TitleClick : CenterClick;
    }

    private async Task<bool> IsLikelyTitleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TitleHit? quick = await DetectTitleQuickAsync(window, cancellationToken);
        return quick is not null;
    }

    private async Task<TitleHit?> DetectTitleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        TitleHit? best = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            TitleHit? quick = await DetectTitleQuickAsync(window, cancellationToken);
            if (quick is not null && (best is null || quick.Value.Score > best.Value.Score))
                best = quick;

            if (best is null || best.Value.Score < 0.85)
            {
                TitleHit? ocr = await TryOcrTitleAsync(window, cancellationToken);
                if (ocr is not null && (best is null || ocr.Value.Score > best.Value.Score))
                    best = ocr;
            }

            if (best is { Score: >= TouchThreshold })
                return best;

            if (timer.ElapsedMilliseconds < DetectTimeoutMs)
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
        while (timer.ElapsedMilliseconds < DetectTimeoutMs);

        return best is { Score: >= TouchThreshold } ? best : null;
    }

    private async Task<TitleHit?> TryOcrTitleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        RegionCapture region = _screen.CaptureRegion(window, TouchRoiTopLeft, TouchRoiSize);
        (double X, double Y, string Hit)? touch = await DigitOcrService.TryLocatePhraseAsync(
            region.Image, TouchPhrases, cancellationToken);
        if (touch is { } t)
        {
            var screen = new Point(region.ScreenRect.Left + t.X, region.ScreenRect.Top + t.Y);
            return new TitleHit($"OCR:{t.Hit}", 0.90, screen);
        }

        RegionCapture worldRegion = _screen.CaptureRegion(window, WorldBarRoiTopLeft, WorldBarRoiSize);
        (double X, double Y, string Hit)? world = await DigitOcrService.TryLocatePhraseAsync(
            worldRegion.Image, WorldPhrases, cancellationToken);
        if (world is { } w)
            return new TitleHit($"OCR:{w.Hit}", 0.82, null);

        return null;
    }

    private async Task<TitleHit?> DetectTitleQuickAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        ConfigSize touchSize = ScreenAutomation.EnsureFitsTemplate(TouchRoiSize, _touchMatcher);
        TemplateProbeResult touch = await _screen.ProbeAsync(
            window, _touchMatcher, TouchRoiTopLeft, touchSize,
            cancellationToken, TouchThreshold);
        if (touch.IsMatch)
            return new TitleHit("TOUCH TO START", touch.Score, touch.Center);

        ConfigSize worldSize = ScreenAutomation.EnsureFitsTemplate(WorldBarRoiSize, _worldBarMatcher);
        TemplateProbeResult world = await _screen.ProbeAsync(
            window, _worldBarMatcher, WorldBarRoiTopLeft, worldSize,
            cancellationToken, WorldBarThreshold);
        if (world.IsMatch)
            return new TitleHit("世界条", world.Score, null);

        return null;
    }

    /// <summary>与 HomePresence 一致：底栏クエスト可见且右上无返回主页钮。</summary>
    private async Task<bool> IsTrueHomeAsync(GameWindow window, CancellationToken cancellationToken)
    {
        ConfigSize size = ScreenAutomation.EnsureFitsTemplate(_config.FirstSearchSize, _questMatcher);
        TemplateProbeResult quest = await _screen.ProbeAsync(
            window, _questMatcher, _config.SearchTopLeft, size,
            cancellationToken, QuestThreshold);
        if (!quest.IsMatch)
            return false;

        TemplateProbeResult hudHome = await _screen.ProbeAsync(
            window, _hudHomeMatcher, new ConfigPoint(1420, 0), new ConfigSize(500, 260),
            cancellationToken, 0.78);
        return !hudHome.IsMatch;
    }

    private readonly record struct TitleHit(string Label, double Score, Point? ScreenClick);
}
