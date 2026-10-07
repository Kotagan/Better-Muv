using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 从主页进入商店：只靠模板定位点击，不使用固定坐标兜底。
/// 底栏购物车 →（可选）枢纽内目标入口 → 用落地模板确认。
/// </summary>
internal sealed class ShopEntryAccess
{
    /// <summary>购物车图标含红点时分会掉；0.60 仍高于底栏其它图标误检。</summary>
    private const double EntryThreshold = 0.60;
    private const int LandingTimeoutMs = 4000;
    private const int AfterEntryClickMs = 700;
    /// <summary>底栏「ショップ」购物车一带（1080p；含红点偏移）。</summary>
    private static readonly ConfigPoint EntryTopLeft = new(1720, 930);
    private static readonly ConfigSize EntrySize = new(220, 160);

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _entryMatcher;
    private readonly PromoPopupDismisser _promo;

    public ShopEntryAccess(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _entryMatcher = TemplateAssets.Load("daily-shop-icon.png");
        _promo = new PromoPopupDismisser(config, screen, log);
    }

    public async Task<(GameWindow Window, bool Opened)> OpenAsync(
        GameWindow window,
        string taskName,
        TemplateMatcher landingMatcher,
        ConfigPoint landingTopLeft,
        ConfigSize landingSize,
        double landingThreshold,
        CancellationToken cancellationToken,
        TemplateMatcher? portalMatcher = null,
        ConfigPoint? portalTopLeft = null,
        ConfigSize? portalSize = null,
        double portalThreshold = 0.65)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            TemplateProbeResult alreadyOpen = await _screen.ProbeAsync(
                window, landingMatcher, landingTopLeft, landingSize,
                cancellationToken, landingThreshold);
            if (alreadyOpen.IsMatch)
            {
                _log($"{taskName}：已确认商店页面（{alreadyOpen.Score:F4}）。");
                return (window, true);
            }

            TemplateProbeResult entry = await _screen.ProbeAsync(
                window, _entryMatcher, EntryTopLeft, EntrySize,
                cancellationToken, EntryThreshold);
            if (!entry.IsMatch)
            {
                _log($"{taskName}：商店图标未命中（最高 {entry.Score:F4}），不点击（第 {attempt}/2 次）。");
                await _promo.DismissAllAsync(window, cancellationToken);
                continue;
            }

            _log($"{taskName}：已识别主页商店图标（{entry.Score:F4}），点击匹配中心（第 {attempt}/2 次）。");
            await _screen.ClickProbeAsync(window, entry, "商店入口", cancellationToken, settleDelayMs: 150);
            await Task.Delay(AfterEntryClickMs, cancellationToken);
            window = _screen.Refresh(window);

            if (portalMatcher is not null && portalTopLeft is { } pTl && portalSize is { } pSz)
            {
                TemplateProbeResult portal = await _screen.WaitForProbeAsync(
                    window, portalMatcher, pTl, pSz,
                    cancellationToken, timeoutMs: 3500, matchThreshold: portalThreshold);
                if (!portal.IsMatch)
                {
                    _log($"{taskName}：商店枢纽目标入口未命中（最高 {portal.Score:F4}），不点击。");
                    await _promo.DismissAllAsync(window, cancellationToken);
                    continue;
                }

                _log($"{taskName}：已识别商店枢纽目标入口（{portal.Score:F4}），点击匹配中心。");
                await _screen.ClickProbeAsync(window, portal, "商店枢纽目标入口", cancellationToken, settleDelayMs: 200);
                await Task.Delay(AfterEntryClickMs, cancellationToken);
                window = _screen.Refresh(window);
            }

            TemplateProbeResult landing = await _screen.WaitForProbeAsync(
                window, landingMatcher, landingTopLeft, landingSize,
                cancellationToken, LandingTimeoutMs, landingThreshold);
            if (landing.IsMatch)
            {
                _log($"{taskName}：进入商店成功（页面标记 {landing.Score:F4}）。");
                return (window, true);
            }

            _log($"{taskName}：点击后未确认商店页面（最高 {landing.Score:F4}）。");
            await _promo.DismissAllAsync(window, cancellationToken);
        }

        return (_screen.Refresh(window), false);
    }
}
