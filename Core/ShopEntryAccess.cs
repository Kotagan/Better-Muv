using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 从主页进入商店：只靠模板定位点击，不使用固定坐标兜底。
/// 底栏购物车 →（可选）枢纽内目标入口 → 用落地模板确认。
/// 清弹窗仅由失败处理负责，本路径不主动扫弹窗。
/// </summary>
internal sealed class ShopEntryAccess
{
    /// <summary>模板为无红点购物车；有/无红点均可。0.55 高于ガチャ等邻图标误检。</summary>
    private const double EntryThreshold = 0.55;
    private const int LandingTimeoutMs = 4000;
    private const int AfterEntryClickMs = 700;
    /// <summary>底栏「ショップ」购物车一带（1080p；略扩以覆盖红点偏移）。</summary>
    private static readonly ConfigPoint EntryTopLeft = new(1700, 920);
    private static readonly ConfigSize EntrySize = new(260, 170);

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _entryMatcher;

    public ShopEntryAccess(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _entryMatcher = TemplateAssets.Load("daily-shop-icon.png");
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
        }

        return (_screen.Refresh(window), false);
    }
}
