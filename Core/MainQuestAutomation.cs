using System.Diagnostics;
using System.Windows;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 自动主线：
/// 关卡页双击「クエスト開始」→ 准备页点「出撃」→ 右上角箭头=剧情（菜单→加速；箭头卡住 15s 再点选项）
/// 否则 SKIP 战斗 → 下一步（偶发关宣传 X）。再戦则回主页结束。
/// </summary>
public sealed class MainQuestAutomation
{
    private const double PresenceThreshold = 0.62;
    private const int MissTimeoutMs = 120000;
    private const int ScenarioMissTimeoutMs = 900000;
    private const int ClickCooldownMs = 100;
    /// <summary>回关卡列表后有切关动画，开始钮虽已画出但尚不可点；过短会空点后卡在出击页。</summary>
    private const int StartAppearSettleMs = 2200;
    private const int AfterBeginDelayMs = 800;
    /// <summary>出击页若迟迟无「出撃」（动画空点/纯剧情直进），超时回关卡页重试。</summary>
    private const int SortieMissFallbackMs = 4000;
    /// <summary>点击剧情回放后仍停在关卡页，视为切关动画期间空点并回到开始阶段重试。</summary>
    private const int ScenarioLaunchStuckMs = 4000;
    /// <summary>加速后箭头仍在超过此时长，视为选项卡住。</summary>
    private const int ScenarioChoiceStuckMs = 15000;

    private enum Phase { Start, Sortie, AwaitBranch, Battle, Scenario, Reward, Next }

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _start;
    private readonly TemplateMatcher _scenarioReplay;
    private readonly TemplateMatcher _scenarioReplay1080;
    private readonly TemplateMatcher _sortie;
    private readonly TemplateMatcher _scenarioMenu;
    private readonly TemplateMatcher _scenarioMenuBack;
    private readonly TemplateMatcher _scenarioSpeed;
    private readonly TemplateMatcher _scenarioVoiceNone;
    private readonly TemplateMatcher _scenarioOk;
    private readonly TemplateMatcher _scenarioChoice;
    private readonly TemplateMatcher _scenarioChoiceAlt;
    private readonly TemplateMatcher _scenarioPortrait;
    private readonly TemplateMatcher _skip;
    private readonly TemplateMatcher _skipAlt;
    private readonly TemplateMatcher _next;
    private readonly TemplateMatcher _rematch;
    private readonly TemplateMatcher _toHome;
    private readonly TemplateMatcher _banner;
    private readonly PromoPopupDismisser _promoPopup;

