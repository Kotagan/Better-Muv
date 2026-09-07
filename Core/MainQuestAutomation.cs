using System.Diagnostics;
using System.Windows;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 自动主线：主页 → 任务 → 主线 → 开始 → 出击/情景再现 → SKIP 或剧情加速 → 奖励 OK → 下一步…
/// 出现「再戦」时点击「ホームへ」并结束。
/// </summary>
public sealed class MainQuestAutomation
{
    private const double PresenceThreshold = 0.62;
    private const int MissTimeoutMs = 120000;
    private const int ScenarioMissTimeoutMs = 900000;
    /// <summary>同键连点冷却。</summary>
    private const int ClickCooldownMs = 100;
    /// <summary>任务开始按钮出现后的稳定等待。</summary>
    private const int StartAppearSettleMs = 100;

    private enum Phase { Start, Sortie, Battle, Scenario, Next }

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _start;
    private readonly TemplateMatcher _sortie;
    private readonly TemplateMatcher _scenarioReplay;
    private readonly TemplateMatcher _scenarioMenu;
    private readonly TemplateMatcher _scenarioSpeed;
    private readonly TemplateMatcher _scenarioOk;
    private readonly TemplateMatcher _scenarioChoice;
    private readonly TemplateMatcher _scenarioChoiceAlt;
    private readonly TemplateMatcher _scenarioPortrait;
    private readonly TemplateMatcher _skip;
    private readonly TemplateMatcher _skipAlt;
    private readonly TemplateMatcher _next;
    private readonly TemplateMatcher _rematch;
    private readonly TemplateMatcher _toHome;
    private readonly PromoPopupDismisser _promoPopup;

