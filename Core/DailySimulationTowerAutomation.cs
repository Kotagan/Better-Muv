using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日模拟战斗爬塔：主页 → 战斗 → 模拟战斗 → 当日属性塔 → 通用出击/SKIP/次へ。
/// 不读次数、不计数；每座塔循环出击直到点不出「出撃開始」。
/// </summary>
public sealed class DailySimulationTowerAutomation
{
    /// <summary>单塔最长墙钟，仅防死挂；不作次数配额。</summary>
    private static readonly TimeSpan TowerWallClockLimit = TimeSpan.FromMinutes(90);
    /// <summary>点完出撃準備后，出撃開始认不出则判定该塔打满/不可出击。</summary>
    private const int SortieMissTimeoutMs = 4000;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _entry = TemplateAssets.Load("quest-battle-simulate.png");
    private readonly TemplateMatcher _title = TemplateAssets.Load("simulation-title.png");
    private readonly TemplateMatcher _towerList = TemplateAssets.Load("simulation-tower-list-ranking.png");
    private readonly TemplateMatcher _prepare = TemplateAssets.Load("simulation-prepare.png");
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
        DayOfWeek.Wednesday => ["earth"], DayOfWeek.Thursday => ["wood"],
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

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        DateTime now = DateTime.Now;
        string dayKey = DailyExercisesAutomation.CurrentExercisesDayKey(now);
        if (string.Equals(_config.LastDailySimulationTowerStableLoopDay, dayKey, StringComparison.Ordinal))
        { _log("每日模拟战斗：今日已完成。"); return; }

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
            _log($"每日模拟战斗：选择{name}（{point.X},{point.Y}），打到点不出撃为止。");
            window = await _screen.ClickAsync(_screen.Refresh(window), point, name, cancellationToken);
            await Task.Delay(1200, cancellationToken);

            bool towerOk = await PushTowerUntilCannotSortieAsync(window, name, cancellationToken);
            if (!towerOk)
            {
                _log($"每日模拟战斗：{name}中途中断，停止本日任务。");
                break;
            }

            finishedTowers++;
            _log($"每日模拟战斗：{name}已推到点不下。");

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
            _config.LastDailySimulationTowerDay = dayKey;
            _config.LastDailySimulationTowerTenRunDay = dayKey;
            _config.LastDailySimulationTowerStableLoopDay = dayKey;
            ConfigStore.Save(_config);
            _log($"每日模拟战斗：今日 {finishedTowers} 座塔均已推完，记录今日完成。");
        }
        else _log($"每日模拟战斗：完成 {finishedTowers}/{towerKeys.Count} 座塔，不写完成标记。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(_screen.Refresh(window), cancellationToken);
    }

    /// <summary>循环出击直到点不出「出撃開始」；不读剩余次数、不累计战次。</summary>
    private async Task<bool> PushTowerUntilCannotSortieAsync(
        GameWindow window, string name, CancellationToken cancellationToken)
    {
        var wall = System.Diagnostics.Stopwatch.StartNew();
        while (wall.Elapsed < TowerWallClockLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            TemplateProbeResult prepare = await _screen.WaitForProbeAsync(window, _prepare,
                _config.DailySimulationPrepareTopLeft, _config.DailySimulationPrepareSize,
                cancellationToken, timeoutMs: 2500, matchThreshold: 0.70);
            if (!prepare.IsMatch)
            {
                window = await _screen.ClickAsync(window, _config.DailySimulationPrepareClick,
                    "模拟战斗 出撃準備（固定位置）", cancellationToken);
                await Task.Delay(1200, cancellationToken);
            }
            else
                await _screen.ClickProbeAsync(window, prepare, "模拟战斗 出撃準備", cancellationToken, 1200);

            TemplateProbeResult sortie = await _screen.WaitForProbeAsync(window, _sortie,
                _config.MainQuestSortieTopLeft, _config.MainQuestSortieSize,
                cancellationToken, timeoutMs: SortieMissTimeoutMs, matchThreshold: 0.70);
            if (!sortie.IsMatch)
            {
                _log($"每日模拟战斗：{name} {SortieMissTimeoutMs / 1000}s 内点不出「出撃開始」（最高 {sortie.Score:F3}），该塔结束。");
                return true;
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
        return true;
    }

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
