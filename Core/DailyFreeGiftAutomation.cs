using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日免费礼包：主页 → 商店 → お得パック → デイリー無料パック → 購入 → OK → 回主页。
/// 已领取（有标题但无購入弹窗）视为当天完成。
/// </summary>
public sealed class DailyFreeGiftAutomation
{
    private const int AfterHomeDelayMs = 700;
    private const int AfterShopOpenDelayMs = 1000;
    private const int AfterTabDelayMs = 900;
    private const int AfterItemClickDelayMs = 900;
    private const int AfterPurchaseDelayMs = 900;
    private const int AfterOkDelayMs = 700;
    private const int RecognizeTimeoutMs = 8000;
    private const int PurchaseTimeoutMs = 4000;
    private const double OtokuThreshold = 0.72;
    private const double TitleThreshold = 0.72;
    private const double PurchaseThreshold = 0.70;
    private const double OkThreshold = 0.70;
    /// <summary>礼包「毎日5時更新」。</summary>
    private const int ResetHour = 5;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _otokuMatcher;
    private readonly TemplateMatcher _titleMatcher;
    private readonly TemplateMatcher _purchaseMatcher;
    private readonly TemplateMatcher _okMatcher;
    private readonly TemplateMatcher _okMatcherAlt;

    public DailyFreeGiftAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _otokuMatcher = TemplateAssets.Load("shop-otoku-pack.png");
        _titleMatcher = TemplateAssets.Load("daily-free-gift-title.png");
        _purchaseMatcher = TemplateAssets.Load("shop-purchase.png");
        _okMatcher = TemplateAssets.Load("settlement-confirm.png");
        _okMatcherAlt = TemplateAssets.Load("daily-shop-ok.png");
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = DateTime.Now;
        string dayKey = CurrentGiftDayKey(now);
        if (QuotaFilledThisDay(_config.LastDailyFreeGiftDay, now))
        {
            _log($"每日免费礼包：今日（{dayKey}）已领过，等到 {NextReset(now):MM-dd HH:mm} 刷新。");
            return;
        }

        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"每日免费礼包：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return;
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"每日免费礼包：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        await Task.Delay(AfterHomeDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        _log($"每日免费礼包：点击商店入口（{_config.DailyShopEntryClick.X},{_config.DailyShopEntryClick.Y}）。");
        window = await _screen.ClickAsync(window, _config.DailyShopEntryClick, "商店入口", cancellationToken);
        await Task.Delay(AfterShopOpenDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        window = await ClickOtokuPackAsync(window, cancellationToken);
        await Task.Delay(AfterTabDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        bool claimed = await TryClaimFreeGiftAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        if (claimed)
        {
            _config.LastDailyFreeGiftDay = dayKey;
            ConfigStore.Save(_config);
            _log($"每日免费礼包：已记录今日完成（{dayKey}）。");
        }
        else
        {
            _log("每日免费礼包：未能确认领取（可能尚未刷新或界面变化）。");
        }

        _log("每日免费礼包：返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日免费礼包：结束。");
    }

    private async Task<GameWindow> ClickOtokuPackAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("每日免费礼包：识别「お得パック」。");
        TemplateProbeResult otoku = await _screen.WaitForProbeAsync(
            window,
            _otokuMatcher,
            _config.DailyFreeGiftOtokuTopLeft,
            _config.DailyFreeGiftOtokuSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: OtokuThreshold);
        if (otoku.IsMatch)
        {
            _log($"每日免费礼包：已识别「お得パック」（{otoku.Score:F4}），点击匹配中心。");
            await _screen.ClickProbeAsync(window, otoku, "お得パック", cancellationToken, settleDelayMs: 200);
            return _screen.Refresh(window);
        }

        _log($"每日免费礼包：未识别「お得パック」（最高 {otoku.Score:F4}），改用固定点击（{_config.DailyFreeGiftOtokuClick.X},{_config.DailyFreeGiftOtokuClick.Y}）。");
        return await _screen.ClickAsync(
            window, _config.DailyFreeGiftOtokuClick, "お得パック", cancellationToken);
    }

    private async Task<bool> TryClaimFreeGiftAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("每日免费礼包：识别「デイリー無料パック」。");
        TemplateProbeResult title = await _screen.WaitForProbeAsync(
            window,
            _titleMatcher,
            _config.DailyFreeGiftTitleTopLeft,
            _config.DailyFreeGiftTitleSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: TitleThreshold);
        if (!title.IsMatch)
        {
            _log($"每日免费礼包：未识别到标题（最高 {title.Score:F4}）。");
            return false;
        }

        // 点标题中心偏下（価格/無料条），若已领取则通常无弹窗。
        var click = new System.Windows.Point(
            title.Center.X + _config.DailyFreeGiftTitleClickOffset.X,
            title.Center.Y + _config.DailyFreeGiftTitleClickOffset.Y);
        _log($"每日免费礼包：已识别标题（{title.Score:F4}），点击（{click.X:F0},{click.Y:F0}）。");
        await _screen.ClickScreenAsync(window, click, cancellationToken);
        await Task.Delay(AfterItemClickDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        _log("每日免费礼包：等待「購入」确认弹窗。");
        TemplateProbeResult purchase = await _screen.WaitForProbeAsync(
            window,
            _purchaseMatcher,
            _config.DailyFreeGiftPurchaseTopLeft,
            _config.DailyFreeGiftPurchaseSize,
            cancellationToken,
            timeoutMs: PurchaseTimeoutMs,
            matchThreshold: PurchaseThreshold);
        if (!purchase.IsMatch)
        {
            _log($"每日免费礼包：无「購入」弹窗（最高 {purchase.Score:F4}），视为今日已领取。");
            return true;
        }

        _log($"每日免费礼包：已识别「購入」（{purchase.Score:F4}），点击确认。");
        await _screen.ClickProbeAsync(window, purchase, "購入", cancellationToken, settleDelayMs: AfterPurchaseDelayMs);
        window = _screen.Refresh(window);

        await TryDismissOkAsync(window, cancellationToken);
        return true;
    }

    private async Task TryDismissOkAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("每日免费礼包：等待领取后 OK。");
        TemplateProbeResult ok = await _screen.WaitForProbeAsync(
            window,
            _okMatcher,
            _config.DailyFreeGiftOkTopLeft,
            _config.DailyFreeGiftOkSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: OkThreshold);
        if (!ok.IsMatch)
        {
            ok = await _screen.WaitForProbeAsync(
                window,
                _okMatcherAlt,
                _config.DailyFreeGiftOkTopLeft,
                _config.DailyFreeGiftOkSize,
                cancellationToken,
                timeoutMs: 2500,
                matchThreshold: OkThreshold);
        }

        if (!ok.IsMatch)
        {
            _log($"每日免费礼包：未出现 OK（最高 {ok.Score:F4}），可能无需确认。");
            return;
        }

        _log($"每日免费礼包：已识别 OK（{ok.Score:F4}），点击。");
        await _screen.ClickProbeAsync(window, ok, "OK", cancellationToken, settleDelayMs: AfterOkDelayMs);
    }

    public static DateOnly CurrentGiftDay(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime date = local.Date;
        if (local.Hour < ResetHour)
            date = date.AddDays(-1);
        return DateOnly.FromDateTime(date);
    }

    public static string CurrentGiftDayKey(DateTime now) =>
        CurrentGiftDay(now).ToString("yyyy-MM-dd");

    public static bool QuotaFilledThisDay(string? lastDayKey, DateTime now) =>
        !string.IsNullOrWhiteSpace(lastDayKey) &&
        string.Equals(lastDayKey, CurrentGiftDayKey(now), StringComparison.Ordinal);

    public static DateTime NextReset(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime todayReset = local.Date.AddHours(ResetHour);
        return local < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
