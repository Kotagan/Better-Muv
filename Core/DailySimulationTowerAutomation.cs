using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日模拟战斗爬塔：主页 → 战斗 → 模拟战斗 → 当日属性塔 → 通用出击/SKIP/次へ。
/// 每座塔循环出击；某塔中途失败则跳过并切下一座；禁止持久化完成标记。
/// </summary>
public sealed class DailySimulationTowerAutomation
{
    /// <summary>单塔最长墙钟，仅防死挂；不作次数配额。</summary>
    private static readonly TimeSpan TowerWallClockLimit = TimeSpan.FromMinutes(90);
    private const int SortieMissTimeoutMs = 8000;
    private const double RemainingZeroStrongThreshold = 0.98;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _entry = TemplateAssets.Load("quest-battle-simulate.png");
    private readonly TemplateMatcher _title = TemplateAssets.Load("simulation-title.png");
    private readonly TemplateMatcher _towerList = TemplateAssets.Load("simulation-tower-list-ranking.png");
    private readonly TemplateMatcher _prepare = TemplateAssets.Load("simulation-prepare.png");
    private readonly TemplateMatcher _remainingZero = TemplateAssets.Load("simulation-remaining-zero.png");
    private readonly TemplateMatcher _sortie = TemplateAssets.Load("main-quest-sortie.png");
    private readonly TemplateMatcher _skip = TemplateAssets.Load("battle-skip.png");
    private readonly TemplateMatcher _skipAlt = TemplateAssets.Load("main-quest-skip.png");
    private readonly TemplateMatcher _next = TemplateAssets.Load("main-quest-next.png");
    private readonly BattleResultHome _resultHome;

