using System.Diagnostics;
using System.Windows;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 自动主线：
/// 关卡页双击「开始/情景回放」同位置 → 右上角箭头=剧情（菜单→加速；箭头卡住 15s 再点选项）
/// 否则 SKIP 战斗 → 下一步（偶发关宣传 X）。再戦则回主页结束。
/// </summary>
public sealed class MainQuestAutomation
{
    private const double PresenceThreshold = 0.62;
    private const int MissTimeoutMs = 120000;
    private const int ScenarioMissTimeoutMs = 900000;
    private const int ClickCooldownMs = 100;
    private const int StartAppearSettleMs = 100;
    private const int AfterBeginDelayMs = 800;
    /// <summary>加速后箭头仍在超过此时长，视为选项卡住。</summary>
    private const int ScenarioChoiceStuckMs = 15000;

    private enum Phase { Start, AwaitBranch, Battle, Scenario, Next }

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _start;
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

    /// <summary>开始 / 情景回放同一写死点（ROI 中心）。</summary>
    private ConfigPoint BeginStageClick => new(
        _config.MainQuestStartTopLeft.X + _config.MainQuestStartSize.Width / 2,
        _config.MainQuestStartTopLeft.Y + _config.MainQuestStartSize.Height / 2);

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

        _log("自动主线：双击开始/情景同点；箭头=剧情加速，否则 SKIP→下一步；再戦结束。");
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
        Stopwatch? scenarioArrowSince = null;
        bool scenarioSped = false;
        bool scenarioMenuOpened = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            // SKIP 后 / 结算：偶发宣传弹窗 X。
            if (ShouldCheckPromoPopup(phase) &&
                await _promoPopup.TryAsync(window, cancellationToken))
            {
                missTimer.Restart();
                await Task.Delay(250, cancellationToken);
                continue;
            }

            IReadOnlyList<(string Key, TemplateMatcher Matcher)> active = phase switch
            {
                Phase.Start =>
                [
                    ("start", _start), ("next", _next), ("rematch", _rematch), ("toHome", _toHome)
                ],
                Phase.AwaitBranch =>
                [
                    ("scenarioMenu", _scenarioMenu),
                    ("skip", _skip), ("skipAlt", _skipAlt),
                    ("next", _next), ("rematch", _rematch), ("toHome", _toHome), ("start", _start)
                ],
                Phase.Battle =>
                [
                    ("scenarioOk", _scenarioOk), ("skip", _skip), ("skipAlt", _skipAlt),
                    ("next", _next), ("rematch", _rematch), ("toHome", _toHome)
                ],
                Phase.Scenario => scenarioSped
                    ?
                    [
                        ("scenarioMenu", _scenarioMenu),
                        ("scenarioOk", _scenarioOk),
                        ("scenarioChoice", _scenarioChoice),
                        ("scenarioChoiceAlt", _scenarioChoiceAlt),
                        ("scenarioPortrait", _scenarioPortrait),
                        ("next", _next),
                        ("start", _start),
                        ("rematch", _rematch),
                        ("toHome", _toHome)
                    ]
                    :
                    [
                        ("scenarioMenu", _scenarioMenu),
                        ("scenarioSpeed", _scenarioSpeed),
                        ("scenarioOk", _scenarioOk),
                        ("next", _next),
                        ("rematch", _rematch),
                        ("toHome", _toHome)
                    ],
                _ =>
                [
                    ("scenarioOk", _scenarioOk), ("next", _next), ("start", _start),
                    ("rematch", _rematch), ("toHome", _toHome)
                ]
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
                    ResetScenario(ref scenarioSped, ref scenarioMenuOpened, ref scenarioArrowSince, ref startAppearAt);
                }

