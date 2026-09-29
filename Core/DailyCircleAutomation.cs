using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日社团：主页 → サークル → ミッション → 一括受取已完成奖励 → 回主页。
/// 只领已完成项，不点「挑戦」、不购买、不代做迷宫/扭蛋等。
/// </summary>
public sealed class DailyCircleAutomation
{
    private const int AfterHomeDelayMs = 700;
    private const int AfterNavDelayMs = 1200;
    private const int AfterOpenDelayMs = 1000;
    private const int AfterClaimDelayMs = 900;
    private const int AfterOkDelayMs = 700;
    private const int RecognizeTimeoutMs = 8000;
    /// <summary>社团一括受取按钮常亮着；点了无 OK 就停，最多 2 轮。</summary>
    private const int MaxClaimRounds = 2;
    private const double NavThreshold = 0.55;
    private const double TitleThreshold = 0.70;
    private const double MissionEntryThreshold = 0.70;
    private const double ClaimThreshold = 0.70;
    private const double OkThreshold = 0.70;
    private const int ResetHour = 5;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _navMatcher;
    private readonly TemplateMatcher _titleMatcher;
    private readonly TemplateMatcher _missionEntryMatcher;
    private readonly TemplateMatcher _claimMatcher;
    private readonly TemplateMatcher _okMatcher;
    private readonly TemplateMatcher _okMatcherAlt;

    public DailyCircleAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _navMatcher = TemplateAssets.Load("nav-circle.png");
        _titleMatcher = TemplateAssets.Load("circle-title.png");
        _missionEntryMatcher = TemplateAssets.Load("circle-mission-entry.png");
        _claimMatcher = TemplateAssets.Load("mission-claim-all.png");
        _okMatcher = TemplateAssets.Load("settlement-confirm.png");
        _okMatcherAlt = TemplateAssets.Load("daily-shop-ok.png");
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = DateTime.Now;
        string dayKey = CurrentCircleDayKey(now);
        if (QuotaFilledThisDay(_config.LastDailyCircleDay, now))
        {
            _log($"每日社团：今日（{dayKey}）已领完，等到 {NextReset(now):MM-dd HH:mm} 刷新。");
            return;
        }

        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"每日社团：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return;
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"每日社团：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        await Task.Delay(AfterHomeDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        window = await OpenCircleAsync(window, cancellationToken);
        await Task.Delay(AfterNavDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        window = await OpenCircleMissionsAsync(window, cancellationToken);
        await Task.Delay(AfterOpenDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        int claimed = await ClaimRewardsAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        if (claimed > 0)
        {
            _config.LastDailyCircleDay = dayKey;
            ConfigStore.Save(_config);
            _log($"每日社团：已领取完成（{dayKey}，命中一括受取 {claimed} 次）。");
        }
        else
        {
            // 无可领奖励时不写完成日：其它日课完成后还可再跑本任务领奖。
            _log("每日社团：当前无可领奖励（未点挑戦、未购买），不记今日完成。");
        }

        _log("每日社团：返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日社团：结束。");
    }

    private async Task<GameWindow> OpenCircleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("每日社团：识别底栏「サークル」。");
        TemplateProbeResult nav = await _screen.WaitForProbeAsync(
            window,
            _navMatcher,
            _config.DailyCircleNavTopLeft,
            _config.DailyCircleNavSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: NavThreshold);
        if (nav.IsMatch)
        {
            _log($"每日社团：已识别底栏（{nav.Score:F4}），点击。");
            await _screen.ClickProbeAsync(window, nav, "サークル", cancellationToken, settleDelayMs: 200);
        }
        else
        {
            _log($"每日社团：未识别底栏（最高 {nav.Score:F4}），改用固定点击（{_config.DailyCircleNavClick.X},{_config.DailyCircleNavClick.Y}）。");
            await _screen.ClickAsync(window, _config.DailyCircleNavClick, "サークル", cancellationToken);
        }

        window = _screen.Refresh(window);
        TemplateProbeResult title = await _screen.WaitForProbeAsync(
            window,
            _titleMatcher,
            _config.DailyCircleTitleTopLeft,
            _config.DailyCircleTitleSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: TitleThreshold);
        if (title.IsMatch)
            _log($"每日社团：已进入サークル页（{title.Score:F4}）。");
        else
            _log($"每日社团：标题未识别（最高 {title.Score:F4}），继续尝试打开ミッション。");

        return _screen.Refresh(window);
    }

