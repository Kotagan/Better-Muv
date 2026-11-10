using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 公式 MG 竞技：主页 → クエスト → 公式MG競技 →
/// 対戦相手選択 → 変更/抽選 → 选对手 → 対戦準備 → 出撃 → 次へ，每日最多 5 次。
/// <para>
/// 粉钮模板只判在页；「対戦相手選択 / 対戦準備 / 出撃」一律固定坐标点击。
/// 进入对手选择必须以「対戦準備」为准（点飞回主页也会让大厅粉钮消失）。
/// 同对手重复挑战不计进度，每场先点卡底「変更」再弹窗「抽選」。
/// 战后停在对手选择，下一轮不必再回大厅。
/// </para>
/// </summary>
public sealed class DailyMgArenaAutomation
{
    private const int AfterOpenDelayMs = 1400;
    private const int AfterChallengeDelayMs = 1400;
    private const int AfterChangeDelayMs = 900;
    private const int AfterDrawDelayMs = 1400;
    private const int AfterOpponentDelayMs = 800;
    private const int AfterPrepareDelayMs = 1400;
    private const int AfterSortieDelayMs = 1600;
    private const int AfterNextDelayMs = 900;
    /// <summary>轮换前 4 槽（最右常已 WIN）。</summary>
    private const int ChangeSlotCount = 4;
    private const int LobbyTimeoutMs = 10000;
    private const int BattlePhaseTimeoutMs = 180_000;
    private const int MaxRunsPerDay = 5;
    private const int ChallengeEnterAttempts = 3;
    private const int PrepareEnterAttempts = 3;
    private const double EntryThreshold = 0.72;
    private const double ChallengeThreshold = 0.70;
    private const double PrepareThreshold = 0.68;
    private const double SortieThreshold = 0.62;
    private const double NextThreshold = 0.70;
    private const int ResetHour = 5;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _entryMatcher;
    private readonly TemplateMatcher _challengeMatcher;
    private readonly TemplateMatcher _prepareMatcher;
    private readonly TemplateMatcher _sortieMatcher;
    private readonly TemplateMatcher _sortieMatcherAlt;
    private readonly TemplateMatcher _nextMatcher;
    private readonly TemplateMatcher _skipMatcher;
    private readonly TemplateMatcher _skipAltMatcher;

