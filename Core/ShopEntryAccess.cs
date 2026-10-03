using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 从主页可靠进入商店。主页商店按钮贴近底边，固定旧坐标容易点到按钮上方；
/// 因此优先识别图标，并在点击后用目标商店页的模板确认确实完成了跳转。
/// </summary>
internal sealed class ShopEntryAccess
{
    private const double EntryThreshold = 0.68;
    private const int LandingTimeoutMs = 4000;
    private const int AfterEntryClickMs = 700;
    private static readonly ConfigPoint EntryTopLeft = new(1680, 900);
    private static readonly ConfigSize EntrySize = new(240, 180);

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
        ConfigPoint? shopMenuClick = null)
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
            if (entry.IsMatch)
            {
                _log($"{taskName}：已识别主页商店图标（{entry.Score:F4}），点击匹配中心（第 {attempt}/2 次）。");
                await _screen.ClickProbeAsync(window, entry, "商店入口", cancellationToken, settleDelayMs: 150);
            }
            else
            {
                _log($"{taskName}：商店图标未命中（最高 {entry.Score:F4}），点击校正后的兜底坐标 " +
                     $"（{_config.DailyShopEntryClick.X},{_config.DailyShopEntryClick.Y}，第 {attempt}/2 次）。");
                window = await _screen.ClickAsync(
                    window, _config.DailyShopEntryClick, "商店入口兜底", cancellationToken);
            }

            await Task.Delay(AfterEntryClickMs, cancellationToken);
            window = _screen.Refresh(window);
            if (shopMenuClick is ConfigPoint menuClick)
            {
                _log($"{taskName}：商店选择层已打开，点击目标商店入口（{menuClick.X},{menuClick.Y}）。");
                window = await _screen.ClickAsync(
                    window, menuClick, "商店选择层目标入口", cancellationToken);
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
