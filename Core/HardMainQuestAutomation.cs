using System.Diagnostics;
using System.Windows;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 自动困难主线：与自动主线相同循环，但在开始界面先切到困难（红底 MAIN QUEST BATTLE）。
/// 出现「再戦」时点击「ホームへ」并结束。
/// </summary>
public sealed class HardMainQuestAutomation
{
    private const double PresenceThreshold = 0.62;
    private const int MissTimeoutMs = 120000;
    private const int ClickCooldownMs = 100;
    private const int StartAppearSettleMs = 100;

    private enum Phase { Start, Sortie, Battle, Next }

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _start;
    private readonly TemplateMatcher _sortie;
    private readonly TemplateMatcher _skip;
    private readonly TemplateMatcher _skipAlt;
    private readonly TemplateMatcher _next;
    private readonly TemplateMatcher _rematch;
    private readonly TemplateMatcher _toHome;
    private readonly TemplateMatcher _difficulty;
    private readonly TemplateMatcher _hardMark;
    private readonly TemplateMatcher _scenarioOk;
    private readonly TemplateMatcher _banner;
    private readonly PromoPopupDismisser _promoPopup;

    public HardMainQuestAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _start = TemplateAssets.Load("main-quest-start.png");
        _sortie = TemplateAssets.Load("main-quest-sortie.png");
        _skip = TemplateAssets.Load("main-quest-skip.png");
        _skipAlt = TemplateAssets.Load("battle-skip.png");
        _next = TemplateAssets.Load("main-quest-next.png");
        _rematch = TemplateAssets.Load("main-quest-rematch.png");
        _toHome = TemplateAssets.Load("main-quest-to-home.png");
        _difficulty = TemplateAssets.Load("hard-quest-difficulty.png");
        _hardMark = TemplateAssets.Load("hard-quest-battle.png");
        _scenarioOk = TemplateAssets.Load("main-quest-scenario-ok.png");
        _banner = TemplateAssets.Load("main-quest-banner.png");
        _promoPopup = new PromoPopupDismisser(config, _screen, log);
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"困难主线：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return;
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"困难主线：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}，显示器 {window.DisplayRect.Width}×{window.DisplayRect.Height}");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        window = _screen.Refresh(window);
        _log("困难主线：开局只看是否已在关卡内；否则回主页连点进主线。开始界面切困难后再出击；再戦则回主页结束。");

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
        bool hardConfirmed = false;

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

            IReadOnlyList<(string Key, TemplateMatcher Matcher)> active = phase switch
            {
                Phase.Start =>
                [
                    ("hardMark", _hardMark), ("difficulty", _difficulty),
                    ("start", _start), ("sortie", _sortie), ("next", _next), ("scenarioOk", _scenarioOk)
                ],
                Phase.Sortie =>
                [
                    ("hardMark", _hardMark), ("sortie", _sortie),
                    ("skip", _skip), ("skipAlt", _skipAlt), ("next", _next), ("rematch", _rematch), ("toHome", _toHome)
                ],
                Phase.Battle =>
                [
                    ("scenarioOk", _scenarioOk), ("skip", _skip), ("skipAlt", _skipAlt),
                    ("next", _next), ("rematch", _rematch), ("toHome", _toHome)
                ],
                _ =>
                [
                    ("scenarioOk", _scenarioOk), ("next", _next), ("start", _start),
                    ("hardMark", _hardMark), ("rematch", _rematch), ("toHome", _toHome)
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
                _log($"困难主线探测异常：{ex.Message}");
                await Task.Delay(200, cancellationToken);
                continue;
            }

            if (TryHit(probes, "hardMark", out _))
                hardConfirmed = true;

            if (lastLog.ElapsedMilliseconds >= 2000)
            {
                string snap = string.Join(' ', active.Select(a =>
                    probes.TryGetValue(a.Key, out TemplateProbeResult? p)
                        ? $"{a.Key}={p.Score:F2}{(p.IsMatch ? "*" : "")}"
                        : a.Key));
                _log($"困难主线 已通关{completed} hard={(hardConfirmed ? 1 : 0)} {probeMs}ms phase={phase} {snap}");
                lastLog.Restart();
            }

            // 必须以「再戦」为准；単独 toHome 易在其它界面误匹配，不能当失败。
            if (TryHit(probes, "rematch", out _))
            {
                _log("检测到再戦，点击ホームへ后结束困难主线。");
                if (TryHit(probes, "toHome", out TemplateProbeResult homeBtn))
                    await ClickMatchAsync(window, homeBtn, "ホームへ", cancellationToken);
                else if (!await WaitForClickAsync(window, "toHome", [("toHome", _toHome)], cancellationToken))
                    _log("再戦出现但未找到ホームへ。");

                _log("战斗失败，困难主线已停止。");
                return;
            }

            if (TryHit(probes, "scenarioOk", out TemplateProbeResult scenarioOk) &&
                ReadyToClick(lastClick, lastClickAt, "scenarioOk"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "scenarioOk");
                await ClickMatchAsync(window, scenarioOk, "升级OK", cancellationToken);
                missTimer.Restart();
                continue;
            }

            if (phase is Phase.Start &&
                !hardConfirmed &&
                TryHit(probes, "difficulty", out TemplateProbeResult difficulty) &&
                ReadyToClick(lastClick, lastClickAt, "difficulty"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "difficulty");
                await ClickMatchAsync(window, difficulty, "难易度变更", cancellationToken);
                missTimer.Restart();
                continue;
            }

            // 结算「下一步」优先：避免 Start 且未确认困难时卡在结算页空转。
            if (TryHit(probes, "next", out TemplateProbeResult nextEarly) &&
                ReadyToClick(lastClick, lastClickAt, "next"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "next");
                bool clearedEarly = await ClickNextUntilGoneAsync(window, nextEarly, cancellationToken);
                if (!clearedEarly)
                {
                    phase = Phase.Next;
                    missTimer.Restart();
                    continue;
                }

                completed++;
                _log($"困难主线：已通关第 {completed} 轮，继续下一关。");
                phase = Phase.Start;
                startAppearAt = null;
                missTimer.Restart();
                continue;
            }

            if (phase is Phase.Start && !hardConfirmed)
            {
                if (missTimer.ElapsedMilliseconds >= MissTimeoutMs)
                    throw new TimeoutException("困难主线未能切到红底 MAIN QUEST BATTLE。");
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

            // next 已在上方统一处理。

            if (TryHit(probes, "sortie", out TemplateProbeResult sortie) &&
                ReadyToClick(lastClick, lastClickAt, "sortie"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "sortie");
                await ClickMatchAsync(window, sortie, "出击", cancellationToken);
                phase = Phase.Battle;
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
                throw new TimeoutException($"困难主线持续 {MissTimeoutMs / 1000} 秒未识别到可操作界面（phase={phase}）。");

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
    }

    private async Task<(Phase Phase, bool Abort)> BootstrapEntryAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var locateJobs = new (string Key, TemplateMatcher Matcher)[]
        {
            ("rematch", _rematch), ("toHome", _toHome), ("next", _next),
            ("start", _start), ("sortie", _sortie)
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

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }

        _log("开局不在关卡内：回主页后连点进主线。");
        await new QuestFromHomeEntry(_config, _screen, _log).RunAsync(
            window,
            QuestFromHomeEntry.MainQuestBannerClick(_config),
            "メインクエスト",
            cancellationToken,
            _banner);
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
                "scenarioOk" => 0.55,
                "rematch" => 0.72,
                "hardMark" => 0.58,
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
        "skip" or "skipAlt" => (_config.MainQuestSkipTopLeft, _config.MainQuestSkipSize),
        "next" => (_config.MainQuestNextTopLeft, _config.MainQuestNextSize),
        "scenarioOk" => (_config.MainQuestScenarioOkTopLeft, _config.MainQuestScenarioOkSize),
        "rematch" => (_config.MainQuestRematchTopLeft, _config.MainQuestRematchSize),
        "difficulty" => (_config.HardQuestDifficultyTopLeft, _config.HardQuestDifficultySize),
        "hardMark" => (_config.HardQuestBattleTopLeft, _config.HardQuestBattleSize),
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

    /// <summary>宣传弹窗只在战斗结束后挡结算，主页与进关前不扫。</summary>
    private static bool ShouldCheckPromoPopup(Phase phase) =>
        phase is Phase.Battle or Phase.Next;

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

    private async Task ClickMatchAsync(
        GameWindow window, TemplateProbeResult probe, string reason, CancellationToken cancellationToken)
    {
        await _screen.ClickProbeAsync(window, probe, reason, cancellationToken);
    }

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
}
