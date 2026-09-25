using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日演习（戦術演習）：主页 → クエスト → 戦術演習 →
/// 循环「出撃準備 → 出撃 → SKIP/次へ」直到当日次数用尽（最多 5 次）→ 回主页。
/// </summary>
public sealed class DailyExercisesAutomation
{
    private const int AfterHomeDelayMs = 1100;
    private const int AfterPrepareDelayMs = 1400;
    private const int AfterSortieDelayMs = 1500;
    private const int AfterNextDelayMs = 1100;
    private const int AfterSkipDelayMs = 200;
    private const int LobbyTimeoutMs = 12000;
    private const int SortieTimeoutMs = 10000;
    private const int BattlePhaseTimeoutMs = 180_000;
    private const int MaxRunsPerDay = 5;
    private const int PrepareClickAttempts = 3;
    private const double PrepareThreshold = 0.72;
    private const double SortieThreshold = 0.70;
    private const double SkipThreshold = 0.58;
    private const double SkipFallbackScore = 0.28;
    private const double NextThreshold = 0.70;
    /// <summary>「本日あと0回」蓝色 0：非零次数通常 &lt;0.90。</summary>
    private const double RemainingZeroThreshold = 0.92;
    /// <summary>与商店类任务一致：每天 5:00 刷新。</summary>
    private const int ResetHour = 5;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _exercisesEntryMatcher;
    private readonly TemplateMatcher _prepareMatcher;
    private readonly TemplateMatcher _remainingZeroMatcher;
    private readonly TemplateMatcher _sortieMatcher;
    private readonly TemplateMatcher _skipMatcher;
    private readonly TemplateMatcher _skipAltMatcher;
    private readonly TemplateMatcher _nextMatcher;