    private async Task<GameWindow> OpenCircleMissionsAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("每日社团：识别「ミッション」入口。");
        TemplateProbeResult entry = await _screen.WaitForProbeAsync(
            window,
            _missionEntryMatcher,
            _config.DailyCircleMissionEntryTopLeft,
            _config.DailyCircleMissionEntrySize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: MissionEntryThreshold);
        if (entry.IsMatch)
        {
            _log($"每日社团：已识别ミッション（{entry.Score:F4}），点击。");
            await _screen.ClickProbeAsync(window, entry, "サークルミッション", cancellationToken, settleDelayMs: 200);
        }
        else
        {
            _log($"每日社团：未识别ミッション（最高 {entry.Score:F4}），改用固定点击（{_config.DailyCircleMissionEntryClick.X},{_config.DailyCircleMissionEntryClick.Y}）。");
            await _screen.ClickAsync(window, _config.DailyCircleMissionEntryClick, "サークルミッション", cancellationToken);
        }

        return _screen.Refresh(window);
    }

    private async Task<int> ClaimRewardsAsync(GameWindow window, CancellationToken cancellationToken)
    {
        int hits = 0;
        for (int round = 1; round <= MaxClaimRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            TemplateProbeResult claim = await _screen.WaitForProbeAsync(
                window,
                _claimMatcher,
                _config.DailyMissionsClaimTopLeft,
                _config.DailyMissionsClaimSize,
                cancellationToken,
                timeoutMs: 2500,
                matchThreshold: ClaimThreshold);
            if (!claim.IsMatch)
            {
                _log($"每日社团：第 {round} 轮无一括受取（最高 {claim.Score:F4}），结束领奖。");
                break;
            }

            hits++;
            _log($"每日社团：第 {round} 轮点击「一括受取」（{claim.Score:F4}）。");
            await _screen.ClickProbeAsync(window, claim, "一括受取", cancellationToken, settleDelayMs: AfterClaimDelayMs);
            window = _screen.Refresh(window);
            // 按钮常亮：点了若无奖励弹窗 OK，说明已无可领，立刻停（勿空转多轮）。
            if (!await TryDismissOkAsync(window, cancellationToken))
            {
                _log("每日社团：点了一括受取但无 OK，视为已领完。");
                break;
            }
        }

        return hits;
    }

    private async Task<bool> TryDismissOkAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult ok = await _screen.WaitForProbeAsync(
            window,
            _okMatcher,
            _config.DailyMissionsOkTopLeft,
            _config.DailyMissionsOkSize,
            cancellationToken,
            timeoutMs: 2500,
            matchThreshold: OkThreshold);
        if (!ok.IsMatch)
        {
            ok = await _screen.WaitForProbeAsync(
                window,
                _okMatcherAlt,
                _config.DailyMissionsOkTopLeft,
                _config.DailyMissionsOkSize,
                cancellationToken,
                timeoutMs: 1500,
                matchThreshold: OkThreshold);
        }

        if (!ok.IsMatch)
            return false;

        _log($"每日社团：点击 OK（{ok.Score:F4}）。");
        await _screen.ClickProbeAsync(window, ok, "OK", cancellationToken, settleDelayMs: AfterOkDelayMs);
        return true;
    }

    public static DateOnly CurrentCircleDay(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime date = local.Date;
        if (local.Hour < ResetHour)
            date = date.AddDays(-1);
        return DateOnly.FromDateTime(date);
    }

    public static string CurrentCircleDayKey(DateTime now) =>
        CurrentCircleDay(now).ToString("yyyy-MM-dd");

    public static bool QuotaFilledThisDay(string? lastDayKey, DateTime now) =>
        !string.IsNullOrWhiteSpace(lastDayKey) &&
        string.Equals(lastDayKey, CurrentCircleDayKey(now), StringComparison.Ordinal);

    public static DateTime NextReset(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime todayReset = local.Date.AddHours(ResetHour);
        return local < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
