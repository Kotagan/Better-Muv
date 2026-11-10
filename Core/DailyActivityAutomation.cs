using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日课外活动：主页 → クエスト → 課外活動 →
/// 一括受取（完了）→ 一括活動（募集中）→ 閉じる → 回主页。
/// 每日 5:00 刷新；通常 1 日 5 次。
/// </summary>
public sealed class DailyActivityAutomation
{
    private const int AfterOpenDelayMs = 1600;
    private const int AfterClaimDelayMs = 900;
    private const int AfterStartDelayMs = 1100;
    private const int AfterOkDelayMs = 800;
    private const int AfterCloseDelayMs = 700;
    private const int TitleTimeoutMs = 10000;
    private const int ButtonTimeoutMs = 3500;
    private const int OkTimeoutMs = 4000;
    private const double TitleThreshold = 0.60;
    private const double ButtonThreshold = 0.70;
    private const double OkThreshold = 0.70;
    private const int OpenAttempts = 2;
    private const int ResetHour = 5;
    private const int MaxClaimRounds = 3;
    private const int MaxOkRounds = 4;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _entryMatcher;
    private readonly TemplateMatcher _titleMatcher;
    private readonly TemplateMatcher _claimMatcher;
    private readonly TemplateMatcher _startMatcher;
    private readonly TemplateMatcher _closeMatcher;
    private readonly TemplateMatcher _batchOkMatcher;
    private readonly TemplateMatcher _rewardOkMatcher;
    private readonly TemplateMatcher _rewardOkMatcherAlt;
    private readonly TemplateMatcher _rewardOkMatcherPink;