                missTimer.Restart();
                continue;
            }

            // —— 关卡页：识别到开始钮则双击同点，再等分支 ——
            if (phase == Phase.Start)
            {
                if (TryReadyStartClick(probes, ref startAppearAt, out _) &&
                    ReadyToClick(lastClick, lastClickAt, "begin"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "begin");
                    window = await DoubleClickBeginAsync(window, cancellationToken);
                    phase = Phase.AwaitBranch;
                    startAppearAt = null;
                    missTimer.Restart();
                    continue;
                }

                if (TryHit(probes, "next", out TemplateProbeResult startNext) &&
                    ReadyToClick(lastClick, lastClickAt, "next"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "next");
                    bool cleared = await ClickNextUntilGoneAsync(window, startNext, cancellationToken);
                    phase = cleared ? Phase.Start : Phase.Next;
                    if (cleared)
                    {
                        completed++;
                        _log($"自动主线：已通关第 {completed} 轮，继续下一关。");
                    }

                    missTimer.Restart();
                    continue;
                }
            }

            // —— 已双击：右上角箭头=剧情，否则 SKIP=战斗 ——
            if (phase == Phase.AwaitBranch)
            {
                if (TryHit(probes, "scenarioMenu", out TemplateProbeResult branchMenu) &&
                    ReadyToClick(lastClick, lastClickAt, "scenarioMenu"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "scenarioMenu");
                    await ClickMatchAsync(window, branchMenu, "剧情菜单(箭头)", cancellationToken);
                    phase = Phase.Scenario;
                    scenarioSped = false;
                    scenarioMenuOpened = true;
                    scenarioArrowSince = Stopwatch.StartNew();
                    missTimer.Restart();
                    _log("检测到右上角箭头，进入剧情加速流程。");
                    continue;
                }

                if ((TryHit(probes, "skip", out TemplateProbeResult branchSkip) ||
                     TryHit(probes, "skipAlt", out branchSkip)) &&
                    ReadyToClick(lastClick, lastClickAt, "skip"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "skip");
                    await ClickMatchAsync(window, branchSkip, "SKIP", cancellationToken);
                    phase = Phase.Next;
                    missTimer.Restart();
                    battleSkipFallback = null;
                    _log("检测到 SKIP，战斗结束，等待下一步。");
                    continue;
                }

                if (TryHit(probes, "next", out TemplateProbeResult branchNext) &&
                    ReadyToClick(lastClick, lastClickAt, "next"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "next");
                    bool cleared = await ClickNextUntilGoneAsync(window, branchNext, cancellationToken);
                    phase = cleared ? Phase.Start : Phase.Next;
                    if (cleared)
                    {
                        completed++;
                        _log($"自动主线：已通关第 {completed} 轮，继续下一关。");
                    }

                    missTimer.Restart();
                    continue;
                }

                // 回到关卡页则重新双击。
                if (TryReadyStartClick(probes, ref startAppearAt, out _) &&
                    ReadyToClick(lastClick, lastClickAt, "begin"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "begin");
                    window = await DoubleClickBeginAsync(window, cancellationToken);
                    startAppearAt = null;
                    missTimer.Restart();
                    continue;
                }

                battleSkipFallback ??= Stopwatch.StartNew();
                if (battleSkipFallback.ElapsedMilliseconds >= 12000 &&
                    ReadyToClick(lastClick, lastClickAt, "skip"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "skip");
                    _log("分支等待超时，尝试坐标点 SKIP。");
                    await _screen.ClickAsync(window, _config.BattleSkipClick, "SKIP(坐标)", cancellationToken);
                    phase = Phase.Next;
                    missTimer.Restart();
                    battleSkipFallback = null;
                    continue;
                }
            }

            // —— 剧情 ——
            if (phase == Phase.Scenario)
            {
                bool arrowVisible = TryHit(probes, "scenarioMenu", out TemplateProbeResult arrow);
                if (arrowVisible)
                    scenarioArrowSince ??= Stopwatch.StartNew();
                else
                    scenarioArrowSince = null;

                if (!scenarioSped)
                {
                    if (!scenarioMenuOpened &&
                        arrowVisible &&
                        ReadyToClick(lastClick, lastClickAt, "scenarioMenu"))
                    {
                        MarkClick(ref lastClick, ref lastClickAt, "scenarioMenu");
                        await ClickMatchAsync(window, arrow, "剧情菜单", cancellationToken);
                        scenarioMenuOpened = true;
                        missTimer.Restart();
                        continue;
                    }

                    if (TryHit(probes, "scenarioSpeed", out TemplateProbeResult speed) &&
                        ReadyToClick(lastClick, lastClickAt, "scenarioSpeed"))
                    {
                        MarkClick(ref lastClick, ref lastClickAt, "scenarioSpeed");
                        await ClickMatchAsync(window, speed, "剧情加速", cancellationToken);
                        scenarioSped = true;
                        scenarioArrowSince = Stopwatch.StartNew();
                        missTimer.Restart();
                        _log("剧情已开启加速，等待播放；箭头卡住 15s 再点选项。");
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
                        ResetScenario(ref scenarioSped, ref scenarioMenuOpened, ref scenarioArrowSince, ref startAppearAt);
                        missTimer.Restart();
                        continue;
                    }

                    if (missTimer.ElapsedMilliseconds >= MissTimeoutMs)
                        throw new TimeoutException($"自动主线剧情持续 {MissTimeoutMs / 1000} 秒未开启加速。");

                    await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                    continue;
                }

                // 已加速：下一步优先；选项仅当箭头仍在超 15s。
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
                    ResetScenario(ref scenarioSped, ref scenarioMenuOpened, ref scenarioArrowSince, ref startAppearAt);
                    missTimer.Restart();
                    continue;
                }

                bool choiceStuck = scenarioArrowSince is not null &&
                    scenarioArrowSince.ElapsedMilliseconds >= ScenarioChoiceStuckMs &&
                    arrowVisible;
                if (choiceStuck &&
                    scenarioArrowSince is not null &&
                    ReadyToClick(lastClick, lastClickAt, "scenarioChoice"))
                {
                    Stopwatch stuckClock = scenarioArrowSince;
                    if (TryPickBestPinkChoice(probes, out string choiceKey, out TemplateProbeResult choice))
                    {
                        MarkClick(ref lastClick, ref lastClickAt, "scenarioChoice");
                        _log($"剧情箭头已卡住 {stuckClock.Elapsed.TotalSeconds:F0}s，点击选项。");
                        await ClickScenarioChoiceAsync(window, choiceKey, choice, cancellationToken);
                        stuckClock.Restart();
                        missTimer.Restart();
                        continue;
                    }

                    if (TryHit(probes, "scenarioPortrait", out TemplateProbeResult portrait))
                    {
                        MarkClick(ref lastClick, ref lastClickAt, "scenarioChoice");
                        _log($"剧情箭头已卡住 {stuckClock.Elapsed.TotalSeconds:F0}s，点击立绘选项。");
                        await ClickScenarioChoiceAsync(window, "scenarioPortrait", portrait, cancellationToken);
                        stuckClock.Restart();
                        missTimer.Restart();
                        continue;
                    }
                }

                if (TryReadyStartClick(probes, ref startAppearAt, out _) &&
                    ReadyToClick(lastClick, lastClickAt, "begin"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "begin");
                    window = await DoubleClickBeginAsync(window, cancellationToken);
                    phase = Phase.AwaitBranch;
                    ResetScenario(ref scenarioSped, ref scenarioMenuOpened, ref scenarioArrowSince, ref startAppearAt);
                    missTimer.Restart();
                    continue;
                }

                if (missTimer.ElapsedMilliseconds >= ScenarioMissTimeoutMs)
                    throw new TimeoutException($"自动主线剧情持续 {ScenarioMissTimeoutMs / 1000} 秒未结束。");

                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            // —— 战斗中（若仍停在 Battle）——
            if (phase == Phase.Battle)
            {
                if ((TryHit(probes, "skip", out TemplateProbeResult skip) ||
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

                battleSkipFallback ??= Stopwatch.StartNew();
                if (battleSkipFallback.ElapsedMilliseconds >= 12000 &&
                    ReadyToClick(lastClick, lastClickAt, "skip"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "skip");
                    await _screen.ClickAsync(window, _config.BattleSkipClick, "SKIP(坐标)", cancellationToken);
                    phase = Phase.Next;
                    missTimer.Restart();
                    battleSkipFallback = null;
                    continue;
                }
            }
            else if (phase is not Phase.AwaitBranch)
            {
                battleSkipFallback = null;
            }

            // —— 下一步（战斗后大概率）——
            if ((phase is Phase.Next or Phase.Battle) &&
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

            if (missTimer.ElapsedMilliseconds >= MissTimeoutMs)
                throw new TimeoutException($"自动主线持续 {MissTimeoutMs / 1000} 秒未识别到可操作界面（phase={phase}）。");

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
    }

    private async Task<(Phase Phase, bool Abort)> BootstrapEntryAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var locateJobs = new (string Key, TemplateMatcher Matcher)[]
        {
            ("rematch", _rematch), ("toHome", _toHome), ("next", _next),
            ("scenarioMenu", _scenarioMenu), ("skip", _skip), ("start", _start)
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

            if (TryHit(probes, "scenarioMenu", out _))
            {
                _log("开局定位：剧情中（右上角箭头）。");
                return (Phase.Scenario, false);
            }

            if (TryHit(probes, "skip", out _))
            {
                _log("开局定位：战斗中（SKIP）。");
                return (Phase.Battle, false);
            }

            if (TryHit(probes, "start", out _))
            {
                _log("开局定位：主线关卡页。");
                return (Phase.Start, false);
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

    private async Task<GameWindow> DoubleClickBeginAsync(GameWindow window, CancellationToken cancellationToken)
    {
        int gapMs = Math.Max(_config.DoubleClickIntervalMs, 50);
        _log("双击开始/情景回放（同位置）。");
        window = await _screen.ClickAsync(window, BeginStageClick, "开始/情景 1/2", cancellationToken);
        await Task.Delay(gapMs, cancellationToken);
        window = await _screen.ClickAsync(window, BeginStageClick, "开始/情景 2/2", cancellationToken);
        await Task.Delay(AfterBeginDelayMs, cancellationToken);
        return _screen.Refresh(window);
    }

    private static void ResetScenario(
        ref bool scenarioSped,
        ref bool scenarioMenuOpened,
        ref Stopwatch? scenarioArrowSince,
        ref Stopwatch? startAppearAt)
    {
        scenarioSped = false;
        scenarioMenuOpened = false;
        scenarioArrowSince = null;
        startAppearAt = null;
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
                "scenarioMenu" => 0.45,
                "scenarioSpeed" => 0.45,
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

    /// <summary>SKIP 后下一步、结算阶段偶发宣传 X。</summary>
    private static bool ShouldCheckPromoPopup(Phase phase) =>
        phase is Phase.Battle or Phase.Next;

    private static bool TryHit(
        IReadOnlyDictionary<string, TemplateProbeResult> probes, string key, out TemplateProbeResult probe) =>
        TemplateProbes.TryGetHit(probes, key, out probe);

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
        return startAppearAt.ElapsedMilliseconds >= StartAppearSettleMs;
    }

    private async Task ClickMatchAsync(
        GameWindow window, TemplateProbeResult probe, string reason, CancellationToken cancellationToken) =>
        await _screen.ClickProbeAsync(window, probe, reason, cancellationToken);

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
