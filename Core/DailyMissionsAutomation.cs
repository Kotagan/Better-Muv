using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日任务：主页 → ミッション → デイリー领奖 → ウィークリー领奖 → 実績领奖 → 回主页。
/// 只领奖，不购买。未达成内容由其它任务完成。
/// </summary>
public sealed class DailyMissionsAutomation
{
    private const int AfterHomeDelayMs = 700;
    private const int AfterOpenDelayMs = 1000;
    private const int AfterTabDelayMs = 800;
    private const int AfterClaimDelayMs = 900;
    private const int AfterOkDelayMs = 700;
    private const int RecognizeTimeoutMs = 8000;
    private const int MaxClaimRounds = 1;
    private const double EntryThreshold = 0.70;
    private const double TitleThreshold = 0.70;
    private const double ClearedThreshold = 0.72;
    /// <summary>一括受取粉钮；旧模板/灰态常见 0.47~0.55，过严会漏领。</summary>
    private const double ClaimThreshold = 0.55;
    private const double OkThreshold = 0.70;
    private const double WeeklyTabThreshold = 0.70;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _entryMatcher;
    private readonly TemplateMatcher _titleMatcher;
    private readonly TemplateMatcher _clearedMatcher;
    private readonly TemplateMatcher _claimMatcher;
    private readonly TemplateMatcher _weeklyTabMatcher;
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
        _weeklyTabMatcher = TemplateAssets.Load("mission-tab-weekly.png");
        _okMatcher = TemplateAssets.Load("settlement-confirm.png");
        _okMatcherAlt = TemplateAssets.Load("daily-shop-ok.png");
    }

    public async Task<TaskRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        _log("每日任务：领取デイリー。");
        bool dailyCleared = await ClaimUntilClearedAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        (window, bool onWeekly) = await SwitchWeeklyTabAsync(window, cancellationToken);
        int weeklyClaims = 0;
        if (onWeekly)
        {
            await Task.Delay(AfterTabDelayMs, cancellationToken);
            window = _screen.Refresh(window);
            _log("每日任务：领取ウィークリー。");
            weeklyClaims = await ClaimRoundsAsync(window, "ウィークリー", cancellationToken);
        }
        window = _screen.Refresh(window);

        _log("每日任务：切换「実績」页签。");
        await _screen.ClickAsync(
            window, _config.DailyMissionsAchievementTabClick, "実績页签", cancellationToken);
        await Task.Delay(AfterTabDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        _log("每日任务：领取実績。");
        int achievementClaims = await ClaimRoundsAsync(window, "実績", cancellationToken);
        window = _screen.Refresh(window);

        if (dailyCleared)
            _log("每日任务：デイリー已清完。");
        else
            _log("每日任务：デイリー未能确认清完（可能仍有未达成项）。");
        if (weeklyClaims > 0)
            _log($"每日任务：ウィークリー确认领奖 {weeklyClaims} 次。");
        else
            _log("每日任务：ウィークリー当前无可领。");
        if (achievementClaims > 0)
            _log($"每日任务：実績确认领奖 {achievementClaims} 次。");
        else
            _log("每日任务：実績当前无可领。");

        _log("每日任务：返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日任务：结束。");

        if (dailyCleared || weeklyClaims > 0 || achievementClaims > 0)
            return TaskRunResult.Success();
        // 未能确认清完且零领取：按失败处理，避免红点仍在却显示成功。
        return TaskRunResult.Fail("未能领取（一括受取未命中或未确认清完）");
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

        await _screen.ClickAsync(window, _config.DailyMissionsDailyTabClick, "デイリー页签", cancellationToken);
        return _screen.Refresh(window);
    }

    private async Task<(GameWindow Window, bool Switched)> SwitchWeeklyTabAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        _log("每日任务：切换「ウィークリー」页签。");
        TemplateProbeResult weekly = await _screen.WaitForProbeAsync(
            window,
            _weeklyTabMatcher,
            _config.DailyMissionsTabTopLeft,
            _config.DailyMissionsTabSize,
            cancellationToken,
            timeoutMs: 3500,
            matchThreshold: WeeklyTabThreshold);
        if (weekly.IsMatch)
        {
            _log($"每日任务：已识别ウィークリー（{weekly.Score:F4}），点击。");
            await _screen.ClickProbeAsync(window, weekly, "ウィークリー页签", cancellationToken, settleDelayMs: 200);
        }
        else
        {
            _log($"每日任务：未识别ウィークリー（最高 {weekly.Score:F4}），改用固定点击（{_config.DailyMissionsWeeklyTabClick.X},{_config.DailyMissionsWeeklyTabClick.Y}）。");
            await _screen.ClickAsync(window, _config.DailyMissionsWeeklyTabClick, "ウィークリー页签", cancellationToken);
        }

        return (_screen.Refresh(window), true);
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
                _log($"每日任务：第 {round} 轮デイリー无一括受取（最高 {claim.Score:F4}）。");
                return await IsClearedAsync(window, cancellationToken);
            }

            _log($"每日任务：第 {round} 轮点击デイリー「一括受取」（{claim.Score:F4}）。");
            await _screen.ClickProbeAsync(window, claim, "一括受取", cancellationToken, settleDelayMs: AfterClaimDelayMs);
            window = _screen.Refresh(window);
            if (!await TryDismissOkAsync(window, cancellationToken))
            {
                _log("每日任务：点了一括受取但无 OK，停止デイリー连点。");
                // 无 OK 不能凭「クリア」文案单独报成功（文案可能残留/误匹配）。
                return false;
            }
        }

        return await IsClearedAsync(window, cancellationToken);
    }

    private async Task<int> ClaimRoundsAsync(GameWindow window, string scope, CancellationToken cancellationToken)
    {
        int claimed = 0;
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
                _log($"每日任务：第 {round} 轮{scope}无一括受取（最高 {claim.Score:F4}）。");
                break;
            }

            _log($"每日任务：第 {round} 轮点击{scope}「一括受取」（{claim.Score:F4}）。");
            await _screen.ClickProbeAsync(window, claim, "一括受取", cancellationToken, settleDelayMs: AfterClaimDelayMs);
            window = _screen.Refresh(window);
            if (!await TryDismissOkAsync(window, cancellationToken))
            {
                _log($"每日任务：{scope}点了一括受取但无 OK，停止连点。");
                break;
            }

            claimed++;
        }

        return claimed;
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
}
