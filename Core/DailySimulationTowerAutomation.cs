using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日模拟战斗爬塔：主页 → 战斗 → 模拟战斗 → 当日属性塔 → 通用出击/SKIP/次へ。
/// 每座塔循环出击；识别到「本日あと0回」仅结束本次运行，禁止持久化完成标记。
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
    private readonly TemplateMatcher _rematch = TemplateAssets.Load("main-quest-rematch.png");

    public DailySimulationTowerAutomation(AutomationConfig config, Action<string> log)
    { _config = config; _log = log; _screen = new ScreenAutomation(config, log); }

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
        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
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
        for (int towerIndex = 0; towerIndex < towerKeys.Count; towerIndex++)
        {
            string key = towerKeys[towerIndex];
            string name = TowerNameFor(key);
            ConfigPoint point = TowerPointFor(key);
            _log($"每日模拟战斗：选择{name}（{point.X},{point.Y}），持续出击直到确认今日剩余 0 次。");
            window = await _screen.ClickAsync(_screen.Refresh(window), point, name, cancellationToken);
            await Task.Delay(1200, cancellationToken);

            bool towerOk = await PushTowerUntilCannotSortieAsync(window, name, cancellationToken);
            if (!towerOk)
            {
                _log($"每日模拟战斗：{name}中途中断，停止本日任务。");
                break;
            }

            finishedTowers++;
            _log($"每日模拟战斗：{name}已确认今日剩余 0 次，本次不再出击。");

            if (towerIndex + 1 < towerKeys.Count)
            {
                window = await _screen.ClickAsync(_screen.Refresh(window), QuestFromHomeEntry.NavBackClick(_config),
                    "返回塔列表", cancellationToken);
                TemplateProbeResult listAgain = await _screen.WaitForProbeAsync(window, _towerList,
                    _config.DailySimulationListTopLeft, _config.DailySimulationListSize,
                    cancellationToken, timeoutMs: 10_000, matchThreshold: 0.72);
                if (!listAgain.IsMatch)
                {
                    _log($"每日模拟战斗：完成{name}后未返回塔列表，停止切换下一塔。");
                    break;
                }
                window = _screen.Refresh(window);
            }
        }

        if (finishedTowers == towerKeys.Count)
        {
            _log($"每日模拟战斗：今日 {finishedTowers} 座塔均已确认剩余 0 次；不写任何完成标记。");
            await new HudHomeReturn(_config, _screen, _log).TryAsync(_screen.Refresh(window), cancellationToken);
            return TaskRunResult.Success();
        }
        _log($"每日模拟战斗：完成 {finishedTowers}/{towerKeys.Count} 座塔，不写完成标记。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(_screen.Refresh(window), cancellationToken);
        return TaskRunResult.Fail($"仅完成 {finishedTowers}/{towerKeys.Count} 座塔");
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

            await _screen.ClickProbeAsync(window, sortie, "模拟战斗 出撃開始", cancellationToken, 1400);
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

    private async Task<bool> RunBattleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 180_000)
        {
            cancellationToken.ThrowIfCancellationRequested(); window = _screen.Refresh(window);
            TemplateProbeResult next = await _screen.ProbeAsync(window, _next,
                _config.MainQuestNextTopLeft, _config.MainQuestNextSize, cancellationToken, 0.70);
            TemplateProbeResult rematch = await _screen.ProbeAsync(window, _rematch,
                _config.MainQuestRematchTopLeft, _config.MainQuestRematchSize, cancellationToken, 0.72);
            if (next.IsMatch || rematch.IsMatch)
            {
                TemplateProbeResult done = next.IsMatch ? next : rematch;
                await _screen.ClickProbeAsync(window, done, next.IsMatch ? "模拟战斗 次へ" : "模拟战斗 再戦",
                    cancellationToken, 1000);
                return await AdvanceSettlementToTowerStageAsync(_screen.Refresh(window), cancellationToken);
            }
            TemplateProbeResult skip = await _screen.ProbeAsync(window, _skip,
                _config.BattleSkipTopLeft, _config.BattleSkipSize, cancellationToken, 0.58);
            if (!skip.IsMatch)
                skip = await _screen.ProbeAsync(window, _skipAlt, _config.MainQuestSkipTopLeft,
                    _config.MainQuestSkipSize, cancellationToken, 0.52);
            if (skip.IsMatch) await _screen.ClickProbeAsync(window, skip, "模拟战斗 SKIP", cancellationToken, 180);
            else await Task.Delay(250, cancellationToken);
        }
        return false;
    }

    private async Task<bool> AdvanceSettlementToTowerStageAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 25_000)
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

            TemplateProbeResult next = await _screen.ProbeAsync(window, _next,
                _config.MainQuestNextTopLeft, _config.MainQuestNextSize,
                cancellationToken, 0.68);
            if (next.IsMatch)
            {
                await _screen.ClickProbeAsync(window, next, "模拟战斗 奖励次へ", cancellationToken, 900);
                continue;
            }
            await Task.Delay(250, cancellationToken);
        }
        _log("每日模拟战斗：结算后未能返回塔关卡页。");
        return false;
    }
}
