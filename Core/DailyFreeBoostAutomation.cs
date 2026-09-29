using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日免费加速：主页采矿小人 → 採掘 → 反复点「0時短受取」→ 确认弹窗点「実行」直到无免费次数。
/// 仅匹配费用为 0 的按钮，不会点付费加速。
/// </summary>
public sealed class DailyFreeBoostAutomation
{
    private const int AfterBoostDelayMs = 900;
    private const int AfterExecuteDelayMs = 1200;
    private const int RecognizeTimeoutMs = 8000;
    private const int MaxBoostRounds = 6;
    private const double BoostThreshold = 0.68;
    private const double ExecuteThreshold = 0.70;
    private const int ResetHour = 5;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _boostMatcher;
    private readonly TemplateMatcher _executeMatcher;

    public DailyFreeBoostAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _boostMatcher = TemplateAssets.Load("mining-boost-free.png");
        _executeMatcher = TemplateAssets.Load("mining-boost-execute.png");
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = DateTime.Now;
        string dayKey = CurrentBoostDayKey(now);
        if (QuotaFilledThisDay(_config.LastDailyFreeBoostDay, now))
        {
            _log($"每日免费加速：今日（{dayKey}）已用完，等到 {NextReset(now):MM-dd HH:mm} 刷新。");
            return;
        }

        var mining = new MiningPopupAccess(_config, _screen, _log, "每日免费加速");
        (GameWindow window, bool opened) = await mining.EnsureHomeAndOpenAsync(cancellationToken);
        if (!opened)
        {
            _log("每日免费加速：未能打开採掘弹窗，不记今日完成。");
            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
            _log("每日免费加速：结束。");
            return;
        }

        int clicked = await BoostUntilExhaustedAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        if (clicked > 0)
        {
            _config.LastDailyFreeBoostDay = dayKey;
            ConfigStore.Save(_config);
            _log($"每日免费加速：已点 {clicked} 次免费加速，记录今日完成（{dayKey}）。");
        }
        else
        {
            _log("每日免费加速：未成功点到免费加速，不记今日完成。");
        }

        _log("每日免费加速：返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日免费加速：结束。");
    }

    private async Task<int> BoostUntilExhaustedAsync(GameWindow window, CancellationToken cancellationToken)
    {
        int clicked = 0;
        for (int round = 1; round <= MaxBoostRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            TemplateProbeResult boost = await _screen.WaitForProbeAsync(
                window,
                _boostMatcher,
                _config.MiningBoostTopLeft,
                _config.MiningBoostSize,
                cancellationToken,
                timeoutMs: round == 1 ? RecognizeTimeoutMs : 2500,
                matchThreshold: BoostThreshold);
            if (!boost.IsMatch)
            {
                _log($"每日免费加速：第 {round} 轮未识别「0時短受取」（最高 {boost.Score:F4}），免费次数已用尽或不存在。");
                break;
            }

            _log($"每日免费加速：第 {round} 轮点击「0時短受取」（{boost.Score:F4}）。");
            await _screen.ClickProbeAsync(window, boost, "0時短受取", cancellationToken, settleDelayMs: AfterBoostDelayMs);
            window = _screen.Refresh(window);

            if (!await TryConfirmExecuteAsync(window, cancellationToken))
            {
                _log("每日免费加速：未点到确认「実行」，停止以免误关弹窗。");
                break;
            }

            clicked++;
            window = _screen.Refresh(window);
        }

        return clicked;
    }

    private async Task<bool> TryConfirmExecuteAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult exec = await _screen.WaitForProbeAsync(
            window,
            _executeMatcher,
            _config.MiningBoostExecuteTopLeft,
            _config.MiningBoostExecuteSize,
            cancellationToken,
            timeoutMs: 4000,
            matchThreshold: ExecuteThreshold);
        if (exec.IsMatch)
        {
            _log($"每日免费加速：点击「実行」（{exec.Score:F4}）。");
            await _screen.ClickProbeAsync(window, exec, "実行", cancellationToken, settleDelayMs: AfterExecuteDelayMs);
            return true;
        }

        // 兜底：确认弹窗右侧粉钮中心（1080p）
        var fallback = new ConfigPoint(1089, 969);
        _log($"每日免费加速：未识别「実行」（最高 {exec.Score:F4}），改用固定点击（{fallback.X},{fallback.Y}）。");
        await _screen.ClickAsync(window, fallback, "実行", cancellationToken);
        await Task.Delay(AfterExecuteDelayMs, cancellationToken);
        return true;
    }

    public static DateOnly CurrentBoostDay(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime date = local.Date;
        if (local.Hour < ResetHour)
            date = date.AddDays(-1);
        return DateOnly.FromDateTime(date);
    }

    public static string CurrentBoostDayKey(DateTime now) =>
        CurrentBoostDay(now).ToString("yyyy-MM-dd");

    public static bool QuotaFilledThisDay(string? lastDayKey, DateTime now) =>
        !string.IsNullOrWhiteSpace(lastDayKey) &&
        string.Equals(lastDayKey, CurrentBoostDayKey(now), StringComparison.Ordinal);

    public static DateTime NextReset(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime todayReset = local.Date.AddHours(ResetHour);
        return local < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
