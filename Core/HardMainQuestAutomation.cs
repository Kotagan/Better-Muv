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
    private const int ClickCooldownMs = 900;

    private enum Phase { Home, Banner, Start, Sortie, Battle, Next }

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _homeQuest;
    private readonly TemplateMatcher _banner;
    private readonly TemplateMatcher _start;
    private readonly TemplateMatcher _sortie;
    private readonly TemplateMatcher _skip;
    private readonly TemplateMatcher _skipAlt;
    private readonly TemplateMatcher _next;
    private readonly TemplateMatcher _rematch;
    private readonly TemplateMatcher _toHome;
    private readonly TemplateMatcher _difficulty;
    private readonly TemplateMatcher _hardMark;

    public HardMainQuestAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _homeQuest = TemplateAssets.Load("main-quest-home-quest.png");
        _banner = TemplateAssets.Load("main-quest-banner.png");
        _start = TemplateAssets.Load("main-quest-start.png");
        _sortie = TemplateAssets.Load("main-quest-sortie.png");
        _skip = TemplateAssets.Load("main-quest-skip.png");
        _skipAlt = TemplateAssets.Load("battle-skip.png");
        _next = TemplateAssets.Load("main-quest-next.png");
        _rematch = TemplateAssets.Load("main-quest-rematch.png");
        _toHome = TemplateAssets.Load("main-quest-to-home.png");
        _difficulty = TemplateAssets.Load("hard-quest-difficulty.png");
        _hardMark = TemplateAssets.Load("hard-quest-battle.png");
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
        window = _screen.Refresh(window);
        _screen.EnsureUsableViewport(window);
        _log($"困难主线：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}，显示器 {window.DisplayRect.Width}×{window.DisplayRect.Height}");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        window = _screen.Refresh(window);
        _log("困难主线：开始界面切到困难（红底 MAIN QUEST BATTLE）后再出击；再戦则回主页结束。");

        int completed = 0;
        var phase = Phase.Home;
        string? lastClick = null;
        var lastClickAt = Stopwatch.StartNew();
        var missTimer = Stopwatch.StartNew();
        var lastLog = Stopwatch.StartNew();
        Stopwatch? battleSkipFallback = null;
        bool hardConfirmed = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            IReadOnlyList<(string Key, TemplateMatcher Matcher)> active = phase switch
            {
                Phase.Home => [("homeQuest", _homeQuest)],
                Phase.Banner => [("banner", _banner), ("start", _start), ("hardMark", _hardMark)],
                Phase.Start =>
                [
                    ("hardMark", _hardMark), ("difficulty", _difficulty),
                    ("start", _start), ("sortie", _sortie)
                ],
                Phase.Sortie =>
                [
                    ("hardMark", _hardMark), ("sortie", _sortie),
                    ("skip", _skip), ("skipAlt", _skipAlt), ("rematch", _rematch), ("toHome", _toHome)
                ],
                Phase.Battle => [("skip", _skip), ("skipAlt", _skipAlt), ("next", _next), ("rematch", _rematch), ("toHome", _toHome)],
                _ => [("next", _next), ("start", _start), ("hardMark", _hardMark), ("rematch", _rematch), ("toHome", _toHome)]
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

            if (TryHit(probes, "rematch", out _) || TryHit(probes, "toHome", out _))
            {
                _log("检测到再戦，点击ホームへ后结束困难主线。");
                if (TryHit(probes, "toHome", out TemplateProbeResult homeBtn))
                    await ClickMatchAsync(window, homeBtn, "ホームへ", cancellationToken);
                else if (!await WaitForClickAsync(window, "toHome", [("toHome", _toHome)], cancellationToken))
                    _log("再戦出现但未找到ホームへ。");

                _log("战斗失败，困难主线已停止。");
                return;
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

            if (phase is Phase.Next or Phase.Battle &&
                TryHit(probes, "next", out TemplateProbeResult next) &&
                ReadyToClick(lastClick, lastClickAt, "next"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "next");
                await ClickMatchAsync(window, next, "下一步", cancellationToken);
                completed++;
                _log($"困难主线：已通关第 {completed} 轮，继续下一关。");
                phase = Phase.Start;
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

            if (TryHit(probes, "start", out TemplateProbeResult start) &&
                ReadyToClick(lastClick, lastClickAt, "start"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "start");
                await ClickMatchAsync(window, start, "任务开始", cancellationToken);
                phase = Phase.Sortie;
                missTimer.Restart();
                continue;
            }

            if (TryHit(probes, "banner", out TemplateProbeResult banner) &&
                ReadyToClick(lastClick, lastClickAt, "banner"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "banner");
                await ClickMatchAsync(window, banner, "主线任务", cancellationToken);
                phase = Phase.Start;
                hardConfirmed = false;
                missTimer.Restart();
                continue;
            }

            if (TryHit(probes, "homeQuest", out TemplateProbeResult homeQuest) &&
                ReadyToClick(lastClick, lastClickAt, "homeQuest"))
            {
                MarkClick(ref lastClick, ref lastClickAt, "homeQuest");
                await ClickMatchAsync(window, homeQuest, "任务入口", cancellationToken);
                phase = Phase.Banner;
                missTimer.Restart();
                continue;
            }

            if (missTimer.ElapsedMilliseconds >= MissTimeoutMs)
                throw new TimeoutException($"困难主线持续 {MissTimeoutMs / 1000} 秒未识别到可操作界面（phase={phase}）。");

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
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
        "homeQuest" => (_config.MainQuestHomeTopLeft, _config.MainQuestHomeSize),
        "banner" => (_config.MainQuestBannerTopLeft, _config.MainQuestBannerSize),
        "start" => (_config.MainQuestStartTopLeft, _config.MainQuestStartSize),
        "sortie" => (_config.MainQuestSortieTopLeft, _config.MainQuestSortieSize),
        "skip" or "skipAlt" => (_config.MainQuestSkipTopLeft, _config.MainQuestSkipSize),
        "next" => (_config.MainQuestNextTopLeft, _config.MainQuestNextSize),
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
}