    public DailySimulationTowerAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _resultHome = new BattleResultHome(config, _screen, log);
    }

    public static IReadOnlyList<string> TowerKeysFor(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => ["fire"], DayOfWeek.Tuesday => ["water"],
        DayOfWeek.Wednesday => ["wood"], DayOfWeek.Thursday => ["earth"],
        DayOfWeek.Friday => ["water", "fire"], DayOfWeek.Saturday => ["earth", "wood"],
        _ => ["fire", "water", "earth", "wood"]
    };

    public static string TowerNameFor(string key) => key switch
    {
        "fire" => "火塔", "water" => "水塔", "earth" => "土塔", "wood" => "木塔",
        _ => key
    };

    public static ConfigPoint TowerPointFor(string key) => key switch
    {
        "fire" => new ConfigPoint(500, 635),
        "water" => new ConfigPoint(1420, 635),
        "wood" => new ConfigPoint(500, 835),
        "earth" => new ConfigPoint(1420, 835),
        _ => new ConfigPoint(960, 540)
    };

    public async Task<TaskRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        DateTime now = DateTime.Now;
        GameWindow? window = null;
        try
        {
            window = _screen.FindWindow(_config.WindowTitleKeyword);
            if (!await _screen.FocusAsync(window.Handle, cancellationToken))
                throw new InvalidOperationException("未能将游戏置于前台");
            window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
            window = await new QuestFromHomeEntry(_config, _screen, _log).RunAsync(
                window, QuestFromHomeEntry.BattleSimulateClick(_config), "模拟战斗", cancellationToken,
                _entry, _config.QuestBattleSimulateTopLeft, _config.QuestBattleSimulateSize,
                singleClickTarget: true);
            TemplateProbeResult title = await _screen.WaitForProbeAsync(window, _title,
                _config.DailySimulationTitleTopLeft, _config.DailySimulationTitleSize,
                cancellationToken, timeoutMs: 10_000, matchThreshold: 0.72);
            if (!title.IsMatch)
                throw new InvalidOperationException($"模拟战斗页面未就绪：左上标题未识别（最高 {title.Score:F4}）。");
            TemplateProbeResult towerList = await _screen.WaitForProbeAsync(window, _towerList,
                _config.DailySimulationListTopLeft, _config.DailySimulationListSize,
                cancellationToken, timeoutMs: 10_000, matchThreshold: 0.72);
            if (!towerList.IsMatch)
                throw new InvalidOperationException($"模拟战斗页面未就绪：未识别到塔列表（最高 {towerList.Score:F4}），停止点击属性塔。");
            _log($"每日模拟战斗：已确认塔列表（标题 {title.Score:F3}，列表 {towerList.Score:F3}）。");
            window = _screen.Refresh(window);

            DayOfWeek gameDay = DailyExercisesAutomation.CurrentExercisesDay(now).DayOfWeek;
            IReadOnlyList<string> towerKeys = TowerKeysFor(gameDay);
            _log("每日模拟战斗：今日依次挑战" + string.Join("、", towerKeys.Select(TowerNameFor)) + "。");
            int finishedTowers = 0;
            int skippedTowers = 0;
            for (int towerIndex = 0; towerIndex < towerKeys.Count; towerIndex++)
            {
                string key = towerKeys[towerIndex];
                string name = TowerNameFor(key);
                ConfigPoint point = TowerPointFor(key);
                _log($"每日模拟战斗：选择{name}（{point.X},{point.Y}），持续出击直到确认今日剩余 0 次。");
                window = await _screen.ClickAsync(_screen.Refresh(window), point, name, cancellationToken);
                await Task.Delay(1200, cancellationToken);

                bool towerOk = await PushTowerUntilCannotSortieAsync(window, name, cancellationToken);
                bool hasNext = towerIndex + 1 < towerKeys.Count;
                if (!towerOk)
                {
                    skippedTowers++;
                    _log($"每日模拟战斗：{name}中途失败，点ホームへ后跳过"
                        + $"（已完成 {finishedTowers}，已跳过 {skippedTowers}）。");
                    window = await LeaveResultOrHomeAsync(window, $"{name}失败", cancellationToken);
                    if (!hasNext)
                        break;

                    (window, bool backOk) = await TryResumeTowerListForNextAsync(
                        window, $"{name}失败后", cancellationToken);
                    if (!backOk)
                    {
                        _log($"每日模拟战斗：{name}失败后未能回到塔列表，停止切换下一塔。");
                        break;
                    }

                    continue;
                }

                finishedTowers++;
                _log($"每日模拟战斗：{name}已确认今日剩余 0 次，本次不再出击。");

                if (!hasNext)
                    continue;

                (window, bool listOk) = await TryReturnToTowerListAsync(
                    window, $"完成{name}后", cancellationToken);
                if (!listOk)
                {
                    _log($"每日模拟战斗：完成{name}后未返回塔列表，停止切换下一塔。");
                    break;
                }
            }

            // 成功/失败都要退出回主页；失败路径用 None，避免停止令牌已取消导致退不出。
            CancellationToken homeToken = cancellationToken.IsCancellationRequested
                ? CancellationToken.None
                : cancellationToken;
            if (finishedTowers == towerKeys.Count)
            {
                _log($"每日模拟战斗：今日 {finishedTowers} 座塔均已确认剩余 0 次；不写任何完成标记。");
                await TryExitToHomeAsync(window, homeToken);
                return TaskRunResult.Success();
            }

            string skipText = skippedTowers > 0 ? $"，跳过 {skippedTowers}" : "";
            _log($"每日模拟战斗：完成 {finishedTowers}/{towerKeys.Count} 座塔{skipText}，不写完成标记。");
            await TryExitToHomeAsync(window, CancellationToken.None);
            return TaskRunResult.Fail($"仅完成 {finishedTowers}/{towerKeys.Count} 座塔");
        }
        catch (OperationCanceledException)
        {
            _log("每日模拟战斗：已取消，执行退出回主页。");
            await TryExitToHomeAsync(window, CancellationToken.None);
            throw;
        }
    }

    private async Task TryExitToHomeAsync(GameWindow? window, CancellationToken cancellationToken)
    {
        try
        {
            window ??= _screen.FindWindow(_config.WindowTitleKeyword);
            window = await LeaveResultOrHomeAsync(window, "每日模拟战斗退出", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"每日模拟战斗：退出回主页失败：{ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // 退出流程使用 None 时不应走到这里；忽略以免掩盖主结果。
        }
    }

    /// <summary>结算/战败优先点「ホームへ」，再尝试 HUD 房子钮。</summary>
    private async Task<GameWindow> LeaveResultOrHomeAsync(
        GameWindow window, string reason, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        if (await _resultHome.TryClickHomeAsync(window, reason, cancellationToken))
        {
            await Task.Delay(700, cancellationToken);
            return _screen.Refresh(window);
        }

        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        return _screen.Refresh(window);
    }

    /// <summary>失败回主页后重新进模拟战斗塔列表，以便切下一座。</summary>
    private async Task<(GameWindow Window, bool Ok)> TryResumeTowerListForNextAsync(
        GameWindow window, string reason, CancellationToken cancellationToken)
    {
        (window, bool listOk) = await TryReturnToTowerListAsync(window, reason, cancellationToken);
        if (listOk)
            return (window, true);

        _log($"每日模拟战斗：{reason}未在塔列表，从主页重新进入模拟战斗。");
        try
        {
            window = await new QuestFromHomeEntry(_config, _screen, _log).RunAsync(
                window, QuestFromHomeEntry.BattleSimulateClick(_config), "模拟战斗", cancellationToken,
                _entry, _config.QuestBattleSimulateTopLeft, _config.QuestBattleSimulateSize,
                singleClickTarget: true);
            TemplateProbeResult title = await _screen.WaitForProbeAsync(window, _title,
                _config.DailySimulationTitleTopLeft, _config.DailySimulationTitleSize,
                cancellationToken, timeoutMs: 10_000, matchThreshold: 0.72);
            TemplateProbeResult list = await _screen.WaitForProbeAsync(window, _towerList,
                _config.DailySimulationListTopLeft, _config.DailySimulationListSize,
                cancellationToken, timeoutMs: 10_000, matchThreshold: 0.72);
            if (title.IsMatch && list.IsMatch)
            {
                _log($"每日模拟战斗：已重新进入塔列表（标题 {title.Score:F3}，列表 {list.Score:F3}）。");
                return (_screen.Refresh(window), true);
            }

            _log($"每日模拟战斗：重新进入后未确认塔列表（标题 {title.Score:F3}，列表 {list.Score:F3}）。");
            return (_screen.Refresh(window), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"每日模拟战斗：重新进入模拟战斗失败：{ex.Message}");
            return (_screen.Refresh(window), false);
        }
    }

    /// <summary>从关卡/结算页回到四塔列表，供切下一座塔。</summary>
    private async Task<(GameWindow Window, bool Ok)> TryReturnToTowerListAsync(
        GameWindow window, string reason, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        TemplateProbeResult already = await _screen.ProbeAsync(
            window, _towerList,
            _config.DailySimulationListTopLeft, _config.DailySimulationListSize,
            cancellationToken, 0.72);
        if (already.IsMatch)
            return (window, true);

        // 结算卡住时先 Esc / 点次へ，再返回。
        for (int i = 0; i < 2; i++)
        {
            TemplateProbeResult next = await _screen.ProbeAsync(
                window, _next,
                _config.MainQuestNextTopLeft, _config.MainQuestNextSize,
                cancellationToken, 0.68);
            if (next.IsMatch)
            {
                await _screen.ClickProbeAsync(window, next, $"模拟战斗 {reason}次へ", cancellationToken, 700);
                window = _screen.Refresh(window);
            }
            else
                break;
        }

        window = await _screen.ClickAsync(
            window, QuestFromHomeEntry.NavBackClick(_config), $"返回塔列表（{reason}）", cancellationToken);
        TemplateProbeResult list = await _screen.WaitForProbeAsync(
            window, _towerList,
            _config.DailySimulationListTopLeft, _config.DailySimulationListSize,
            cancellationToken, timeoutMs: 8_000, matchThreshold: 0.72);
        if (list.IsMatch)
            return (_screen.Refresh(window), true);

        _log($"每日模拟战斗：{reason}返回未命中塔列表（最高 {list.Score:F3}），Esc 后再试。");
        for (int i = 0; i < 2; i++)
        {
            await _screen.Mouse.SendEscapeAsync(window.Handle, cancellationToken);
            await Task.Delay(350, cancellationToken);
            window = _screen.Refresh(window);
            TemplateProbeResult afterEsc = await _screen.ProbeAsync(
                window, _towerList,
                _config.DailySimulationListTopLeft, _config.DailySimulationListSize,
                cancellationToken, 0.72);
            if (afterEsc.IsMatch)
                return (window, true);
        }

        window = await _screen.ClickAsync(
            window, QuestFromHomeEntry.NavBackClick(_config), $"返回塔列表再试（{reason}）", cancellationToken);
        list = await _screen.WaitForProbeAsync(
            window, _towerList,
            _config.DailySimulationListTopLeft, _config.DailySimulationListSize,
            cancellationToken, timeoutMs: 8_000, matchThreshold: 0.72);
        return (_screen.Refresh(window), list.IsMatch);
    }

    /// <summary>循环出击，只有明确读到今日剩余 0 次才返回完成。</summary>
    private async Task<bool> PushTowerUntilCannotSortieAsync(
        GameWindow window, string name, CancellationToken cancellationToken)
    {
        var wall = System.Diagnostics.Stopwatch.StartNew();
        while (wall.Elapsed < TowerWallClockLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            if (await IsRemainingZeroAsync(window, cancellationToken))
            {
                _log($"每日模拟战斗：{name}已确认「本日あと0回」。");
                return true;
            }

            TemplateProbeResult prepare = await _screen.WaitForProbeAsync(window, _prepare,
                _config.DailySimulationPrepareTopLeft, _config.DailySimulationPrepareSize,
                cancellationToken, timeoutMs: 6000, matchThreshold: 0.70);
            if (!prepare.IsMatch)
            {
                // 当前游戏版本的出撃準備按钮视觉已变化。只有确认已离开塔列表、
                // 且模拟战斗标题仍在时，才允许点经过实机校准的固定位置。
                TemplateProbeResult listStill = await _screen.ProbeAsync(
                    window, _towerList,
                    _config.DailySimulationListTopLeft, _config.DailySimulationListSize,
                    cancellationToken, 0.72);
                TemplateProbeResult stageTitle = await _screen.ProbeAsync(
                    window, _title,
                    _config.DailySimulationTitleTopLeft, _config.DailySimulationTitleSize,
                    cancellationToken, 0.72);
                if (listStill.IsMatch || !stageTitle.IsMatch)
                {
                    _log($"每日模拟战斗：{name}未识别到「出撃準備」（最高 {prepare.Score:F3}），" +
                         $"关卡页复核失败（塔列表 {listStill.Score:F3} / 标题 {stageTitle.Score:F3}），停止且不记完成。");
                    return false;
                }

                _log($"每日模拟战斗：{name}按钮模板未命中（最高 {prepare.Score:F3}），" +
                     "但已确认进入关卡页，点击校准后的「出撃準備」位置。");
                window = await _screen.ClickAsync(
                    window,
                    _config.DailySimulationPrepareClick,
                    "模拟战斗 出撃準備（关卡页确认后兜底）",
                    cancellationToken);
                await Task.Delay(1200, cancellationToken);
            }
            else
                await _screen.ClickProbeAsync(window, prepare, "模拟战斗 出撃準備", cancellationToken, 1200);

            TemplateProbeResult sortie = await _screen.WaitForProbeAsync(window, _sortie,
                _config.MainQuestSortieTopLeft, _config.MainQuestSortieSize,
                cancellationToken, timeoutMs: SortieMissTimeoutMs, matchThreshold: 0.70);
            if (!sortie.IsMatch)
            {
                _log($"每日模拟战斗：{name} {SortieMissTimeoutMs / 1000}s 内未识别「出撃開始」" +
                     $"（最高 {sortie.Score:F3}），停止且不记完成。");
                return false;
            }

            if (!await ClickSortieUntilBattleAsync(window, name, sortie, cancellationToken))
            {
                _log($"每日模拟战斗：{name}出撃未离开准备页，中断。");
                return false;
            }

            if (!await RunBattleAsync(window, cancellationToken))
            {
                _log($"每日模拟战斗：{name}战斗/结算未完成，中断。");
                return false;
            }

            await Task.Delay(1000, cancellationToken);
        }

        _log($"每日模拟战斗：{name}已跑满 {TowerWallClockLimit.TotalMinutes:0} 分钟墙钟，停止该塔。");
        return false;
    }

    private async Task<bool> IsRemainingZeroAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult zero = await _screen.ProbeAsync(
            window,
            _remainingZero,
            _config.DailySimulationRemainingTopLeft,
            _config.DailySimulationRemainingSize,
            cancellationToken,
            0.78);
        if (!zero.IsMatch)
            return false;

        // 「本日あとN回」中只有一个数字不同，整句模板会把 9/8 等高分误配成 0。
        // 模板仅作快速候选；最终必须由 OCR 明确读到整数 0。
        RegionCapture remainingRegion = _screen.CaptureRegion(
            window,
            _config.DailySimulationRemainingTopLeft,
            _config.DailySimulationRemainingSize);
        string? text = await DigitOcrService.TryReadTextAsync(remainingRegion.Image, cancellationToken);
        int? remaining = DigitOcrService.TryParseNonNegativeInt(text);
        if (remaining is int value)
        {
            _log($"每日模拟战斗：剩余次数候选模板 {zero.Score:F4}，OCR「{text}」→ {value}。");
            return value == 0;
        }

        if (IsStrongZeroMatch(zero.Score, remaining))
        {
            _log($"每日模拟战斗：剩余次数模板强命中 {zero.Score:F4}，OCR 未读出数字，按 0 次处理。");
            return true;
        }

        _log($"每日模拟战斗：剩余次数候选模板 {zero.Score:F4}，但 OCR 无法确认 0 次，继续运行。");
        return false;
    }

    public static bool IsStrongZeroMatch(double templateScore, int? ocrRemaining) =>
        ocrRemaining is null && templateScore >= RemainingZeroStrongThreshold;

    /// <summary>
    /// 点「出撃」直到离开准备页。日志常见命中 0.99 仍停在クエスト準備，需重试且勿立刻挪光标。
    /// </summary>
    private async Task<bool> ClickSortieUntilBattleAsync(
        GameWindow window, string name, TemplateProbeResult firstSortie, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        TemplateProbeResult sortie = firstSortie;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            if (!await _screen.FocusAsync(window.Handle, cancellationToken))
                _log($"每日模拟战斗：{name}未能置前台，「出撃」可能点不中。");

            _log($"每日模拟战斗：{name}点击「出撃」（{sortie.Score:F3}，{attempt}/{maxAttempts}）。");
            await _screen.ClickScreenAsync(window, sortie.Center, cancellationToken, parkCursor: false);
            await Task.Delay(1600, cancellationToken);
            _screen.ParkCursorAway(window);
            window = _screen.Refresh(window);

            TemplateProbeResult skip = await _screen.ProbeAsync(
                window, _skip, _config.BattleSkipTopLeft, _config.BattleSkipSize, cancellationToken, 0.58);
            if (!skip.IsMatch)
                skip = await _screen.ProbeAsync(
                    window, _skipAlt, _config.MainQuestSkipTopLeft, _config.MainQuestSkipSize,
                    cancellationToken, 0.52);
            if (skip.IsMatch)
            {
                _log($"每日模拟战斗：{name}已进入战斗（SKIP {skip.Score:F3}）。");
                return true;
            }

            TemplateProbeResult stillSortie = await _screen.ProbeAsync(
                window, _sortie, _config.MainQuestSortieTopLeft, _config.MainQuestSortieSize,
                cancellationToken, 0.80);
            if (!stillSortie.IsMatch)
            {
                _log($"每日模拟战斗：{name}准备页「出撃」已消失，进入战斗等待。");
                return true;
            }

            _log($"每日模拟战斗：{name}仍在准备页（出撃 {stillSortie.Score:F3}），重试。");
            sortie = stillSortie;
        }

        return false;
    }

    private async Task<bool> RunBattleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        DateTime lastProgressLog = DateTime.MinValue;
        while (timer.ElapsedMilliseconds < 180_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            // 战败/再戦：直接点ホームへ，结束本塔（不点再戦空耗）。
            if (await _resultHome.LooksLikeDefeatOrRematchAsync(window, cancellationToken))
            {
                if (await _resultHome.TryClickHomeAsync(window, "每日模拟战斗战败", cancellationToken))
                {
                    _log("每日模拟战斗：已点ホームへ离开战败页，本塔中止。");
                    return false;
                }
            }

            // 仍停在准备页说明出撃未生效，尽快失败交给外层重试/中断，勿空等 3 分钟。
            if (timer.ElapsedMilliseconds >= 12_000)
            {
                TemplateProbeResult stillSortie = await _screen.ProbeAsync(
                    window, _sortie, _config.MainQuestSortieTopLeft, _config.MainQuestSortieSize,
                    cancellationToken, 0.85);
                if (stillSortie.IsMatch)
                {
                    _log($"每日模拟战斗：战斗等待中仍见「出撃」（{stillSortie.Score:F3}，"
                        + $"已 {timer.Elapsed.TotalSeconds:0}s），判定未开战。");
                    return false;
                }
            }

            TemplateProbeResult next = await _screen.ProbeAsync(window, _next,
                _config.MainQuestNextTopLeft, _config.MainQuestNextSize, cancellationToken, 0.70);
            if (next.IsMatch)
            {
                await _screen.ClickProbeAsync(window, next, "模拟战斗 次へ", cancellationToken, 1000);
                return await AdvanceSettlementToTowerStageAsync(_screen.Refresh(window), cancellationToken);
            }

            TemplateProbeResult skip = await _screen.ProbeAsync(window, _skip,
                _config.BattleSkipTopLeft, _config.BattleSkipSize, cancellationToken, 0.58);
            if (!skip.IsMatch)
                skip = await _screen.ProbeAsync(window, _skipAlt, _config.MainQuestSkipTopLeft,
                    _config.MainQuestSkipSize, cancellationToken, 0.52);
            if (skip.IsMatch)
            {
                await _screen.ClickProbeAsync(window, skip, "模拟战斗 SKIP", cancellationToken, 180);
            }
            else
            {
                if ((DateTime.UtcNow - lastProgressLog).TotalSeconds >= 10)
                {
                    lastProgressLog = DateTime.UtcNow;
                    _log($"每日模拟战斗：战斗中未命中 SKIP/次へ/ホームへ（已 {timer.Elapsed.TotalSeconds:0}s / 180s）。");
                }
                await Task.Delay(250, cancellationToken);
            }
        }

        // 超时前再试一次ホームへ，避免卡在 DEFEAT。
        window = _screen.Refresh(window);
        if (await _resultHome.TryClickHomeAsync(window, "每日模拟战斗超时", cancellationToken))
            _log("每日模拟战斗：战斗超时后已点ホームへ。");
        return false;
    }

    private async Task<bool> AdvanceSettlementToTowerStageAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        // FLASH SALE 等宣传层会盖住结算「次へ」：连点无效时需清弹窗 / Esc。
        var promo = new PromoPopupDismisser(_config, _screen, _log);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int stagnantNextClicks = 0;
        while (timer.ElapsedMilliseconds < 35_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            TemplateProbeResult towerStage = await _screen.ProbeAsync(window, _title,
                _config.DailySimulationTitleTopLeft, _config.DailySimulationTitleSize,
                cancellationToken, 0.72);
            if (towerStage.IsMatch)
            {
                _log($"每日模拟战斗：已返回塔关卡页（标题 {towerStage.Score:F3}）。");
                return true;
            }

            if (await promo.DismissAllAsync(window, cancellationToken) > 0)
            {
                stagnantNextClicks = 0;
                await Task.Delay(400, cancellationToken);
                continue;
            }

            TemplateProbeResult next = await _screen.ProbeAsync(window, _next,
                _config.MainQuestNextTopLeft, _config.MainQuestNextSize,
                cancellationToken, 0.68);
            if (next.IsMatch)
            {
                await _screen.ClickProbeAsync(window, next, "模拟战斗 奖励次へ", cancellationToken, 900);
                stagnantNextClicks++;
                // 点了仍像卡在结算（常见被 FLASH SALE 挡住）：Esc 清层后再试。
                if (stagnantNextClicks >= 3)
                {
                    _log("每日模拟战斗：结算「次へ」多次无效，发送 Esc 清弹层。");
                    await _screen.Mouse.SendEscapeAsync(window.Handle, cancellationToken);
                    await Task.Delay(500, cancellationToken);
                    stagnantNextClicks = 0;
                }

                continue;
            }

            stagnantNextClicks = 0;
            await Task.Delay(250, cancellationToken);
        }
        _log("每日模拟战斗：结算后未能返回塔关卡页。");
        return false;
    }
}
