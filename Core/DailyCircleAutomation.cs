using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日社团：主页 → サークル → ミッション → デイリー领奖 → ウィークリー领奖 → 回主页。
/// 只领已完成项，不点「挑戦」、不购买、不代做迷宫/扭蛋等。
/// </summary>
public sealed class DailyCircleAutomation
{
    private const int AfterHomeDelayMs = 700;
    private const int AfterNavDelayMs = 1200;
    private const int AfterOpenDelayMs = 1000;
    private const int AfterTabDelayMs = 800;
    private const int AfterClaimDelayMs = 900;
    private const int AfterOkDelayMs = 700;
    private const int RecognizeTimeoutMs = 8000;
    /// <summary>一括受取一次会领取当前页全部奖励；按钮可能继续常亮，禁止再次点击。</summary>
    private const int MaxClaimRounds = 1;
    private const double NavThreshold = 0.55;
    private const double TitleThreshold = 0.70;
    private const double MissionEntryThreshold = 0.70;
    /// <summary>一括受取粉钮；旧模板/灰态常见 0.47~0.55，过严会漏领。</summary>
    private const double ClaimThreshold = 0.55;
    private const double OkThreshold = 0.70;
    private const double WeeklyTabThreshold = 0.70;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _navMatcher;
    private readonly TemplateMatcher _titleMatcher;
    private readonly TemplateMatcher _missionEntryMatcher;
    private readonly TemplateMatcher _claimMatcher;
    private readonly TemplateMatcher _weeklyTabMatcher;
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
        _weeklyTabMatcher = TemplateAssets.Load("circle-mission-tab-weekly.png");
        _okMatcher = TemplateAssets.Load("settlement-confirm.png");
        _okMatcherAlt = TemplateAssets.Load("daily-shop-ok.png");
    }

    public async Task<TaskRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"每日社团：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return TaskRunResult.Fail("未能将游戏置于前台");
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"每日社团：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        var home = new HomePresence(_config, _screen, _log);
        (window, bool onHome) = await home.EnsureAsync(window, "每日社团", cancellationToken);
        if (!onHome)
            return TaskRunResult.Fail("未能回到主界面");

        await Task.Delay(AfterHomeDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        if (!await OpenCircleAsync(window, cancellationToken))
        {
            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
            _log("每日社团：结束。");
            return TaskRunResult.Fail("未进入サークル");
        }

        await Task.Delay(AfterNavDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        if (!await OpenCircleMissionsAsync(window, cancellationToken))
        {
            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
            _log("每日社团：结束。");
            return TaskRunResult.Fail("未打开社团ミッション");
        }

        await Task.Delay(AfterOpenDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        await _screen.ClickAsync(window, _config.DailyCircleDailyTabClick, "デイリー页签", cancellationToken);
        await Task.Delay(AfterTabDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        _log("每日社团：领取デイリー。");
        int dailyClaims = await ClaimRewardsAsync(window, "デイリー", cancellationToken);
        window = _screen.Refresh(window);

        (window, bool onWeekly) = await SwitchWeeklyTabAsync(window, cancellationToken);
        int weeklyClaims = 0;
        if (onWeekly)
        {
            await Task.Delay(AfterTabDelayMs, cancellationToken);
            window = _screen.Refresh(window);
            _log("每日社团：领取ウィークリー。");
            weeklyClaims = await ClaimRewardsAsync(window, "ウィークリー", cancellationToken);
        }
        window = _screen.Refresh(window);

        int claimed = dailyClaims + weeklyClaims;
        TaskRunResult result;
        if (claimed > 0)
        {
            _log($"每日社团：已领取（デイリー {dailyClaims}，ウィークリー {weeklyClaims}）。");
            result = TaskRunResult.Success();
        }
        else
        {
            _log("每日社团：未能领取任何奖励（一括受取未命中）。");
            result = TaskRunResult.Fail("未能领取（一括受取未命中）");
        }

        _log("每日社团：返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日社团：结束。");
        return result;
    }

    private async Task<bool> OpenCircleAsync(GameWindow window, CancellationToken cancellationToken)
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
        if (!nav.IsMatch)
        {
            _log($"每日社团：未识别底栏（最高 {nav.Score:F4}）。");
            return false;
        }

        _log($"每日社团：已识别底栏（{nav.Score:F4}），点击。");
        await _screen.ClickProbeAsync(window, nav, "サークル", cancellationToken, settleDelayMs: 200);

        window = _screen.Refresh(window);
        TemplateProbeResult title = await _screen.WaitForProbeAsync(
            window,
            _titleMatcher,
            _config.DailyCircleTitleTopLeft,
            _config.DailyCircleTitleSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: TitleThreshold);
        if (!title.IsMatch)
        {
            _log($"每日社团：标题未识别（最高 {title.Score:F4}）。");
            return false;
        }

        _log($"每日社团：已进入サークル页（{title.Score:F4}）。");
        return true;
    }

    private async Task<bool> OpenCircleMissionsAsync(GameWindow window, CancellationToken cancellationToken)
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
        if (!entry.IsMatch)
        {
            _log($"每日社团：未识别ミッション（最高 {entry.Score:F4}）。");
            return false;
        }

        _log($"每日社团：已识别ミッション（{entry.Score:F4}），点击。");
        await _screen.ClickProbeAsync(window, entry, "サークルミッション", cancellationToken, settleDelayMs: 200);
        return true;
    }

    private async Task<(GameWindow Window, bool Switched)> SwitchWeeklyTabAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        _log("每日社团：切换「ウィークリー」页签。");
        TemplateProbeResult weekly = await _screen.WaitForProbeAsync(
            window,
            _weeklyTabMatcher,
            _config.DailyCircleTabTopLeft,
            _config.DailyCircleTabSize,
            cancellationToken,
            timeoutMs: 3500,
            matchThreshold: WeeklyTabThreshold);
        if (weekly.IsMatch)
        {
            _log($"每日社团：已识别ウィークリー（{weekly.Score:F4}），点击。");
            await _screen.ClickProbeAsync(window, weekly, "ウィークリー页签", cancellationToken, settleDelayMs: 200);
        }
        else
        {
            _log($"每日社团：未识别ウィークリー（最高 {weekly.Score:F4}），改用固定点击（{_config.DailyCircleWeeklyTabClick.X},{_config.DailyCircleWeeklyTabClick.Y}）。");
            await _screen.ClickAsync(window, _config.DailyCircleWeeklyTabClick, "ウィークリー页签", cancellationToken);
        }

        return (_screen.Refresh(window), true);
    }

    /// <summary>每个页签只尝试一次；必须以出现奖励 OK 并点掉才算领取成功（避免空点假成功）。</summary>
    private async Task<int> ClaimRewardsAsync(GameWindow window, string scope, CancellationToken cancellationToken)
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
                _log($"每日社团：第 {round} 轮{scope}无一括受取（最高 {claim.Score:F4}），结束领奖。");
                break;
            }

            _log($"每日社团：第 {round} 轮点击{scope}「一括受取」（{claim.Score:F4}）。");
            await _screen.ClickProbeAsync(window, claim, "一括受取", cancellationToken, settleDelayMs: AfterClaimDelayMs);
            window = _screen.Refresh(window);
            if (!await TryDismissOkAsync(window, cancellationToken))
            {
                _log($"每日社团：{scope}点了一括受取后无 OK，判定未领取。");
                break;
            }

            claimed++;
        }

        return claimed;
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
}