    public DailyMgArenaAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _entryMatcher = TemplateAssets.Load("quest-mg-arena.png");
        _challengeMatcher = TemplateAssets.Load("mg-arena-challenge.png");
        _prepareMatcher = TemplateAssets.Load("mg-arena-prepare.png");
        _sortieMatcher = TemplateAssets.Load("mg-arena-sortie.png");
        _sortieMatcherAlt = TemplateAssets.Load("main-quest-sortie.png");
        _nextMatcher = TemplateAssets.Load("main-quest-next.png");
        _skipMatcher = TemplateAssets.Load("battle-skip.png");
        _skipAltMatcher = TemplateAssets.Load("main-quest-skip.png");
    }

    public async Task<TaskRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = DateTime.Now;
        string dayKey = CurrentArenaDayKey(now);
        if (QuotaFilledThisDay(_config.LastDailyMgArenaDay, now))
        {
            _log($"公式MG竞技：今日（{dayKey}）已打完，等到 {NextReset(now):MM-dd HH:mm} 刷新。");
            return TaskRunResult.Success("今日已完成");
        }

        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"公式MG竞技：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return TaskRunResult.Fail("未能将游戏置于前台");
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"公式MG竞技：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        try
        {
            window = await new QuestFromHomeEntry(_config, _screen, _log).RunAsync(
                window,
                MgArenaClick(_config),
                "公式MG競技",
                cancellationToken,
                _entryMatcher,
                _config.QuestMgArenaTopLeft,
                _config.QuestMgArenaSize,
                singleClickTarget: true);
        }
        catch (InvalidOperationException ex)
        {
            _log($"公式MG竞技：进入失败 — {ex.Message}");
            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
            return TaskRunResult.Fail(ex.Message);
        }

        await Task.Delay(AfterOpenDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        int completed = 0;
        bool exhausted = false;
        for (int run = 1; run <= MaxRunsPerDay; run++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            // 结算残留：先点次へ。战后正常停在对手选择，不必回大厅。
            {
                bool lobbyNow = await IsLobbyAsync(window, cancellationToken);
                bool opponentNow = !lobbyNow && await IsOpponentSelectAsync(window, cancellationToken);
                if (!lobbyNow && !opponentNow)
                {
                    TemplateProbeResult next = await _screen.ProbeAsync(
                        window, _nextMatcher,
                        _config.MainQuestNextTopLeft, _config.MainQuestNextSize,
                        cancellationToken, NextThreshold);
                    if (next.IsMatch)
                    {
                        await _screen.ClickProbeAsync(window, next, "次へ", cancellationToken, settleDelayMs: AfterNextDelayMs);
                        window = _screen.Refresh(window);
                    }
                }
            }

            // 大厅粉钮与「対戦準備」形近，prepare 在大厅也会 0.85+；有 challenge 一律按大厅。
            bool onLobby = await IsLobbyAsync(window, cancellationToken);
            bool onOpponent = !onLobby && await IsOpponentSelectAsync(window, cancellationToken);
            if (!onOpponent && !onLobby)
            {
                _log($"公式MG竞技：第 {run} 次既非大厅也非对手选择，视为次数用尽或界面异常。");
                exhausted = true;
                break;
            }

            if (onLobby)
            {
                _log($"公式MG竞技：第 {run}/{MaxRunsPerDay} 次，进入对手选择。");
                if (!await EnterOpponentSelectAsync(window, cancellationToken))
                {
                    _log("公式MG竞技：未能点进「対戦相手選択」，结束本轮。");
                    break;
                }

                window = _screen.Refresh(window);
            }
            else
            {
                _log($"公式MG竞技：第 {run}/{MaxRunsPerDay} 次，已在对手选择。");
            }

            // 同人重复挑战不计进度：先変更→抽選换人，再点卡出撃。
            int slot = (run - 1) % ChangeSlotCount;
            window = await RefreshOpponentAsync(window, slot, cancellationToken);
            ConfigPoint opponent = OpponentCardClick(slot);
            await _screen.ClickAsync(window, opponent, $"对手卡#{slot + 1}", cancellationToken, parkCursor: false);
            await Task.Delay(AfterOpponentDelayMs, cancellationToken);
            _screen.ParkCursorAway(window);
            window = _screen.Refresh(window);

            if (!await EnterSortiePageAsync(window, cancellationToken))
            {
                _log("公式MG竞技：多次未能进入出撃页，结束本轮。");
                break;
            }

            window = _screen.Refresh(window);
            if (!await ClickSortieAsync(window, cancellationToken))
            {
                _log("公式MG竞技：出撃点击失败，结束本轮。");
                break;
            }

            window = _screen.Refresh(window);
            bool returned = await RunBattleUntilReadyAsync(window, run, cancellationToken);
            window = _screen.Refresh(window);
            if (!returned)
            {
                _log("公式MG竞技：战斗阶段未回到对手选择/大厅，结束本轮。");
                break;
            }

            completed++;
            _log($"公式MG竞技：第 {run} 次完成（累计 {completed}）。");
        }

        if ((exhausted && completed > 0) || completed >= MaxRunsPerDay || (exhausted && completed == 0))
        {
            _config.LastDailyMgArenaDay = dayKey;
            ConfigStore.Save(_config);
            _log($"公式MG竞技：已记录今日完成（{dayKey}，本轮 {completed} 次）。");
        }
        else if (completed > 0)
        {
            _log($"公式MG竞技：本轮完成 {completed} 次，未确认次数用尽，不写完成标记。");
        }
        else
        {
            _log("公式MG竞技：未能完成任何一次。");
        }

        window = _screen.Refresh(window);
        _log("公式MG竞技：返回主页。");
        if (!await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken))
        {
            try
            {
                await new TaskFailureHandler(_config, _log)
                    .RecoverToHomeAsync("公式MG竞技", cancellationToken);
            }
            catch (Exception ex)
            {
                _log($"公式MG竞技：回主页恢复失败 — {ex.Message}");
            }
        }

        _log("公式MG竞技：结束。");
        return completed > 0 || exhausted
            ? TaskRunResult.Success()
            : TaskRunResult.Fail("未能完成挑战");
    }

    /// <summary>大厅粉钮「対戦相手選択」：模板只判在页，点击用固定坐标（避免命中中心偏下）。</summary>
    private async Task<bool> EnterOpponentSelectAsync(GameWindow window, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= ChallengeEnterAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            TemplateProbeResult challenge = await _screen.WaitForProbeAsync(
                window, _challengeMatcher,
                _config.MgArenaChallengeTopLeft, _config.MgArenaChallengeSize,
                cancellationToken, timeoutMs: attempt == 1 ? LobbyTimeoutMs : 4000,
                matchThreshold: ChallengeThreshold);
            if (!challenge.IsMatch)
            {
                _log($"公式MG竞技：未识别大厅「対戦相手選択」（最高 {challenge.Score:F4}，{attempt}/{ChallengeEnterAttempts}）。");
                return false;
            }

            _log($"公式MG竞技：大厅已识别（{challenge.Score:F3}），固定点击「対戦相手選択」" +
                 $"（{_config.MgArenaChallengeClick.X},{_config.MgArenaChallengeClick.Y}，{attempt}/{ChallengeEnterAttempts}）。");
            await _screen.FocusAsync(window.Handle, cancellationToken);
            await _screen.ClickAsync(
                window, _config.MgArenaChallengeClick, "対戦相手選択", cancellationToken, parkCursor: false);
            await Task.Delay(AfterChallengeDelayMs, cancellationToken);
            _screen.ParkCursorAway(window);
            window = _screen.Refresh(window);

            // 成功必须以「対戦準備」为准：点飞回主页也会让大厅粉钮消失，不能用「离开大厅」当成功。
            TemplateProbeResult prepare = await _screen.ProbeAsync(
                window, _prepareMatcher,
                _config.MgArenaPrepareTopLeft, _config.MgArenaPrepareSize,
                cancellationToken, PrepareThreshold);
            if (prepare.IsMatch)
            {
                _log($"公式MG竞技：已进入对手选择（対戦準備 {prepare.Score:F3}）。");
                return true;
            }

            if (await IsLobbyAsync(window, cancellationToken))
            {
                _log($"公式MG竞技：仍在大厅（未出现対戦準備），重试点击。");
            }
            else
            {
                _log($"公式MG竞技：点后既非大厅也无対戦準備（疑似点飞），放弃本轮进入。");
                return false;
            }
        }

        return false;
    }

    private async Task<bool> EnterSortiePageAsync(GameWindow window, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= PrepareEnterAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            if (await FindSortieAsync(window, cancellationToken) is { IsMatch: true })
            {
                _log("公式MG竞技：已在出撃页。");
                return true;
            }

            TemplateProbeResult prepare = await _screen.WaitForProbeAsync(
                window, _prepareMatcher,
                _config.MgArenaPrepareTopLeft, _config.MgArenaPrepareSize,
                cancellationToken, timeoutMs: 4000, matchThreshold: PrepareThreshold);
            if (prepare.IsMatch)
            {
                _log($"公式MG竞技：固定点击「対戦準備」（识别 {prepare.Score:F3}，{attempt}/{PrepareEnterAttempts}）。");
            }
            else
            {
                _log($"公式MG竞技：対戦準備弱（最高 {prepare.Score:F4}），仍点固定坐标（{attempt}/{PrepareEnterAttempts}）。");
            }

            await _screen.FocusAsync(window.Handle, cancellationToken);
            await _screen.ClickAsync(
                window, _config.MgArenaPrepareClick, "対戦準備", cancellationToken, parkCursor: false);
            await Task.Delay(AfterPrepareDelayMs, cancellationToken);
            _screen.ParkCursorAway(window);
            window = _screen.Refresh(window);

            TemplateProbeResult? sortie = await FindSortieAsync(window, cancellationToken);
            if (sortie is { IsMatch: true })
            {
                _log($"公式MG竞技：已进入出撃页（{sortie.Score:F3}）。");
                return true;
            }

            _log($"公式MG竞技：点対戦準備后未见出撃（最高 {sortie?.Score:F4}），重试。");
        }

        return false;
    }

    private async Task<bool> ClickSortieAsync(GameWindow window, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        TemplateProbeResult? sortie = await FindSortieAsync(window, cancellationToken);
        // 模板只确认在页；点击一律用固定点（与対戦相手選択同理，避免中心漂移）。
        if (sortie is { IsMatch: true })
            _log($"公式MG竞技：出撃页已识别（{sortie.Score:F3}），固定点击" +
                 $"（{_config.MgArenaSortieClick.X},{_config.MgArenaSortieClick.Y}）。");
        else
            _log($"公式MG竞技：出撃弱识别（最高 {sortie?.Score:F4}），仍点固定坐标。");

        await _screen.FocusAsync(window.Handle, cancellationToken);
        await _screen.ClickAsync(
            window, _config.MgArenaSortieClick, "出撃", cancellationToken, parkCursor: false);
        await Task.Delay(AfterSortieDelayMs, cancellationToken);
        _screen.ParkCursorAway(window);
        return true;
    }

    private async Task<TemplateProbeResult?> FindSortieAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult? best = null;
        foreach (TemplateMatcher matcher in new[] { _sortieMatcher, _sortieMatcherAlt })
        {
            TemplateProbeResult hit = await _screen.WaitForProbeAsync(
                window, matcher,
                _config.MgArenaSortieTopLeft, _config.MgArenaSortieSize,
                cancellationToken, timeoutMs: 2500, matchThreshold: SortieThreshold);
            if (best is null || hit.Score > best.Score)
                best = hit;
            if (hit.IsMatch)
                return hit;
        }

        return best;
    }

    /// <summary>点指定槽「変更」→ 弹窗「抽選」（保留默认戦力下位）。</summary>
    private async Task<GameWindow> RefreshOpponentAsync(
        GameWindow window, int slot, CancellationToken cancellationToken)
    {
        ConfigPoint change = ChangeButtonClick(slot);
        _log($"公式MG竞技：点击「変更」槽{slot + 1}（{change.X},{change.Y}）。");
        await _screen.FocusAsync(window.Handle, cancellationToken);
        await _screen.ClickAsync(window, change, $"変更#{slot + 1}", cancellationToken, parkCursor: false);
        await Task.Delay(AfterChangeDelayMs, cancellationToken);
        _screen.ParkCursorAway(window);
        window = _screen.Refresh(window);

        ConfigPoint draw = _config.MgArenaDrawClick;
        _log($"公式MG竞技：点击「抽選」（{draw.X},{draw.Y}）。");
        await _screen.FocusAsync(window.Handle, cancellationToken);
        await _screen.ClickAsync(window, draw, "抽選", cancellationToken, parkCursor: false);
        await Task.Delay(AfterDrawDelayMs, cancellationToken);
        _screen.ParkCursorAway(window);
        return _screen.Refresh(window);
    }

    private ConfigPoint ChangeButtonClick(int slot)
    {
        int[] xs = _config.MgArenaOpponentSlotXs;
        int x = xs is { Length: > 0 }
            ? xs[Math.Clamp(slot, 0, xs.Length - 1)]
            : _config.MgArenaOpponentClick.X;
        return new ConfigPoint(x, _config.MgArenaChangeClickY);
    }

    private ConfigPoint OpponentCardClick(int slot)
    {
        int[] xs = _config.MgArenaOpponentSlotXs;
        if (xs is { Length: > 0 })
            return new ConfigPoint(xs[Math.Clamp(slot, 0, xs.Length - 1)], _config.MgArenaOpponentClick.Y);
        return _config.MgArenaOpponentClick;
    }

    private async Task<bool> IsLobbyAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult challenge = await _screen.ProbeAsync(
            window, _challengeMatcher,
            _config.MgArenaChallengeTopLeft, _config.MgArenaChallengeSize,
            cancellationToken, ChallengeThreshold);
        return challenge.IsMatch;
    }

    private async Task<bool> IsOpponentSelectAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult prepare = await _screen.ProbeAsync(
            window, _prepareMatcher,
            _config.MgArenaPrepareTopLeft, _config.MgArenaPrepareSize,
            cancellationToken, PrepareThreshold);
        return prepare.IsMatch;
    }

    /// <summary>战斗中等到 SKIP/次へ；点次へ后通常停在对手选择（也可能回大厅）。</summary>
    private async Task<bool> RunBattleUntilReadyAsync(
        GameWindow window, int run, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DateTime lastSkipAt = DateTime.MinValue;
        DateTime lastNextAt = DateTime.MinValue;
        _log($"公式MG竞技：第 {run} 次战斗中，等待 SKIP / 次へ / 对手选择。");

        while (sw.ElapsedMilliseconds < BattlePhaseTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            if (sw.ElapsedMilliseconds > 2500)
            {
                if (await IsLobbyAsync(window, cancellationToken))
                {
                    _log("公式MG竞技：已回到大厅。");
                    return true;
                }

                // 对手选择：prepare 命中且非大厅（大厅粉钮也会误触 prepare）。
                if (await IsOpponentSelectAsync(window, cancellationToken))
                {
                    _log("公式MG竞技：已回到对手选择。");
                    return true;
                }
            }

            TemplateProbeResult next = await _screen.ProbeAsync(
                window, _nextMatcher,
                _config.MainQuestNextTopLeft, _config.MainQuestNextSize,
                cancellationToken, NextThreshold);
            if (next.IsMatch && ReadyToClick(lastNextAt, 800))
            {
                lastNextAt = DateTime.UtcNow;
                _log($"公式MG竞技：点击「次へ」（{next.Score:F3}）。");
                await _screen.ClickProbeAsync(
                    window, next, "次へ", cancellationToken,
                    settleDelayMs: AfterNextDelayMs, parkCursor: false);
                _screen.ParkCursorAway(window);
                continue;
            }

            if (ReadyToClick(lastSkipAt, 1200))
            {
                foreach (TemplateMatcher skip in new[] { _skipMatcher, _skipAltMatcher })
                {
                    TemplateProbeResult hit = await _screen.ProbeAsync(
                        window, skip,
                        _config.MainQuestSkipTopLeft, _config.MainQuestSkipSize,
                        cancellationToken, 0.50);
                    if (!hit.IsMatch)
                        continue;
                    lastSkipAt = DateTime.UtcNow;
                    _log($"公式MG竞技：点击 SKIP（{hit.Score:F3}）。");
                    await _screen.ClickProbeAsync(
                        window, hit, "SKIP", cancellationToken, settleDelayMs: 200, parkCursor: false);
                    _screen.ParkCursorAway(window);
                    break;
                }
            }

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }

        return false;
    }

    private static bool ReadyToClick(DateTime lastAt, int minGapMs) =>
        lastAt == DateTime.MinValue || (DateTime.UtcNow - lastAt).TotalMilliseconds >= minGapMs;

    public static ConfigPoint MgArenaClick(AutomationConfig config) => new(
        config.QuestMgArenaTopLeft.X + config.QuestMgArenaSize.Width / 2,
        config.QuestMgArenaTopLeft.Y + config.QuestMgArenaSize.Height / 2);

    public static DateOnly CurrentArenaDay(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime date = local.Date;
        if (local.Hour < ResetHour)
            date = date.AddDays(-1);
        return DateOnly.FromDateTime(date);
    }

    public static string CurrentArenaDayKey(DateTime now) =>
        CurrentArenaDay(now).ToString("yyyy-MM-dd");

    public static bool QuotaFilledThisDay(string? lastDayKey, DateTime now) =>
        !string.IsNullOrWhiteSpace(lastDayKey) &&
        string.Equals(lastDayKey, CurrentArenaDayKey(now), StringComparison.Ordinal);

    public static DateTime NextReset(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime todayReset = local.Date.AddHours(ResetHour);
        return local < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
