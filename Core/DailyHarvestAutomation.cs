using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日收菜：主页采矿小人 → 採掘 → 受取 → 回主页。
/// 必须先确认打开採掘弹窗；点粉钮「受取」后才记完成。
/// </summary>
public sealed class DailyHarvestAutomation
{
    private const int AfterClaimDelayMs = 900;
    private const int AfterOkDelayMs = 700;
    private const int RecognizeTimeoutMs = 8000;
    private const double ClaimThreshold = 0.68;
    private const double OkThreshold = 0.70;
    private const int ResetHour = 5;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _claimMatcher;
    private readonly TemplateMatcher _okMatcher;
    private readonly TemplateMatcher _okMatcherAlt;

    public DailyHarvestAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _claimMatcher = TemplateAssets.Load("mining-claim.png");
        _okMatcher = TemplateAssets.Load("settlement-confirm.png");
        _okMatcherAlt = TemplateAssets.Load("daily-shop-ok.png");
    }

    public async Task<TaskRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = DateTime.Now;
        string dayKey = CurrentHarvestDayKey(now);
        if (QuotaFilledThisDay(_config.LastDailyHarvestDay, now))
        {
            _log($"每日收菜：今日（{dayKey}）已收过，等到 {NextReset(now):MM-dd HH:mm} 刷新。");
            return TaskRunResult.Success("今日已收过");
        }

        var mining = new MiningPopupAccess(_config, _screen, _log, "每日收菜");
        (GameWindow window, bool opened) = await mining.EnsureHomeAndOpenAsync(cancellationToken);
        if (!opened)
        {
            _log("每日收菜：未能打开採掘弹窗，不记今日完成。请确认主页右下采矿小人可见。");
            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
            _log("每日收菜：结束。");
            return TaskRunResult.Fail("未能打开採掘弹窗");
        }

        bool claimed = await TryClaimAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        if (claimed)
        {
            _config.LastDailyHarvestDay = dayKey;
            ConfigStore.Save(_config);
            _log($"每日收菜：已记录今日完成（{dayKey}）。");
        }
        else
        {
            _log("每日收菜：已打开採掘但未点到「受取」，不记今日完成。");
        }

        _log("每日收菜：关闭採掘弹窗（关闭后即为主页）。");
        await mining.CloseAsync(window, cancellationToken);
        _log("每日收菜：结束。");
        return claimed
            ? TaskRunResult.Success()
            : TaskRunResult.Fail("未点到受取");
    }

    private async Task<bool> TryClaimAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("每日收菜：识别「受取」。");
        TemplateProbeResult claim = await _screen.WaitForProbeAsync(
            window,
            _claimMatcher,
            _config.MiningClaimTopLeft,
            _config.MiningClaimSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: ClaimThreshold);
        if (!claim.IsMatch)
        {
            _log($"每日收菜：未识别「受取」（最高 {claim.Score:F4}），不点击。");
            return false;
        }

        _log($"每日收菜：已识别「受取」（{claim.Score:F4}），点击。");
        await _screen.ClickProbeAsync(window, claim, "受取", cancellationToken, settleDelayMs: AfterClaimDelayMs);
        window = _screen.Refresh(window);
        await TryDismissOkAsync(window, cancellationToken);
        return true;
    }

    private async Task TryDismissOkAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult ok = await _screen.WaitForProbeAsync(
            window,
            _okMatcher,
            _config.MiningOkTopLeft,
            _config.MiningOkSize,
            cancellationToken,
            timeoutMs: 3500,
            matchThreshold: OkThreshold);
        if (!ok.IsMatch)
        {
            ok = await _screen.WaitForProbeAsync(
                window,
                _okMatcherAlt,
                _config.MiningOkTopLeft,
                _config.MiningOkSize,
                cancellationToken,
                timeoutMs: 2000,
                matchThreshold: OkThreshold);
        }

        if (!ok.IsMatch)
            return;

        _log($"每日收菜：点击 OK（{ok.Score:F4}）。");
        await _screen.ClickProbeAsync(window, ok, "OK", cancellationToken, settleDelayMs: AfterOkDelayMs);
    }

    public static DateOnly CurrentHarvestDay(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime date = local.Date;
        if (local.Hour < ResetHour)
            date = date.AddDays(-1);
        return DateOnly.FromDateTime(date);
    }

    public static string CurrentHarvestDayKey(DateTime now) =>
        CurrentHarvestDay(now).ToString("yyyy-MM-dd");

    public static bool QuotaFilledThisDay(string? lastDayKey, DateTime now) =>
        !string.IsNullOrWhiteSpace(lastDayKey) &&
        string.Equals(lastDayKey, CurrentHarvestDayKey(now), StringComparison.Ordinal);

    public static DateTime NextReset(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime todayReset = local.Date.AddHours(ResetHour);
        return local < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