    public DailyExercisesAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _exercisesEntryMatcher = TemplateAssets.Load("quest-exercises.png");
        _prepareMatcher = TemplateAssets.Load("exercises-prepare.png");
        _remainingZeroMatcher = TemplateAssets.Load("exercises-remaining-zero.png");
        _sortieMatcher = TemplateAssets.Load("main-quest-sortie.png");
        _skipMatcher = TemplateAssets.Load("battle-skip.png");
        _skipAltMatcher = TemplateAssets.Load("main-quest-skip.png");
        _nextMatcher = TemplateAssets.Load("main-quest-next.png");
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = DateTime.Now;
        string dayKey = CurrentExercisesDayKey(now);
        if (QuotaFilledThisDay(_config.LastDailyExercisesDay, now))
        {
            _log($"每日演习：今日（{dayKey}）已打完，等到 {NextReset(now):MM-dd HH:mm} 刷新。");
            return;
        }

        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"每日演习：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return;
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"每日演习：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        window = await new QuestFromHomeEntry(_config, _screen, _log).RunAsync(
            window,
            QuestFromHomeEntry.ExercisesClick(_config),
            "戦術演習",
            cancellationToken,
            _exercisesEntryMatcher,
            _config.QuestExercisesTopLeft,
            _config.QuestExercisesSize);
        await Task.Delay(AfterHomeDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        int completed = 0;
        bool exhausted = false;
        for (int run = 1; run <= MaxRunsPerDay; run++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            _log($"每日演习：第 {run}/{MaxRunsPerDay} 次，等待「出撃準備」。");
            TemplateProbeResult prepare = await _screen.WaitForProbeAsync(
                window,
                _prepareMatcher,
                _config.DailyExercisesPrepareTopLeft,
                _config.DailyExercisesPrepareSize,
                cancellationToken,
                timeoutMs: LobbyTimeoutMs,
                matchThreshold: PrepareThreshold);
            if (!prepare.IsMatch)
            {
                _log($"每日演习：未识别「出撃準備」（最高 {prepare.Score:F4}），视为次数已用尽或未进大厅。");
                exhausted = true;
                break;
            }

            if (await IsRemainingZeroAsync(window, cancellationToken))
            {
                _log("每日演习：识别到「本日あと0回」，今日次数已用尽。");
                exhausted = true;
                break;
            }

            bool enteredSortie = await ClickPrepareUntilSortieAsync(window, prepare, cancellationToken);
            window = _screen.Refresh(window);
            if (!enteredSortie)
            {
                // 按钮仍在但点不进：常见于已 0 次（模板未命中时的兜底）。
                if (completed > 0 || await IsRemainingZeroAsync(window, cancellationToken))
                {
                    _log("每日演习：无法进入出击页，视为次数已用尽。");
                    exhausted = true;
                }
                else
                {
                    _log("每日演习：多次点击「出撃準備」仍未见「出撃」，结束本轮（不写完成标记）。");
                }
                break;
            }

            TemplateProbeResult sortie = await _screen.WaitForProbeAsync(
                window,
                _sortieMatcher,
                _config.MainQuestSortieTopLeft,
                _config.MainQuestSortieSize,
                cancellationToken,
                timeoutMs: 2500,
                matchThreshold: SortieThreshold);
            if (!sortie.IsMatch)
            {
                _log($"每日演习：准备页短暂丢失「出撃」（最高 {sortie.Score:F4}），结束本轮。");
                break;
            }

            _log($"每日演习：已识别「出撃」（{sortie.Score:F4}），点击开战。");
            await _screen.ClickProbeAsync(window, sortie, "出撃", cancellationToken, settleDelayMs: AfterSortieDelayMs);
            window = _screen.Refresh(window);

            bool gotNext = await RunBattleUntilNextAsync(window, run, cancellationToken);
            window = _screen.Refresh(window);
            if (!gotNext)
            {
                _log("每日演习：战斗阶段超时未回到大厅，结束本轮。");
                break;
            }

            completed++;
            _log($"每日演习：第 {run} 次完成（累计 {completed}）。");
        }

        if ((exhausted && completed > 0) || completed >= MaxRunsPerDay)
        {
            _config.LastDailyExercisesDay = dayKey;
            ConfigStore.Save(_config);
            _log($"每日演习：已记录今日完成（{dayKey}，本轮完成 {completed} 次）。");
        }
        else if (exhausted && completed == 0)
        {
            _config.LastDailyExercisesDay = dayKey;
            ConfigStore.Save(_config);
            _log($"每日演习：今日已无可用次数，已记录完成（{dayKey}）。");
        }
        else if (completed > 0)
        {
            _log($"每日演习：本轮完成 {completed} 次，未确认次数用尽，不写完成标记。");
        }
        else
        {
            _log("每日演习：未能完成任何一次，请确认已在戦術演習大厅。");
        }

        window = _screen.Refresh(window);
        _log("每日演习：返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日演习：结束。");
    }

    private async Task<bool> IsRemainingZeroAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult zero = await _screen.ProbeAsync(
            window,
            _remainingZeroMatcher,
            _config.DailyExercisesRemainingTopLeft,
            _config.DailyExercisesRemainingSize,
            cancellationToken,
            RemainingZeroThreshold);
        if (zero.IsMatch)
            _log($"每日演习：剩余次数 0（{zero.Score:F4}）。");
        return zero.IsMatch;
    }

    /// <summary>点「出撃準備」直到出现「出撃」；若仍停在大厅则重试。</summary>
    private async Task<bool> ClickPrepareUntilSortieAsync(
        GameWindow window, TemplateProbeResult firstPrepare, CancellationToken cancellationToken)
    {
        TemplateProbeResult prepare = firstPrepare;
        for (int attempt = 1; attempt <= PrepareClickAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            if (!await _screen.FocusAsync(window.Handle, cancellationToken))
                _log("每日演习：未能将游戏置于前台，「出撃準備」可能点不中。");

            _log($"每日演习：点击「出撃準備」（{prepare.Score:F4}，{attempt}/{PrepareClickAttempts}）。");
            // 进页过程勿立刻挪开光标，避免全屏客户端吞点击。
            await _screen.ClickScreenAsync(window, prepare.Center, cancellationToken, parkCursor: false);
            await Task.Delay(AfterPrepareDelayMs, cancellationToken);
            _screen.ParkCursorAway(window);
            window = _screen.Refresh(window);

            TemplateProbeResult sortie = await _screen.WaitForProbeAsync(
                window,
                _sortieMatcher,
                _config.MainQuestSortieTopLeft,
                _config.MainQuestSortieSize,
                cancellationToken,
                timeoutMs: SortieTimeoutMs,
                matchThreshold: SortieThreshold);
            if (sortie.IsMatch)
            {
                _log($"每日演习：已进入准备页「出撃」（{sortie.Score:F4}）。");
                return true;
            }

            TemplateProbeResult stillLobby = await _screen.ProbeAsync(
                window,
                _prepareMatcher,
                _config.DailyExercisesPrepareTopLeft,
                _config.DailyExercisesPrepareSize,
                cancellationToken,
                PrepareThreshold);
            if (stillLobby.IsMatch)
            {
                _log($"每日演习：仍在大厅（出撃準備 {stillLobby.Score:F4}，出撃最高 {sortie.Score:F4}），重试。");
                prepare = stillLobby;
                continue;
            }

            _log($"每日演习：大厅已离开但未见「出撃」（最高 {sortie.Score:F4}）。");
            return false;
        }

        return false;
    }

    private async Task<bool> RunBattleUntilNextAsync(
        GameWindow window, int run, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DateTime lastSkipAt = DateTime.MinValue;
        DateTime lastNextAt = DateTime.MinValue;
        _log($"每日演习：第 {run} 次战斗中，等待 SKIP / 次へ。");

        while (sw.ElapsedMilliseconds < BattlePhaseTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            // 已回到大厅则说明结算已点过或异常跳转。
            TemplateProbeResult lobby = await _screen.ProbeAsync(
                window,
                _prepareMatcher,
                _config.DailyExercisesPrepareTopLeft,
                _config.DailyExercisesPrepareSize,
                cancellationToken,
                PrepareThreshold);
            if (lobby.IsMatch && sw.ElapsedMilliseconds > 2500)
            {
                _log($"每日演习：已回到大厅（出撃準備 {lobby.Score:F4}）。");
                return true;
            }

            TemplateProbeResult next = await _screen.ProbeAsync(
                window,
                _nextMatcher,
                _config.MainQuestNextTopLeft,
                _config.MainQuestNextSize,
                cancellationToken,
                NextThreshold);
            if (next.IsMatch && ReadyToClick(lastNextAt, 800))
            {
                lastNextAt = DateTime.UtcNow;
                _log($"每日演习：已识别「次へ」（{next.Score:F4}），点击。");
                await _screen.ClickProbeAsync(window, next, "次へ", cancellationToken, settleDelayMs: AfterNextDelayMs);
                window = _screen.Refresh(window);

                TemplateProbeResult back = await _screen.WaitForProbeAsync(
                    window,
                    _prepareMatcher,
                    _config.DailyExercisesPrepareTopLeft,
                    _config.DailyExercisesPrepareSize,
                    cancellationToken,
                    timeoutMs: LobbyTimeoutMs,
                    matchThreshold: PrepareThreshold);
                if (back.IsMatch)
                {
                    _log($"每日演习：结算后已回大厅（{back.Score:F4}）。");
                    return true;
                }

                _log($"每日演习：点「次へ」后未回大厅（最高 {back.Score:F4}），继续等待。");
                continue;
            }

            TemplateProbeResult skip = await _screen.ProbeAsync(
                window,
                _skipMatcher,
                _config.BattleSkipTopLeft,
                _config.BattleSkipSize,
                cancellationToken,
                SkipThreshold);
            if (!skip.IsMatch)
            {
                skip = await _screen.ProbeAsync(
                    window,
                    _skipAltMatcher,
                    _config.MainQuestSkipTopLeft,
                    _config.MainQuestSkipSize,
                    cancellationToken,
                    0.52);
            }

            if (skip.IsMatch && ReadyToClick(lastSkipAt, 600))
            {
                lastSkipAt = DateTime.UtcNow;
                _log($"每日演习：点击 SKIP（{skip.Score:F4}）。");
                await _screen.ClickProbeAsync(window, skip, "SKIP", cancellationToken, settleDelayMs: AfterSkipDelayMs);
                continue;
            }

            if (skip.Score >= SkipFallbackScore && ReadyToClick(lastSkipAt, 1500))
            {
                lastSkipAt = DateTime.UtcNow;
                _log($"每日演习：SKIP 分偏高（{skip.Score:F2}），写死点兜底。");
                await _screen.ClickAsync(window, _config.BattleSkipClick, "SKIP(坐标)", cancellationToken);
            }

            await Task.Delay(280, cancellationToken);
        }

        return false;
    }

    private static bool ReadyToClick(DateTime lastAt, int cooldownMs) =>
        (DateTime.UtcNow - lastAt).TotalMilliseconds >= cooldownMs;

    public static DateOnly CurrentExercisesDay(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime date = local.Date;
        if (local.Hour < ResetHour)
            date = date.AddDays(-1);
        return DateOnly.FromDateTime(date);
    }

    public static string CurrentExercisesDayKey(DateTime now) =>
        CurrentExercisesDay(now).ToString("yyyy-MM-dd");

    public static bool QuotaFilledThisDay(string? lastDayKey, DateTime now) =>
        !string.IsNullOrWhiteSpace(lastDayKey) &&
        string.Equals(lastDayKey, CurrentExercisesDayKey(now), StringComparison.Ordinal);

    public static DateTime NextReset(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime todayReset = local.Date.AddHours(ResetHour);
        return local < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