    public DailyActivityAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _entryMatcher = TemplateAssets.Load("quest-activity.png");
        _titleMatcher = TemplateAssets.Load("activity-title.png");
        _claimMatcher = TemplateAssets.Load("activity-claim-all.png");
        _startMatcher = TemplateAssets.Load("activity-start-all.png");
        _closeMatcher = TemplateAssets.Load("activity-close.png");
        _batchOkMatcher = TemplateAssets.Load("activity-batch-ok.png");
        _rewardOkMatcher = TemplateAssets.Load("settlement-confirm.png");
        _rewardOkMatcherAlt = TemplateAssets.Load("daily-shop-ok.png");
        _rewardOkMatcherPink = TemplateAssets.Load("settlement-confirm-ok.png");
    }

    public async Task<TaskRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = DateTime.Now;
        string dayKey = CurrentActivityDayKey(now);
        if (QuotaFilledThisDay(_config.LastDailyActivityDay, now))
        {
            _log($"每日课外活动：今日（{dayKey}）已完成，等到 {NextReset(now):MM-dd HH:mm} 刷新。");
            return TaskRunResult.Success("今日已完成");
        }

        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"每日课外活动：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return TaskRunResult.Fail("未能将游戏置于前台");
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"每日课外活动：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        bool opened = false;
        for (int attempt = 1; attempt <= OpenAttempts; attempt++)
        {
            try
            {
                window = await new QuestFromHomeEntry(_config, _screen, _log).RunAsync(
                    window,
                    QuestFromHomeEntry.ActivityClick(_config),
                    "課外活動",
                    cancellationToken,
                    _entryMatcher,
                    _config.QuestActivityTopLeft,
                    _config.QuestActivitySize,
                    singleClickTarget: true);
            }
            catch (InvalidOperationException ex)
            {
                _log($"每日课外活动：进入失败（{attempt}/{OpenAttempts}）— {ex.Message}");
                await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
                if (attempt >= OpenAttempts)
                    return TaskRunResult.Fail(ex.Message);
                continue;
            }

            await Task.Delay(AfterOpenDelayMs, cancellationToken);
            window = _screen.Refresh(window);

            if (await WaitUntilActivityOpenAsync(window, cancellationToken))
            {
                opened = true;
                break;
            }

            _log($"每日课外活动：第 {attempt}/{OpenAttempts} 次未打开弹窗，回主页重试。");
            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
            window = _screen.Refresh(window);
        }

        if (!opened)
        {
            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
            return TaskRunResult.Fail("未打开課外活動");
        }

        (int claimed, bool claimBlocked) = await ClaimAllAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        int started = 0;
        if (!claimBlocked)
            started = await StartAllAsync(window, cancellationToken);
        else
            _log("每日课外活动：领取确认未关掉，跳过一括活動，本轮不记完成。");

        window = _screen.Refresh(window);
        await CloseModalAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        _log("每日课外活动：返回主页。");
        if (!await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken))
        {
            try
            {
                await new TaskFailureHandler(_config, _log)
                    .RecoverToHomeAsync("每日课外活动", cancellationToken);
            }
            catch (Exception ex)
            {
                _log($"每日课外活动：回主页恢复失败 — {ex.Message}");
            }
        }

        _log("每日课外活动：结束。");

        if (claimBlocked)
            return TaskRunResult.Fail("一括受取后未点到奖励 OK");

        if (claimed > 0 || started > 0)
        {
            _config.LastDailyActivityDay = dayKey;
            ConfigStore.Save(_config);
            _log($"每日课外活动：已记录今日完成（{dayKey}，领取 {claimed}，派出 {started}）。");
            return TaskRunResult.Success();
        }

        // 标题在但无可领可派：多半已在活动中，仍记完成避免反复空跑。
        _config.LastDailyActivityDay = dayKey;
        ConfigStore.Save(_config);
        _log($"每日课外活动：无可领/可派（可能已在活动中），已记今日完成（{dayKey}）。");
        return TaskRunResult.Success("已在活动中或无可操作");
    }

    /// <returns>claimed=成功领取轮数；claimBlocked=点了受取却关不掉确认弹窗。</returns>
    private async Task<(int Claimed, bool ClaimBlocked)> ClaimAllAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        int claimed = 0;
        for (int round = 1; round <= MaxClaimRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            TemplateProbeResult claim = await _screen.WaitForProbeAsync(
                window, _claimMatcher,
                _config.DailyActivityClaimTopLeft, _config.DailyActivityClaimSize,
                cancellationToken,
                timeoutMs: round == 1 ? ButtonTimeoutMs : 2000,
                matchThreshold: ButtonThreshold);
            if (!claim.IsMatch)
            {
                _log($"每日课外活动：第 {round} 轮无一括受取（最高 {claim.Score:F4}）。");
                break;
            }

            _log($"每日课外活动：点击「一括受取」（{claim.Score:F3}，{round}/{MaxClaimRounds}）。");
            await _screen.ClickProbeAsync(window, claim, "一括受取", cancellationToken, settleDelayMs: AfterClaimDelayMs);
            window = _screen.Refresh(window);
            if (!await DismissRewardOkAsync(window, cancellationToken))
            {
                _log("每日课外活动：点了一括受取但无奖励 OK，停止领取。");
                return (claimed, ClaimBlocked: true);
            }

            claimed++;
            window = _screen.Refresh(window);
            // 领完后弹窗仍在时再等一拍，避免立刻扫一括活動扫到灰态/遮挡。
            await Task.Delay(400, cancellationToken);
        }

        return (claimed, ClaimBlocked: false);
    }

    private async Task<int> StartAllAsync(GameWindow window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 还有「一括受取」时一括活動常是灰态（模板仍可能 >0.7），先别点。
        TemplateProbeResult claimStill = await _screen.WaitForProbeAsync(
            window, _claimMatcher,
            _config.DailyActivityClaimTopLeft, _config.DailyActivityClaimSize,
            cancellationToken, timeoutMs: 1200, matchThreshold: ButtonThreshold);
        if (claimStill.IsMatch)
        {
            _log($"每日课外活动：仍见一括受取（{claimStill.Score:F3}），跳过一括活動。");
            return 0;
        }

        TemplateProbeResult start = await _screen.WaitForProbeAsync(
            window, _startMatcher,
            _config.DailyActivityStartTopLeft, _config.DailyActivityStartSize,
            cancellationToken, timeoutMs: ButtonTimeoutMs, matchThreshold: ButtonThreshold);
        if (!start.IsMatch)
        {
            _log($"每日课外活动：无一括活動（最高 {start.Score:F4}），跳过派出。");
            return 0;
        }

        _log($"每日课外活动：点击「一括活動」（{start.Score:F3}）。");
        await _screen.ClickProbeAsync(window, start, "一括活動", cancellationToken, settleDelayMs: AfterStartDelayMs);
        window = _screen.Refresh(window);

        TemplateProbeResult batchOk = await _screen.WaitForProbeAsync(
            window, _batchOkMatcher,
            _config.DailyActivityBatchOkTopLeft, _config.DailyActivityBatchOkSize,
            cancellationToken, timeoutMs: OkTimeoutMs, matchThreshold: OkThreshold);
        if (!batchOk.IsMatch)
        {
            // 与奖励 OK 同粉钮；兼容 settlement-confirm。
            batchOk = await _screen.WaitForProbeAsync(
                window, _rewardOkMatcher,
                _config.DailyActivityBatchOkTopLeft, _config.DailyActivityBatchOkSize,
                cancellationToken, timeoutMs: 2000, matchThreshold: OkThreshold);
        }

        if (!batchOk.IsMatch)
        {
            _log($"每日课外活动：一括活動确认 OK 未命中（最高 {batchOk.Score:F4}），改点固定坐标。");
            await _screen.ClickAsync(
                window, _config.DailyActivityBatchOkClick, "一括活動OK(坐标)", cancellationToken);
        }
        else
        {
            _log($"每日课外活动：点击一括活動确认 OK（{batchOk.Score:F3}）。");
            await _screen.ClickProbeAsync(window, batchOk, "一括活動OK", cancellationToken, settleDelayMs: AfterOkDelayMs);
        }

        // 「課外活動を開始しました」toast
        await Task.Delay(1500, cancellationToken);
        return 1;
    }

    private async Task<bool> DismissRewardOkAsync(GameWindow window, CancellationToken cancellationToken)
    {
        for (int i = 1; i <= MaxOkRounds; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            TemplateProbeResult? hit = null;
            foreach (TemplateMatcher matcher in new[]
                     {
                         _rewardOkMatcherPink, _rewardOkMatcher, _rewardOkMatcherAlt
                     })
            {
                TemplateProbeResult ok = await _screen.WaitForProbeAsync(
                    window, matcher,
                    _config.DailyActivityRewardOkTopLeft, _config.DailyActivityRewardOkSize,
                    cancellationToken, timeoutMs: i == 1 ? OkTimeoutMs : 1800, matchThreshold: OkThreshold);
                if (ok.IsMatch && (hit is null || ok.Score > hit.Score))
                    hit = ok;
            }

            if (hit is null || !hit.IsMatch)
            {
                if (i == 1)
                {
                    _log("每日课外活动：奖励 OK 模板未命中，改点固定坐标。");
                    await _screen.ClickAsync(
                        window, _config.DailyActivityRewardOkClick, "奖励OK(坐标)", cancellationToken);
                    await Task.Delay(AfterOkDelayMs, cancellationToken);
                    continue;
                }

                return true;
            }

            _log($"每日课外活动：点击奖励 OK（{hit.Score:F3}，{i}/{MaxOkRounds}）。");
            await _screen.ClickProbeAsync(window, hit, "奖励OK", cancellationToken, settleDelayMs: AfterOkDelayMs);
        }

        return true;
    }

    private async Task<bool> WaitUntilActivityOpenAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult title = await _screen.WaitForProbeAsync(
            window, _titleMatcher,
            _config.DailyActivityTitleTopLeft, _config.DailyActivityTitleSize,
            cancellationToken, timeoutMs: TitleTimeoutMs, matchThreshold: TitleThreshold);
        if (title.IsMatch)
        {
            _log($"每日课外活动：已打开（标题 {title.Score:F3}）。");
            return true;
        }

        // 标题 ROI/缩放偶发偏低时，用底栏「閉じる」确认弹窗已开。
        TemplateProbeResult closeGate = await _screen.WaitForProbeAsync(
            window, _closeMatcher,
            _config.DailyActivityCloseTopLeft, _config.DailyActivityCloseSize,
            cancellationToken, timeoutMs: 2500, matchThreshold: ButtonThreshold);
        if (closeGate.IsMatch)
        {
            _log($"每日课外活动：标题弱（{title.Score:F3}），以閉じる（{closeGate.Score:F3}）确认已打开。");
            return true;
        }

        _log($"每日课外活动：未识别标题（最高 {title.Score:F4}）且无閉じる（最高 {closeGate.Score:F4}）。");
        return false;
    }

    private async Task CloseModalAsync(GameWindow window, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        TemplateProbeResult close = await _screen.WaitForProbeAsync(
            window, _closeMatcher,
            _config.DailyActivityCloseTopLeft, _config.DailyActivityCloseSize,
            cancellationToken, timeoutMs: 3000, matchThreshold: ButtonThreshold);
        if (close.IsMatch)
        {
            _log($"每日课外活动：点击「閉じる」（{close.Score:F3}）。");
            await _screen.ClickProbeAsync(window, close, "閉じる", cancellationToken, settleDelayMs: AfterCloseDelayMs);
            return;
        }

        // 弹窗可能已因确认/toast 自行关掉；此时固定点会误点底栏「アイテム」等。
        if (!await IsActivityModalOpenAsync(window, cancellationToken))
        {
            _log($"每日课外活动：未识别閉じる（最高 {close.Score:F4}），且弹窗已不在，跳过关闭。");
            return;
        }

        _log($"每日课外活动：未识别閉じる（最高 {close.Score:F4}），改用固定点击。");
        await _screen.ClickAsync(
            window, _config.DailyActivityCloseClick, "閉じる(坐标)", cancellationToken);
        await Task.Delay(AfterCloseDelayMs, cancellationToken);
    }

    private async Task<bool> IsActivityModalOpenAsync(GameWindow window, CancellationToken cancellationToken)
    {
        foreach ((TemplateMatcher matcher, ConfigPoint tl, ConfigSize size, double thr) in new (TemplateMatcher, ConfigPoint, ConfigSize, double)[]
                 {
                     (_titleMatcher, _config.DailyActivityTitleTopLeft, _config.DailyActivityTitleSize, 0.60),
                     (_closeMatcher, _config.DailyActivityCloseTopLeft, _config.DailyActivityCloseSize, ButtonThreshold),
                     (_claimMatcher, _config.DailyActivityClaimTopLeft, _config.DailyActivityClaimSize, ButtonThreshold),
                     (_startMatcher, _config.DailyActivityStartTopLeft, _config.DailyActivityStartSize, ButtonThreshold),
                 })
        {
            TemplateProbeResult probe = await _screen.ProbeAsync(
                window, matcher, tl, size, cancellationToken, thr);
            if (probe.IsMatch)
                return true;
        }

        return false;
    }

    public static DateOnly CurrentActivityDay(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime date = local.Date;
        if (local.Hour < ResetHour)
            date = date.AddDays(-1);
        return DateOnly.FromDateTime(date);
    }

    public static string CurrentActivityDayKey(DateTime now) =>
        CurrentActivityDay(now).ToString("yyyy-MM-dd");

    public static bool QuotaFilledThisDay(string? lastDayKey, DateTime now) =>
        !string.IsNullOrWhiteSpace(lastDayKey) &&
        string.Equals(lastDayKey, CurrentActivityDayKey(now), StringComparison.Ordinal);

    public static DateTime NextReset(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime todayReset = local.Date.AddHours(ResetHour);
        return local < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