    public MainQuestAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _start = TemplateAssets.Load("main-quest-start.png");
        _scenarioReplay = TemplateAssets.Load("main-quest-scenario-replay.png");
        _scenarioReplay1080 = TemplateAssets.Load("main-quest-scenario-replay-1080.png");
        _sortie = TemplateAssets.Load("main-quest-sortie.png");
        _scenarioMenu = TemplateAssets.Load("main-quest-scenario-menu.png");
        _scenarioMenuBack = TemplateAssets.Load("main-quest-scenario-menu-back.png");
        _scenarioSpeed = TemplateAssets.Load("main-quest-scenario-speed.png");
        _scenarioVoiceNone = TemplateAssets.Load("main-quest-scenario-voice-none.png");
        _scenarioOk = TemplateAssets.Load("main-quest-scenario-ok.png");
        _scenarioChoice = TemplateAssets.Load("main-quest-scenario-choice.png");
        _scenarioChoiceAlt = TemplateAssets.Load("main-quest-scenario-choice-alt.png");
        _scenarioPortrait = TemplateAssets.Load("main-quest-scenario-choice-portrait.png");
        _skip = TemplateAssets.Load("main-quest-skip.png");
        _skipAlt = TemplateAssets.Load("battle-skip.png");
        _next = TemplateAssets.Load("main-quest-next.png");
        _rematch = TemplateAssets.Load("main-quest-rematch.png");
        _toHome = TemplateAssets.Load("main-quest-to-home.png");
        _banner = TemplateAssets.Load("main-quest-banner.png");
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
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"自动主线：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}，显示器 {window.DisplayRect.Width}×{window.DisplayRect.Height}");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        _log("自动主线：开始→出撃；箭头=剧情加速，否则 SKIP→下一步；再戦结束。");
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
                    ("scenarioReplay1080", _scenarioReplay1080), ("scenarioReplay", _scenarioReplay),
                    ("start", _start), ("sortie", _sortie),
                    ("next", _next), ("rematch", _rematch), ("toHome", _toHome)
                ],
                Phase.Sortie =>
                [
                    ("sortie", _sortie), ("start", _start),
                    ("scenarioMenu", _scenarioMenu), ("scenarioMenuBack", _scenarioMenuBack),
                    ("skip", _skip), ("skipAlt", _skipAlt),
                    ("next", _next), ("rematch", _rematch), ("toHome", _toHome)
                ],
                Phase.AwaitBranch =>
                [
                    ("skipAlt", _skipAlt), ("skip", _skip),
                    ("scenarioMenu", _scenarioMenu), ("scenarioMenuBack", _scenarioMenuBack),
                    ("sortie", _sortie),
                    ("next", _next), ("rematch", _rematch), ("toHome", _toHome), ("start", _start)
                ],
                Phase.Battle =>
                [
                    ("skipAlt", _skipAlt), ("skip", _skip),
                    ("scenarioOk", _scenarioOk),
                    ("next", _next), ("rematch", _rematch), ("toHome", _toHome)
                ],
                Phase.Scenario => scenarioSped
                    ?
                    [
                        ("scenarioMenu", _scenarioMenu), ("scenarioMenuBack", _scenarioMenuBack),
                        ("scenarioVoiceNone", _scenarioVoiceNone),
                        ("scenarioOk", _scenarioOk),
                        ("scenarioChoice", _scenarioChoice),
                        ("scenarioChoiceAlt", _scenarioChoiceAlt),
                        ("scenarioPortrait", _scenarioPortrait),
                        ("next", _next),
                        ("start", _start),
                        ("scenarioReplay1080", _scenarioReplay1080), ("scenarioReplay", _scenarioReplay),
                        ("rematch", _rematch),
                        ("toHome", _toHome)
                    ]
                    : scenarioMenuOpened
                    ?
                    [
                        // 箭头已展开：只找加速，避免未展开时加速模板误点关卡页。
                        ("scenarioMenu", _scenarioMenu), ("scenarioMenuBack", _scenarioMenuBack),
                        ("scenarioSpeed", _scenarioSpeed),
                        ("scenarioVoiceNone", _scenarioVoiceNone),
                        ("scenarioOk", _scenarioOk),
                        ("scenarioReplay1080", _scenarioReplay1080), ("scenarioReplay", _scenarioReplay),
                        ("next", _next),
                        ("rematch", _rematch),
                        ("toHome", _toHome)
                    ]
                    :
                    [
                        ("scenarioMenu", _scenarioMenu), ("scenarioMenuBack", _scenarioMenuBack),
                        ("scenarioVoiceNone", _scenarioVoiceNone),
                        ("scenarioOk", _scenarioOk),
                        ("scenarioReplay1080", _scenarioReplay1080), ("scenarioReplay", _scenarioReplay),
                        ("next", _next),
                        ("rematch", _rematch),
                        ("toHome", _toHome)
                    ],
                Phase.Reward =>
                [
                    // 剧情结束后可能连续出现多个奖励确认；同时观察是否已真正回到关卡页。
                    ("scenarioOk", _scenarioOk),
                    ("scenarioReplay1080", _scenarioReplay1080), ("scenarioReplay", _scenarioReplay),
                    ("start", _start), ("sortie", _sortie),
                    ("next", _next), ("rematch", _rematch), ("toHome", _toHome)
                ],
                _ =>
                [
                    ("scenarioOk", _scenarioOk), ("next", _next), ("start", _start), ("sortie", _sortie),
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

            if (phase == Phase.Reward &&
                (TryHit(probes, "scenarioReplay1080", out _) ||
                 TryHit(probes, "scenarioReplay", out _) ||
                 TryHit(probes, "start", out _) ||
                 TryHit(probes, "sortie", out _)))
            {
                completed++;
                phase = Phase.Start;
                // 刚返回关卡页仍可能处于切关动画；从此刻开始等待按钮稳定。
                startAppearAt = Stopwatch.StartNew();
                missTimer.Restart();
                _log($"自动主线：奖励结算完成，已通关第 {completed} 轮。等待下一关按钮稳定。");
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            // 点击回放后若画面数秒仍是关卡页，说明点击撞上了切关动画。
            // 不在正常路径立即重试，只有确认“卡住”后才退回 Start，重新等待按钮稳定。
            if (phase == Phase.Scenario &&
                missTimer.ElapsedMilliseconds >= ScenarioLaunchStuckMs &&
                (TryHit(probes, "scenarioReplay1080", out _) ||
                 TryHit(probes, "scenarioReplay", out _)))
            {
                phase = Phase.Start;
                ResetScenario(ref scenarioSped, ref scenarioMenuOpened, ref scenarioArrowSince, ref startAppearAt);
                startAppearAt = Stopwatch.StartNew();
                missTimer.Restart();
                _log($"剧情回放点击后 {ScenarioLaunchStuckMs / 1000}s 仍在关卡页，进入卡住恢复并等待重试。");
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            // 初次播放某些剧情时会询问是否下载语音。这个白色按钮曾与奖励 OK
            // 产生约 0.56 的弱误匹配，导致流程误记通关并返回关卡页。
            if (TryHit(probes, "scenarioVoiceNone", out TemplateProbeResult voiceNone) &&
                ReadyToClick(lastClick, lastClickAt, "scenarioVoiceNone"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "scenarioVoiceNone");
                await ClickMatchAsync(window, voiceNone, "ボイスなし", cancellationToken);
                missTimer.Restart();
                await Task.Delay(700, cancellationToken);
                continue;
            }

            if (TryHit(probes, "scenarioOk", out TemplateProbeResult scenarioOk) &&
                ReadyToClick(lastClick, lastClickAt, "scenarioOk"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "scenarioOk");
                string okReason = phase is Phase.Battle or Phase.Next ? "升级OK" : "确认OK";
                await ClickMatchAsync(window, scenarioOk, okReason, cancellationToken);
                if (phase == Phase.Scenario)
                {
                    phase = Phase.Reward;
                    _log("自动主线：剧情结束，进入奖励结算；如有连续奖励将逐个确认。");
                    ResetScenario(ref scenarioSped, ref scenarioMenuOpened, ref scenarioArrowSince, ref startAppearAt);
                }

                missTimer.Restart();
                continue;
            }

            // —— 关卡页：识别到开始钮则双击同点，再等出击页 ——
            if (phase == Phase.Start)
            {
                bool hasReplay4K = TryHit(probes, "scenarioReplay", out TemplateProbeResult replay4K);
                bool hasReplay1080 = TryHit(probes, "scenarioReplay1080", out TemplateProbeResult replay1080);
                TemplateProbeResult replay = hasReplay1080 && (!hasReplay4K || replay1080.Score >= replay4K.Score)
                    ? replay1080
                    : replay4K;
                if (hasReplay4K || hasReplay1080)
                {
                    startAppearAt ??= Stopwatch.StartNew();
                    if (startAppearAt.ElapsedMilliseconds >= StartAppearSettleMs &&
                        ReadyToClick(lastClick, lastClickAt, "scenarioReplay"))
                    {
                        MarkClick(ref lastClick, ref lastClickAt, "scenarioReplay");
                        await ClickMatchAsync(window, replay, "情景回放", cancellationToken);
                        phase = Phase.Scenario;
                        ResetScenario(ref scenarioSped, ref scenarioMenuOpened, ref scenarioArrowSince, ref startAppearAt);
                        missTimer.Restart();
                        await Task.Delay(700, cancellationToken);
                    }
                    else
                    {
                        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                    }

                    continue;
                }

                if (TryHit(probes, "sortie", out TemplateProbeResult startSortie) &&
                    ReadyToClick(lastClick, lastClickAt, "sortie"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "sortie");
                    await ClickMatchAsync(window, startSortie, "出击", cancellationToken);
                    phase = Phase.AwaitBranch;
                    battleSkipFallback = null;
                    missTimer.Restart();
                    continue;
                }

                if (TryReadyStartClick(probes, ref startAppearAt, out TemplateProbeResult startBtn) &&
                    ReadyToClick(lastClick, lastClickAt, "begin"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "begin");
                    window = await DoubleClickBeginAsync(window, startBtn, cancellationToken);
                    phase = Phase.Sortie;
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

            // —— 准备页：点「出撃」后再等剧情/战斗分支 ——
            if (phase == Phase.Sortie)
            {
                if (TryHit(probes, "sortie", out TemplateProbeResult sortieBtn) &&
                    ReadyToClick(lastClick, lastClickAt, "sortie"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "sortie");
                    await ClickMatchAsync(window, sortieBtn, "出击", cancellationToken);
                    phase = Phase.AwaitBranch;
                    battleSkipFallback = null;
                    missTimer.Restart();
                    _log("已出击，优先识别战斗 SKIP（同迷宫），否则再看剧情箭头。");
                    continue;
                }

                if (TryPickScenarioMenu(probes, out TemplateProbeResult sortieMenu) &&
                    ReadyToClick(lastClick, lastClickAt, "scenarioMenu"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "scenarioMenu");
                    await ClickMatchAsync(window, sortieMenu, "剧情菜单(箭头展开)", cancellationToken);
                    phase = Phase.Scenario;
                    scenarioSped = false;
                    scenarioMenuOpened = true;
                    scenarioArrowSince = Stopwatch.StartNew();
                    missTimer.Restart();
                    _log("未出出击页但已见剧情箭头（纯剧情关），先展开菜单再加速。");
                    await Task.Delay(450, cancellationToken);
                    continue;
                }

                if ((TryHit(probes, "skip", out TemplateProbeResult sortieSkip) ||
                     TryHit(probes, "skipAlt", out sortieSkip)) &&
                    ReadyToClick(lastClick, lastClickAt, "skip"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "skip");
                    await ClickMazeStyleSkipAsync(window, sortieSkip, cancellationToken);
                    phase = Phase.Next;
                    missTimer.Restart();
                    battleSkipFallback = null;
                    continue;
                }

                // 仍停在关卡页则再点开始。
                if (TryReadyStartClick(probes, ref startAppearAt, out TemplateProbeResult sortieRestart) &&
                    ReadyToClick(lastClick, lastClickAt, "begin"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "begin");
                    window = await DoubleClickBeginAsync(window, sortieRestart, cancellationToken);
                    startAppearAt = null;
                    missTimer.Restart();
                    continue;
                }

                // 切关动画期空点后会停在本页且 start/sortie 都不够阈值；回关卡页重新等稳定。
                if (missTimer.ElapsedMilliseconds >= SortieMissFallbackMs)
                {
                    _log($"出击页 {SortieMissFallbackMs / 1000}s 未见出撃/剧情/SKIP，退回关卡页等待（可能仍在切关动画）。");
                    phase = Phase.Start;
                    startAppearAt = null;
                    missTimer.Restart();
                    continue;
                }

                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            // —— 已出击：先看战斗 SKIP（同迷宫），再看剧情箭头 ——
            if (phase == Phase.AwaitBranch)
            {
                if (TryHit(probes, "sortie", out TemplateProbeResult branchSortie) &&
                    ReadyToClick(lastClick, lastClickAt, "sortie"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "sortie");
                    await ClickMatchAsync(window, branchSortie, "出击", cancellationToken);
                    phase = Phase.Battle;
                    battleSkipFallback = null;
                    missTimer.Restart();
                    continue;
                }

                if (TryPickBattleSkip(probes, out TemplateProbeResult branchSkip) &&
                    ReadyToClick(lastClick, lastClickAt, "skip"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "skip");
                    await ClickMazeStyleSkipAsync(window, branchSkip, cancellationToken);
                    phase = Phase.Next;
                    missTimer.Restart();
                    battleSkipFallback = null;
                    _log("检测到 SKIP，战斗结束，等待下一步。");
                    continue;
                }

                if (TryPickScenarioMenu(probes, out TemplateProbeResult branchMenu) &&
                    branchMenu.Score >= 0.62 &&
                    ReadyToClick(lastClick, lastClickAt, "scenarioMenu"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "scenarioMenu");
                    await ClickMatchAsync(window, branchMenu, "剧情菜单(箭头展开)", cancellationToken);
                    phase = Phase.Scenario;
                    scenarioSped = false;
                    scenarioMenuOpened = true;
                    scenarioArrowSince = Stopwatch.StartNew();
                    missTimer.Restart();
                    _log("检测到右上角箭头，先展开菜单再点加速。");
                    await Task.Delay(450, cancellationToken);
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

                // 回到关卡页则重新双击进出击页。
                if (TryReadyStartClick(probes, ref startAppearAt, out TemplateProbeResult restartBtn) &&
                    ReadyToClick(lastClick, lastClickAt, "begin"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "begin");
                    window = await DoubleClickBeginAsync(window, restartBtn, cancellationToken);
                    phase = Phase.Sortie;
                    startAppearAt = null;
                    missTimer.Restart();
                    continue;
                }

                battleSkipFallback ??= Stopwatch.StartNew();
                double skipAltScore = probes.TryGetValue("skipAlt", out TemplateProbeResult? ska) ? ska.Score : 0;
                // 同迷宫：未满阈值但分偏高则写死点 SKIP。
                if (skipAltScore >= 0.40 &&
                    battleSkipFallback.ElapsedMilliseconds >= 1500 &&
                    ReadyToClick(lastClick, lastClickAt, "skip"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "skip");
                    _log($"战斗 SKIP 分偏高（{skipAltScore:F2}），点 SKIP 写死点兜底。");
                    await _screen.ClickAsync(window, _config.BattleSkipClick, "SKIP(未命中兜底)", cancellationToken);
                    phase = Phase.Next;
                    missTimer.Restart();
                    battleSkipFallback = null;
                    continue;
                }

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
                bool arrowVisible = TryPickScenarioMenu(probes, out TemplateProbeResult arrow);
                if (arrowVisible)
                    scenarioArrowSince ??= Stopwatch.StartNew();
                else
                    scenarioArrowSince = null;

                if (!scenarioSped)
                {
                    // 必须先点右上角箭头展开，再点加速；未展开时不扫加速，避免关卡列表误点。
                    if (!scenarioMenuOpened)
                    {
                        if (arrowVisible &&
                            ReadyToClick(lastClick, lastClickAt, "scenarioMenu"))
                        {
                            MarkClick(ref lastClick, ref lastClickAt, "scenarioMenu");
                            await ClickMatchAsync(window, arrow, "剧情菜单(箭头展开)", cancellationToken);
                            scenarioMenuOpened = true;
                            missTimer.Restart();
                            await Task.Delay(450, cancellationToken);
                            continue;
                        }

                        if (TryHit(probes, "next", out TemplateProbeResult earlyNextClosed) &&
                            ReadyToClick(lastClick, lastClickAt, "next"))
                        {
                            MarkClick(ref lastClick, ref lastClickAt, "next");
                            bool clearedEarlyClosed = await ClickNextUntilGoneAsync(
                                window, earlyNextClosed, cancellationToken);
                            if (!clearedEarlyClosed)
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
                            throw new TimeoutException($"自动主线剧情持续 {MissTimeoutMs / 1000} 秒未点到右上角箭头。");

                        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
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
                        _log("剧情菜单已展开并开启加速，等待播放；箭头卡住 15s 再点选项。");
                        continue;
                    }

                    // 展开后仍不见加速：再点一次箭头（菜单可能被点回关闭）。
                    if (arrowVisible &&
                        missTimer.ElapsedMilliseconds >= 2500 &&
                        ReadyToClick(lastClick, lastClickAt, "scenarioMenu"))
                    {
                        MarkClick(ref lastClick, ref lastClickAt, "scenarioMenu");
                        await ClickMatchAsync(window, arrow, "剧情菜单(再展开)", cancellationToken);
                        missTimer.Restart();
                        await Task.Delay(450, cancellationToken);
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

                if (TryReadyStartClick(probes, ref startAppearAt, out TemplateProbeResult scenarioStart) &&
                    ReadyToClick(lastClick, lastClickAt, "begin"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "begin");
                    window = await DoubleClickBeginAsync(window, scenarioStart, cancellationToken);
                    phase = Phase.Sortie;
                    ResetScenario(ref scenarioSped, ref scenarioMenuOpened, ref scenarioArrowSince, ref startAppearAt);
                    missTimer.Restart();
                    continue;
                }

                if (missTimer.ElapsedMilliseconds >= ScenarioMissTimeoutMs)
                    throw new TimeoutException($"自动主线剧情持续 {ScenarioMissTimeoutMs / 1000} 秒未结束。");

                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            // —— 战斗中：同迷宫优先点 SKIP；若是剧情则转加速 ——
            if (phase == Phase.Battle)
            {
                if (TryPickBattleSkip(probes, out TemplateProbeResult battleSkip) &&
                    ReadyToClick(lastClick, lastClickAt, "skip"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "skip");
                    await ClickMazeStyleSkipAsync(window, battleSkip, cancellationToken);
                    phase = Phase.Next;
                    missTimer.Restart();
                    battleSkipFallback = null;
                    _log("战斗 SKIP 已点，等待下一步。");
                    continue;
                }

                battleSkipFallback ??= Stopwatch.StartNew();
                double skipAltScore = probes.TryGetValue("skipAlt", out TemplateProbeResult? skaB) ? skaB.Score : 0;
                if (skipAltScore >= 0.40 &&
                    battleSkipFallback.ElapsedMilliseconds >= 1500 &&
                    ReadyToClick(lastClick, lastClickAt, "skip"))
                {
                    MarkClick(ref lastClick, ref lastClickAt, "skip");
                    _log($"战斗 SKIP 分偏高（{skipAltScore:F2}），点 SKIP 写死点兜底。");
                    await _screen.ClickAsync(window, _config.BattleSkipClick, "SKIP(未命中兜底)", cancellationToken);
                    phase = Phase.Next;
                    missTimer.Restart();
                    battleSkipFallback = null;
                    continue;
                }

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
            ("scenarioMenu", _scenarioMenu), ("scenarioMenuBack", _scenarioMenuBack),
            ("skip", _skip), ("sortie", _sortie), ("start", _start)
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

            if (TryPickScenarioMenu(probes, out _))
            {
                _log("开局定位：剧情中（右上角箭头）。");
                return (Phase.Scenario, false);
            }

            if (TryHit(probes, "skip", out _))
            {
                _log("开局定位：战斗中（SKIP）。");
                return (Phase.Battle, false);
            }

            if (TryHit(probes, "sortie", out _))
            {
                _log("开局定位：出击准备页。");
                return (Phase.Sortie, false);
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
            "メインクエスト",
            cancellationToken,
            _banner,
            _config.MainQuestBannerTopLeft,
            _config.MainQuestBannerSize);
        return (Phase.Start, false);
    }

    private async Task<GameWindow> DoubleClickBeginAsync(
        GameWindow window, TemplateProbeResult start, CancellationToken cancellationToken)
    {
        int gapMs = Math.Max(_config.DoubleClickIntervalMs, 180);
        // 必须点模板命中中心：ROI 几何中心常落在关卡号白底上，点不中粉钮「クエスト開始」。
        _log("双击开始/情景回放（模板中心）。");
        await _screen.ClickProbeAsync(window, start, "开始/情景 1/2", cancellationToken, settleDelayMs: 0, parkCursor: false);
        await Task.Delay(gapMs, cancellationToken);
        await _screen.ClickProbeAsync(window, start, "开始/情景 2/2", cancellationToken, settleDelayMs: 200, parkCursor: false);
        _screen.ParkCursorAway(window);
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
                "skipAlt" => 0.62,
                "skip" or "next" => 0.52,
                "scenarioMenu" => 0.50,
                "scenarioMenuBack" => 0.82,
                "scenarioSpeed" => 0.58,
                "scenarioVoiceNone" => 0.80,
                "scenarioOk" or "scenarioChoice" or "scenarioChoiceAlt" => 0.55,
                "scenarioPortrait" => 0.88,
                // 1080p 实机因按钮抗锯齿与 4K 模板不同约 0.60；战斗负样本仅约 0.16。
                "scenarioReplay" => 0.55,
                "scenarioReplay1080" => 0.80,
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
        "scenarioReplay" or "scenarioReplay1080" =>
            (_config.MainQuestStartTopLeft, _config.MainQuestStartSize),
        "sortie" => (_config.MainQuestSortieTopLeft, _config.MainQuestSortieSize),
        "scenarioMenu" or "scenarioMenuBack" =>
            (_config.MainQuestScenarioMenuTopLeft, _config.MainQuestScenarioMenuSize),
        "scenarioSpeed" => (_config.MainQuestScenarioSpeedTopLeft, _config.MainQuestScenarioSpeedSize),
        "scenarioVoiceNone" => (_config.MainQuestScenarioOkTopLeft, _config.MainQuestScenarioOkSize),
        "scenarioOk" => (_config.MainQuestScenarioOkTopLeft, _config.MainQuestScenarioOkSize),
        "scenarioChoice" or "scenarioChoiceAlt" =>
            (_config.MainQuestScenarioChoiceTopLeft, _config.MainQuestScenarioChoiceSize),
        "scenarioPortrait" =>
            (_config.MainQuestScenarioPortraitTopLeft, _config.MainQuestScenarioPortraitSize),
        "skip" => (_config.MainQuestSkipTopLeft, _config.MainQuestSkipSize),
        // 与迷宫同一套 battle-skip ROI / 写死点。
        "skipAlt" => (_config.BattleSkipTopLeft, _config.BattleSkipSize),
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
        phase is Phase.Battle or Phase.Next or Phase.Sortie;

    private static bool TryHit(
        IReadOnlyDictionary<string, TemplateProbeResult> probes, string key, out TemplateProbeResult probe) =>
        TemplateProbes.TryGetHit(probes, key, out probe);

    private static bool TryPickScenarioMenu(
        IReadOnlyDictionary<string, TemplateProbeResult> probes,
        out TemplateProbeResult menu)
    {
        bool hasOriginal = TryHit(probes, "scenarioMenu", out TemplateProbeResult original);
        bool hasBack = TryHit(probes, "scenarioMenuBack", out TemplateProbeResult back);
        if (hasBack && (!hasOriginal || back.Score >= original.Score))
        {
            menu = back;
            return true;
        }

        menu = original;
        return hasOriginal;
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
        return startAppearAt.ElapsedMilliseconds >= StartAppearSettleMs;
    }

    private bool TryPickBattleSkip(
        IReadOnlyDictionary<string, TemplateProbeResult> probes,
        out TemplateProbeResult skip)
    {
        if (TryHit(probes, "skipAlt", out skip))
            return true;
        return TryHit(probes, "skip", out skip);
    }

    /// <summary>与迷宫一致：点匹配中心，仍在则补点 BattleSkipClick。</summary>
    private async Task ClickMazeStyleSkipAsync(
        GameWindow window, TemplateProbeResult skip, CancellationToken cancellationToken)
    {
        await _screen.ClickProbeAsync(window, skip, "战斗 SKIP", cancellationToken, settleDelayMs: 150);
        TemplateProbeResult still = await _screen.ProbeAsync(
            window, _skipAlt, _config.BattleSkipTopLeft, _config.BattleSkipSize,
            cancellationToken, 0.62);
        if (!still.IsMatch)
            return;

        _log($"SKIP 仍在（{still.Score:F3}），改用固定坐标再点。");
        await _screen.ClickAsync(window, _config.BattleSkipClick, "SKIP(坐标补点)", cancellationToken);
        await Task.Delay(200, cancellationToken);
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