    public MainQuestAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _start = TemplateAssets.Load("main-quest-start.png");
        _sortie = TemplateAssets.Load("main-quest-sortie.png");
        _scenarioReplay = TemplateAssets.Load("main-quest-scenario-replay.png");
        _scenarioMenu = TemplateAssets.Load("main-quest-scenario-menu.png");
        _scenarioSpeed = TemplateAssets.Load("main-quest-scenario-speed.png");
        _scenarioOk = TemplateAssets.Load("main-quest-scenario-ok.png");
        _scenarioChoice = TemplateAssets.Load("main-quest-scenario-choice.png");
        _scenarioChoiceAlt = TemplateAssets.Load("main-quest-scenario-choice-alt.png");
        _scenarioPortrait = TemplateAssets.Load("main-quest-scenario-choice-portrait.png");
        _skip = TemplateAssets.Load("main-quest-skip.png");
        _skipAlt = TemplateAssets.Load("battle-skip.png");
        _next = TemplateAssets.Load("main-quest-next.png");
        _rematch = TemplateAssets.Load("main-quest-rematch.png");
        _toHome = TemplateAssets.Load("main-quest-to-home.png");
        _promoPopup = new PromoPopupDismisser(config, _screen, log);
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"自动主线：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return;
        }

        await Task.Delay(200, cancellationToken);
        window = _screen.Refresh(window);
        _screen.EnsureUsableViewport(window);
        _log($"自动主线：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}，显示器 {window.DisplayRect.Width}×{window.DisplayRect.Height}");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        _log("自动主线：开局只看是否已在关卡内；否则回主页连点进主线。通关点「下一步」后继续；剧情开菜单加速；再戦则回主页结束。");
        _log($"自动主线：模板逻辑尺寸 start={_start.LogicalWidth}×{_start.LogicalHeight}");

        (Phase phase, bool abort) = await BootstrapEntryAsync(window, cancellationToken);
        if (abort)
            return;

        int completed = 0;
        string? lastClick = null;
        var lastClickAt = Stopwatch.StartNew();
        var missTimer = Stopwatch.StartNew();
        var lastLog = Stopwatch.StartNew();
        Stopwatch? battleSkipFallback = null;
        Stopwatch? startAppearAt = null;
        bool scenarioSped = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            if (ShouldCheckPromoPopup(phase) &&
                await _promoPopup.TryAsync(window, cancellationToken))
            {
                missTimer.Restart();
                await Task.Delay(250, cancellationToken);
                continue;
            }

            // 进关后不再扫主页/任务页；只盯当前阶段必要模板。
            IReadOnlyList<(string Key, TemplateMatcher Matcher)> active = phase switch
            {
                Phase.Start =>
                [
                    ("start", _start), ("sortie", _sortie),
                    ("scenarioReplay", _scenarioReplay)
                ],
                Phase.Sortie =>
                [
                    ("scenarioOk", _scenarioOk), ("sortie", _sortie), ("scenarioReplay", _scenarioReplay),
                    ("skip", _skip), ("skipAlt", _skipAlt), ("next", _next), ("rematch", _rematch), ("toHome", _toHome)
                ],
                Phase.Battle => [("scenarioOk", _scenarioOk), ("skip", _skip), ("skipAlt", _skipAlt), ("next", _next), ("rematch", _rematch), ("toHome", _toHome)],
                Phase.Scenario => scenarioSped
                    ?
                    [
                        ("scenarioOk", _scenarioOk),
                        ("scenarioReplay", _scenarioReplay),
                        ("scenarioChoice", _scenarioChoice),
                        ("scenarioChoiceAlt", _scenarioChoiceAlt),
                        ("scenarioPortrait", _scenarioPortrait),
                        ("next", _next),
                        ("start", _start),
                        ("sortie", _sortie),
                        ("rematch", _rematch),
                        ("toHome", _toHome)
                    ]
                    :
                    [
                        ("scenarioMenu", _scenarioMenu),
                        ("scenarioSpeed", _scenarioSpeed),
                        ("scenarioOk", _scenarioOk),
                        ("scenarioReplay", _scenarioReplay),
                        ("next", _next),
                        ("rematch", _rematch),
                        ("toHome", _toHome)
                    ],
                _ => [("scenarioOk", _scenarioOk), ("next", _next), ("start", _start), ("rematch", _rematch), ("toHome", _toHome)]
            };

            IReadOnlyDictionary<string, TemplateProbeResult> probes;
            long probeMs;
            try
            {
                var probeClock = Stopwatch.StartNew();
                probes = await ProbeClientAsync(window, active, cancellationToken);
                probeMs = probeClock.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                _log($"主线探测异常：{ex.Message}");
                await Task.Delay(200, cancellationToken);
                continue;
            }

            if (lastLog.ElapsedMilliseconds >= 2000)
            {
                string snap = string.Join(' ', active.Select(a =>
                    probes.TryGetValue(a.Key, out TemplateProbeResult? p)
                        ? $"{a.Key}={p.Score:F2}{(p.IsMatch ? "*" : "")}"
                        : a.Key));
                string sped = phase == Phase.Scenario ? $" sped={scenarioSped}" : "";
                _log($"主线 已通关{completed} {probeMs}ms phase={phase}{sped} {snap}");
                lastLog.Restart();
            }
            // 必须以「再戦」为准；単独 toHome 易在其它界面误匹配，不能当失败。
            if (TryHit(probes, "rematch", out _))
            {
                _log("检测到再戦，点击ホームへ后结束自动主线。");
                if (TryHit(probes, "toHome", out TemplateProbeResult homeBtn))
                    await ClickMatchAsync(window, homeBtn, "ホームへ", cancellationToken);
                else if (!await WaitForClickAsync(window, "toHome", [("toHome", _toHome)], cancellationToken))
                    _log("再戦出现但未找到ホームへ。");

                _log("战斗失败，自动主线已停止。");
                return;
            }

            if (TryHit(probes, "scenarioOk", out TemplateProbeResult scenarioOk) &&
                ReadyToClick(lastClick, lastClickAt, "scenarioOk"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "scenarioOk");
                string okReason = phase is Phase.Battle or Phase.Next ? "升级OK" : "确认OK";
                await ClickMatchAsync(window, scenarioOk, okReason, cancellationToken);
                if (phase == Phase.Scenario)
                {
                    completed++;
                    _log($"自动主线：剧情奖励已确认，已通关第 {completed} 轮。");
                    phase = Phase.Start;
                    scenarioSped = false;
                    startAppearAt = null;
                }

                missTimer.Restart();
                continue;
            }

            if (phase == Phase.Scenario)
            {
                // 关卡页「シナリオ再生」必须优先于立绘误匹配；这是剧情播放入口。
                if (TryHit(probes, "scenarioReplay", out TemplateProbeResult againReplay) &&
                    ReadyToClick(lastClick, lastClickAt, "scenarioReplay"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "scenarioReplay");
                    await ClickMatchAsync(window, againReplay, "シナリオ再生", cancellationToken);
                    phase = Phase.Scenario;
                    scenarioSped = false;
                    missTimer.Restart();
                    continue;
                }

                // 未加速：先开右上角菜单再点加速，绝不先点剧情选项（易误匹配拖慢）。
                if (!scenarioSped)
                {
                    if (TryHit(probes, "scenarioMenu", out TemplateProbeResult menu) &&
                        ReadyToClick(lastClick, lastClickAt, "scenarioMenu"))
                    {
                        MarkClick(ref lastClick, ref lastClickAt, "scenarioMenu");
                        await ClickMatchAsync(window, menu, "剧情菜单", cancellationToken);
                        missTimer.Restart();
                        continue;
                    }

                    if (TryHit(probes, "scenarioSpeed", out TemplateProbeResult speed) &&
                        ReadyToClick(lastClick, lastClickAt, "scenarioSpeed"))
                    {
                        MarkClick(ref lastClick, ref lastClickAt, "scenarioSpeed");
                        await ClickMatchAsync(window, speed, "剧情加速", cancellationToken);
                        scenarioSped = true;
                        missTimer.Restart();
                        _log("剧情已开启加速，等待播放结束。");
                        continue;
                    }

                    if (TryHit(probes, "next", out TemplateProbeResult earlyNext) &&
                        ReadyToClick(lastClick, lastClickAt, "next"))
                    {
                        MarkClick(ref lastClick, ref lastClickAt, "next");
                        bool clearedEarly = await ClickNextUntilGoneAsync(window, earlyNext, cancellationToken);
                        if (!clearedEarly)
                        {
                            phase = Phase.Next;
                            missTimer.Restart();
                            continue;
                        }

                        completed++;
                        _log($"自动主线：剧情结束，已通关第 {completed} 轮。");
                        phase = Phase.Start;
                        scenarioSped = false;
                        startAppearAt = null;
                        missTimer.Restart();
                        continue;
                    }

                    int earlyTimeout = MissTimeoutMs;
                    if (missTimer.ElapsedMilliseconds >= earlyTimeout)
                        throw new TimeoutException($"自动主线剧情持续 {earlyTimeout / 1000} 秒未开启加速。");

                    await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                    continue;
                }

                // 已加速后才处理选项 / 立绘。
                if (TryPickBestPinkChoice(probes, out string choiceKey, out TemplateProbeResult choice) &&
                    ReadyToClick(lastClick, lastClickAt, "scenarioChoice"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "scenarioChoice");
                    await ClickScenarioChoiceAsync(window, choiceKey, choice, cancellationToken);
                    missTimer.Restart();
                    continue;
                }

                if (TryHit(probes, "scenarioPortrait", out TemplateProbeResult portrait) &&
                    ReadyToClick(lastClick, lastClickAt, "scenarioChoice"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "scenarioChoice");
                    await ClickScenarioChoiceAsync(window, "scenarioPortrait", portrait, cancellationToken);
                    missTimer.Restart();
                    continue;
                }

                if (TryHit(probes, "next", out TemplateProbeResult scenarioNext) &&
                    ReadyToClick(lastClick, lastClickAt, "next"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "next");
                    bool cleared = await ClickNextUntilGoneAsync(window, scenarioNext, cancellationToken);
                    if (!cleared)
                    {
                        phase = Phase.Next;
                        missTimer.Restart();
                        continue;
                    }

                    completed++;
                    _log($"自动主线：剧情结束，已通关第 {completed} 轮。");
                    phase = Phase.Start;
                    scenarioSped = false;
                    startAppearAt = null;
                    missTimer.Restart();
                    continue;
                }

                if (TryHit(probes, "sortie", out TemplateProbeResult scenarioSortie) &&
                    ReadyToClick(lastClick, lastClickAt, "sortie"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "sortie");
                    await ClickMatchAsync(window, scenarioSortie, "出击", cancellationToken);
                    phase = Phase.Battle;
                    scenarioSped = false;
                    missTimer.Restart();
                    continue;
                }

                if (TryReadyStartClick(probes, ref startAppearAt, out TemplateProbeResult scenarioStart) &&
                    ReadyToClick(lastClick, lastClickAt, "start"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "start");
                    await ClickMatchAsync(window, scenarioStart, "任务开始", cancellationToken);
                    phase = Phase.Sortie;
                    scenarioSped = false;
                    startAppearAt = null;
                    missTimer.Restart();
                    continue;
                }

                int scenarioTimeout = ScenarioMissTimeoutMs;
                if (missTimer.ElapsedMilliseconds >= scenarioTimeout)
                    throw new TimeoutException($"自动主线剧情持续 {scenarioTimeout / 1000} 秒未结束（sped={scenarioSped}）。");

                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            if (phase is Phase.Battle or Phase.Next or Phase.Sortie &&
                (TryHit(probes, "skip", out TemplateProbeResult skip) ||
                 TryHit(probes, "skipAlt", out skip)) &&
                ReadyToClick(lastClick, lastClickAt, "skip"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "skip");
                await ClickMatchAsync(window, skip, "SKIP", cancellationToken);
                phase = Phase.Next;
                missTimer.Restart();
                battleSkipFallback = null;
                continue;
            }

            if (phase is Phase.Battle or Phase.Sortie)
            {
                battleSkipFallback ??= Stopwatch.StartNew();
                double skipScore = probes.TryGetValue("skip", out TemplateProbeResult? sk) ? sk.Score : 0;
                double skipAltScore = probes.TryGetValue("skipAlt", out TemplateProbeResult? ska) ? ska.Score : 0;
                double nextScore = probes.TryGetValue("next", out TemplateProbeResult? nx) ? nx.Score : 0;
                double maxBattle = Math.Max(skipScore, Math.Max(skipAltScore, nextScore));
                // 加载过渡期分数极低；等 12s 且已有 UI 信号，或硬等到 25s 再坐标点 SKIP。
                bool readyFallback =
                    battleSkipFallback.ElapsedMilliseconds >= 25000 ||
                    (battleSkipFallback.ElapsedMilliseconds >= 12000 && maxBattle >= 0.15);
                if (readyFallback && ReadyToClick(lastClick, lastClickAt, "skip"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "skip");
                    _log("SKIP 模板未命中，改用固定坐标点击。");
                    await _screen.ClickAsync(window, _config.BattleSkipClick, "SKIP(坐标)", cancellationToken);
                    phase = Phase.Next;
                    missTimer.Restart();
                    battleSkipFallback.Restart();
                    continue;
                }
            }
            else
            {
                battleSkipFallback = null;
            }

            if ((phase is Phase.Next or Phase.Battle or Phase.Start or Phase.Sortie) &&
                TryHit(probes, "next", out TemplateProbeResult next) &&
                ReadyToClick(lastClick, lastClickAt, "next"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "next");
                bool cleared = await ClickNextUntilGoneAsync(window, next, cancellationToken);
                if (!cleared)
                {
                    phase = Phase.Next;
                    missTimer.Restart();
                    continue;
                }

                completed++;
                _log($"自动主线：已通关第 {completed} 轮，继续下一关。");
                phase = Phase.Start;
                startAppearAt = null;
                missTimer.Restart();
                continue;
            }

            if (TryHit(probes, "sortie", out TemplateProbeResult sortie) &&
                ReadyToClick(lastClick, lastClickAt, "sortie"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "sortie");
                await ClickMatchAsync(window, sortie, "出击", cancellationToken);
                phase = Phase.Battle;
                missTimer.Restart();
                continue;
            }

            if (TryHit(probes, "scenarioReplay", out TemplateProbeResult scenarioReplay) &&
                ReadyToClick(lastClick, lastClickAt, "scenarioReplay"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "scenarioReplay");
                await ClickMatchAsync(window, scenarioReplay, "情景再现", cancellationToken);
                phase = Phase.Scenario;
                scenarioSped = false;
                missTimer.Restart();
                continue;
            }

            if (TryReadyStartClick(probes, ref startAppearAt, out TemplateProbeResult start) &&
                ReadyToClick(lastClick, lastClickAt, "start"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "start");
                await ClickMatchAsync(window, start, "任务开始", cancellationToken);
                phase = Phase.Sortie;
                startAppearAt = null;
                missTimer.Restart();
                continue;
            }

            if (missTimer.ElapsedMilliseconds >= MissTimeoutMs)
                throw new TimeoutException($"自动主线持续 {MissTimeoutMs / 1000} 秒未识别到可操作界面（phase={phase}）。");

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
    }

    /// <summary>
    /// 开局：已在关卡内则直接续跑；否则有主页钮先回主页，再固定连点 任务→主线（不识别任务页）。
    /// </summary>
    private async Task<(Phase Phase, bool Abort)> BootstrapEntryAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var locateJobs = new (string Key, TemplateMatcher Matcher)[]
        {
            ("rematch", _rematch), ("toHome", _toHome), ("next", _next),
            ("start", _start), ("sortie", _sortie), ("scenarioReplay", _scenarioReplay)
        };

        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 3000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            IReadOnlyDictionary<string, TemplateProbeResult> probes =
                await ProbeClientAsync(window, locateJobs, cancellationToken);

            if (TryHit(probes, "rematch", out _))
            {
                _log("开局已在再戦，点击ホームへ后结束。");
                if (TryHit(probes, "toHome", out TemplateProbeResult homeBtn))
                    await ClickMatchAsync(window, homeBtn, "ホームへ", cancellationToken);
                else
                    await WaitForClickAsync(window, "toHome", [("toHome", _toHome)], cancellationToken);
                return (Phase.Start, true);
            }

            if (TryHit(probes, "next", out _))
            {
                _log("开局定位：结算页（下一步）。");
                return (Phase.Next, false);
            }

            if (TryHit(probes, "start", out _))
            {
                _log("开局定位：主线开始页。");
                return (Phase.Start, false);
            }

            if (TryHit(probes, "sortie", out _))
            {
                _log("开局定位：出击页。");
                return (Phase.Sortie, false);
            }

            if (TryHit(probes, "scenarioReplay", out _))
            {
                _log("开局定位：情景再现/剧情入口。");
                return (Phase.Scenario, false);
            }

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }

        _log("开局不在关卡内：回主页后连点进主线。");
        await new QuestFromHomeEntry(_config, _screen, _log).RunAsync(
            window,
            QuestFromHomeEntry.MainQuestBannerClick(_config),
            "主线任务",
            cancellationToken);
        return (Phase.Start, false);
    }

    private async Task<IReadOnlyDictionary<string, TemplateProbeResult>> ProbeClientAsync(
        GameWindow window,
        IReadOnlyList<(string Key, TemplateMatcher Matcher)> jobs,
        CancellationToken cancellationToken)
    {
        var probes = jobs.Select(job =>
        {
            (string key, TemplateMatcher matcher) = job;
            (ConfigPoint topLeft, ConfigSize size) = RoiFor(key);
            double threshold = key switch
            {
                "skip" or "skipAlt" or "next" => 0.52,
                "scenarioReplay" => 0.60,
                // 菜单/加速略放宽，日志里常在 0.40~0.50。
                "scenarioMenu" or "scenarioSpeed" => 0.40,
                "scenarioOk" or "scenarioChoice" or "scenarioChoiceAlt" => 0.55,
                "scenarioPortrait" => 0.88,
                "rematch" => 0.72,
                _ => PresenceThreshold
            };
            return new TemplateProbe(key, matcher, topLeft, size, threshold);
        });
        return await _screen.ProbeManyAsync(window, probes, cancellationToken);
    }

    private (ConfigPoint TopLeft, ConfigSize Size) RoiFor(string key) => key switch
    {
        "start" => (_config.MainQuestStartTopLeft, _config.MainQuestStartSize),
        "sortie" => (_config.MainQuestSortieTopLeft, _config.MainQuestSortieSize),
        "scenarioReplay" => (_config.MainQuestSortieTopLeft, _config.MainQuestSortieSize),
        "scenarioMenu" => (_config.MainQuestScenarioMenuTopLeft, _config.MainQuestScenarioMenuSize),
        "scenarioSpeed" => (_config.MainQuestScenarioSpeedTopLeft, _config.MainQuestScenarioSpeedSize),
        "scenarioOk" => (_config.MainQuestScenarioOkTopLeft, _config.MainQuestScenarioOkSize),
        "scenarioChoice" or "scenarioChoiceAlt" =>
            (_config.MainQuestScenarioChoiceTopLeft, _config.MainQuestScenarioChoiceSize),
        "scenarioPortrait" =>
            (_config.MainQuestScenarioPortraitTopLeft, _config.MainQuestScenarioPortraitSize),
        "skip" or "skipAlt" => (_config.MainQuestSkipTopLeft, _config.MainQuestSkipSize),
        "next" => (_config.MainQuestNextTopLeft, _config.MainQuestNextSize),
        "rematch" => (_config.MainQuestRematchTopLeft, _config.MainQuestRematchSize),
        _ => (_config.MainQuestToHomeTopLeft, _config.MainQuestToHomeSize)
    };

    private async Task<bool> WaitForClickAsync(
        GameWindow window,
        string key,
        IReadOnlyList<(string Key, TemplateMatcher Matcher)> jobs,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 8000)
        {
            IReadOnlyDictionary<string, TemplateProbeResult> probes =
                await ProbeClientAsync(window, jobs, cancellationToken);
            if (TryHit(probes, key, out TemplateProbeResult hit))
            {
                await ClickMatchAsync(window, hit, key, cancellationToken);
                return true;
            }

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }

        return false;
    }

    /// <summary>宣传弹窗只在剧情/战斗结束后挡「次へ」等，主页与进关前不扫。</summary>
    private static bool ShouldCheckPromoPopup(Phase phase) =>
        phase is Phase.Battle or Phase.Next or Phase.Scenario;

    private static bool TryHit(
        IReadOnlyDictionary<string, TemplateProbeResult> probes, string key, out TemplateProbeResult probe)
    {
        return TemplateProbes.TryGetHit(probes, key, out probe);
    }

    private static bool ReadyToClick(string? lastClick, Stopwatch lastClickAt, string key) =>
        lastClick != key || lastClickAt.ElapsedMilliseconds >= ClickCooldownMs;

    private static void MarkClick(ref string? lastClick, ref Stopwatch lastClickAt, string key)
    {
        lastClick = key;
        lastClickAt.Restart();
    }

    private bool TryReadyStartClick(
        IReadOnlyDictionary<string, TemplateProbeResult> probes,
        ref Stopwatch? startAppearAt,
        out TemplateProbeResult start)
    {
        if (!TryHit(probes, "start", out start))
        {
            startAppearAt = null;
            return false;
        }

        startAppearAt ??= Stopwatch.StartNew();
        if (startAppearAt.ElapsedMilliseconds < StartAppearSettleMs)
            return false;

        return true;
    }

    private async Task ClickMatchAsync(
        GameWindow window, TemplateProbeResult probe, string reason, CancellationToken cancellationToken)
    {
        await _screen.ClickProbeAsync(window, probe, reason, cancellationToken);
    }

    /// <summary>
    /// 点一次下一步；若仍在再点一次（升级弹层可能吃掉第一次）。
    /// 返回 true 表示下一步已消失，可进入下一关；false 表示仍在，保持 Phase.Next 继续点。
    /// </summary>
    private async Task<bool> ClickNextUntilGoneAsync(
        GameWindow window, TemplateProbeResult next, CancellationToken cancellationToken)
    {
        await ClickMatchAsync(window, next, "下一步", cancellationToken);
        await Task.Delay(1000, cancellationToken);
        window = _screen.Refresh(window);
        IReadOnlyDictionary<string, TemplateProbeResult> probes =
            await ProbeClientAsync(window, [("next", _next)], cancellationToken);
        if (!TryHit(probes, "next", out TemplateProbeResult stillNext))
            return true;

        _log("下一步仍在，再点一次。");
        await ClickMatchAsync(window, stillNext, "下一步(2)", cancellationToken);
        await Task.Delay(1000, cancellationToken);
        window = _screen.Refresh(window);
        probes = await ProbeClientAsync(window, [("next", _next)], cancellationToken);
        if (!TryHit(probes, "next", out _))
            return true;

        _log("结算下一步仍在，稍后继续点。");
        return false;
    }

    private static bool TryPickBestPinkChoice(
        IReadOnlyDictionary<string, TemplateProbeResult> probes,
        out string key,
        out TemplateProbeResult probe)
    {
        key = "";
        probe = null!;
        double best = 0;
        foreach (string candidate in (string[])["scenarioChoice", "scenarioChoiceAlt"])
        {
            if (!TryHit(probes, candidate, out TemplateProbeResult hit))
                continue;
            if (hit.Score <= best)
                continue;
            best = hit.Score;
            key = candidate;
            probe = hit;
        }

        return best > 0;
    }

    private async Task ClickScenarioChoiceAsync(
        GameWindow window,
        string choiceKey,
        TemplateProbeResult probe,
        CancellationToken cancellationToken)
    {
        if (choiceKey == "scenarioPortrait")
        {
            // 角点匹配位置不稳定，立绘选项固定点左侧头像中心。
            _log($"剧情立绘选项命中 {probe.Score:F3}，固定点击左侧 " +
                 $"({_config.MainQuestScenarioPortraitClick.X},{_config.MainQuestScenarioPortraitClick.Y})");
            await _screen.ClickAsync(
                window, _config.MainQuestScenarioPortraitClick, "剧情立绘选项", cancellationToken);
            return;
        }

        CaptureGeometry geometry = _screen.Geometry(window);
        ConfigPoint offset = _config.MainQuestScenarioChoiceClickOffset;
        var point = new Point(
            probe.Center.X + offset.X * geometry.ScaleX,
            probe.Center.Y + offset.Y * geometry.ScaleY);
        _log($"剧情选项命中 {probe.Score:F3}，偏移点击 ({point.X:F0},{point.Y:F0})");
        await _screen.ClickScreenAsync(window, point, cancellationToken);
        await Task.Delay(100, cancellationToken);
    }
}
