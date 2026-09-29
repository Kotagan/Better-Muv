using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日任务：主页 → ミッション → デイリー → 一括受取（可领则领）→ 已清完则记日 → 回主页。
/// 只领奖，不购买。未达成内容由其它任务完成。缺模板可后补。
/// </summary>
public sealed class DailyMissionsAutomation
{
    private const int AfterHomeDelayMs = 700;
    private const int AfterOpenDelayMs = 1000;
    private const int AfterClaimDelayMs = 900;
    private const int AfterOkDelayMs = 700;
    private const int RecognizeTimeoutMs = 8000;
    private const int MaxClaimRounds = 3;
    private const double EntryThreshold = 0.70;
    private const double TitleThreshold = 0.70;
    private const double ClearedThreshold = 0.72;
    private const double ClaimThreshold = 0.70;
    private const double OkThreshold = 0.70;
    private const int ResetHour = 5;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _entryMatcher;
    private readonly TemplateMatcher _titleMatcher;
    private readonly TemplateMatcher _clearedMatcher;
    private readonly TemplateMatcher _claimMatcher;
    private readonly TemplateMatcher _okMatcher;
    private readonly TemplateMatcher _okMatcherAlt;

    public DailyMissionsAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _entryMatcher = TemplateAssets.Load("mission-entry.png");
        _titleMatcher = TemplateAssets.Load("mission-title.png");
        _clearedMatcher = TemplateAssets.Load("mission-daily-cleared.png");
        _claimMatcher = TemplateAssets.Load("mission-claim-all.png");
        _okMatcher = TemplateAssets.Load("settlement-confirm.png");
        _okMatcherAlt = TemplateAssets.Load("daily-shop-ok.png");
    }

    public async Task<TaskRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = DateTime.Now;
        string dayKey = CurrentMissionDayKey(now);
        if (QuotaFilledThisDay(_config.LastDailyMissionsDay, now))
        {
            _log($"每日任务：今日（{dayKey}）已领完，等到 {NextReset(now):MM-dd HH:mm} 刷新。");
            return TaskRunResult.Success("今日已领完");
        }

        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"每日任务：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return TaskRunResult.Fail("未能将游戏置于前台");
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"每日任务：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        var home = new HomePresence(_config, _screen, _log);
        (window, bool onHome) = await home.EnsureAsync(window, "每日任务", cancellationToken);
        if (!onHome)
            return TaskRunResult.Fail("未能回到主界面");

        await Task.Delay(AfterHomeDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        window = await OpenMissionsAsync(window, cancellationToken);
        await Task.Delay(AfterOpenDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        bool done = await ClaimUntilClearedAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        if (done)
        {
            _config.LastDailyMissionsDay = dayKey;
            ConfigStore.Save(_config);
            _log($"每日任务：已记录今日完成（{dayKey}）。");
        }
        else
        {
            _log("每日任务：未能确认清完（可能仍有未达成项，或模板待补）。");
        }

        _log("每日任务：返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日任务：结束。");
        return done
            ? TaskRunResult.Success()
            : TaskRunResult.Fail("未能确认清完");
    }

    private async Task<GameWindow> OpenMissionsAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("每日任务：识别主页「ミッション」。");
        TemplateProbeResult entry = await _screen.WaitForProbeAsync(
            window,
            _entryMatcher,
            _config.DailyMissionsEntryTopLeft,
            _config.DailyMissionsEntrySize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: EntryThreshold);
        if (entry.IsMatch)
        {
            _log($"每日任务：已识别入口（{entry.Score:F4}），点击。");
            await _screen.ClickProbeAsync(window, entry, "ミッション", cancellationToken, settleDelayMs: 200);
        }
        else
        {
            _log($"每日任务：未识别入口（最高 {entry.Score:F4}），改用固定点击（{_config.DailyMissionsEntryClick.X},{_config.DailyMissionsEntryClick.Y}）。");
            await _screen.ClickAsync(window, _config.DailyMissionsEntryClick, "ミッション", cancellationToken);
        }

        window = _screen.Refresh(window);
        TemplateProbeResult title = await _screen.WaitForProbeAsync(
            window,
            _titleMatcher,
            _config.DailyMissionsTitleTopLeft,
            _config.DailyMissionsTitleSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: TitleThreshold);
        if (title.IsMatch)
            _log($"每日任务：已进入ミッション页（{title.Score:F4}）。");
        else
            _log($"每日任务：标题未识别（最高 {title.Score:F4}），继续尝试领奖。");

        // 点左侧「デイリー」兜底（已在该页时通常无害）。
        await _screen.ClickAsync(window, _config.DailyMissionsDailyTabClick, "デイリー页签", cancellationToken);
        return _screen.Refresh(window);
    }

    private async Task<bool> ClaimUntilClearedAsync(GameWindow window, CancellationToken cancellationToken)
    {
        for (int round = 1; round <= MaxClaimRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            if (await IsClearedAsync(window, cancellationToken))
            {
                _log("每日任务：已识别「デイリーミッションをクリアしました」。");
                return true;
            }

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
                _log($"每日任务：第 {round} 轮未见一括受取（最高 {claim.Score:F4}）。");
                // 无领取钮且未清完：可能还有未达成任务，不记完成。
                return await IsClearedAsync(window, cancellationToken);
            }

            _log($"每日任务：第 {round} 轮点击「一括受取」（{claim.Score:F4}）。");
            await _screen.ClickProbeAsync(window, claim, "一括受取", cancellationToken, settleDelayMs: AfterClaimDelayMs);
            window = _screen.Refresh(window);
            if (!await TryDismissOkAsync(window, cancellationToken))
            {
                _log("每日任务：点了一括受取但无 OK，停止连点。");
                return await IsClearedAsync(window, cancellationToken);
            }
        }

        return await IsClearedAsync(window, cancellationToken);
    }

    private async Task<bool> IsClearedAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult cleared = await _screen.ProbeAsync(
            window,
            _clearedMatcher,
            _config.DailyMissionsClearedTopLeft,
            _config.DailyMissionsClearedSize,
            cancellationToken,
            ClearedThreshold);
        return cleared.IsMatch;
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

        _log($"每日任务：点击 OK（{ok.Score:F4}）。");
        await _screen.ClickProbeAsync(window, ok, "OK", cancellationToken, settleDelayMs: AfterOkDelayMs);
        return true;
    }

    public static DateOnly CurrentMissionDay(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime date = local.Date;
        if (local.Hour < ResetHour)
            date = date.AddDays(-1);
        return DateOnly.FromDateTime(date);
    }

    public static string CurrentMissionDayKey(DateTime now) =>
        CurrentMissionDay(now).ToString("yyyy-MM-dd");

    public static bool QuotaFilledThisDay(string? lastDayKey, DateTime now) =>
        !string.IsNullOrWhiteSpace(lastDayKey) &&
        string.Equals(lastDayKey, CurrentMissionDayKey(now), StringComparison.Ordinal);

    public static DateTime NextReset(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime todayReset = local.Date.AddHours(ResetHour);
        return local < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
