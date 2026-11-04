using System.Diagnostics;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 任务失败后的统一恢复：结算/战败点ホームへ → 清弹窗 → Esc → 回主页。
/// </summary>
public sealed class TaskFailureHandler
{
    private const int EscapeRounds = 3;
    private const int AfterEscapeMs = 350;
    private const int AfterHomeMs = 400;
    private const int HomeConfirmTimeoutMs = 4000;
    private const double QuestPresenceThreshold = 0.80;
    private const double HudHomeThreshold = 0.78;
    private static readonly ConfigPoint HudHomeTopLeft = new(1420, 0);
    private static readonly ConfigSize HudHomeSize = new(500, 260);

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly PromoPopupDismisser _promo;
    private readonly BattleResultHome _resultHome;
    private readonly TemplateMatcher _questMatcher;
    private readonly TemplateMatcher _hudHomeMatcher;
    private readonly TemplateMatcher _nextMatcher;

    public TaskFailureHandler(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _promo = new PromoPopupDismisser(config, _screen, log);
        _resultHome = new BattleResultHome(config, _screen, log);
        _questMatcher = TemplateAssets.Load("quest.png");
        _hudHomeMatcher = TemplateAssets.Load("hud-home.png");
        _nextMatcher = TemplateAssets.Load("main-quest-next.png");
    }

    /// <summary>
    /// 尝试拉回主页。返回 true 表示已确认底栏入口存在且右上返回主页按钮消失。
    /// </summary>
    public async Task<bool> RecoverToHomeAsync(string taskName, CancellationToken cancellationToken)
    {
        _log($"失败处理：{taskName} — 开始恢复（ホームへ → 清弹窗 → Esc → 回主页）。");
        try
        {
            GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
            if (!await _screen.FocusAsync(window.Handle, cancellationToken))
            {
                _log("失败处理：未能将游戏置于前台。");
                return false;
            }

            window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);

            if (await IsHomeVisibleAsync(window, cancellationToken))
            {
                _log($"失败处理：{taskName} — 已在主界面，跳过恢复。");
                return true;
            }

            // 战败/结算页先点ホームへ，再 Esc（Esc 对 DEFEAT 往往无效）。
            if (await TryLeaveSettlementAsync(window, cancellationToken))
            {
                window = _screen.Refresh(window);
                if (await IsHomeVisibleAsync(window, cancellationToken))
                {
                    _log($"失败处理：{taskName} — 点ホームへ后已确认主界面。");
                    return true;
                }
            }

            for (int i = 0; i < 2; i++)
            {
                int closed = await _promo.DismissAllAsync(window, cancellationToken);
                window = _screen.Refresh(window);
                if (await IsHomeVisibleAsync(window, cancellationToken))
                {
                    _log($"失败处理：{taskName} — 清弹窗后已在主界面。");
                    return true;
                }
                if (closed == 0)
                    break;
                await Task.Delay(250, cancellationToken);
            }

            for (int i = 0; i < EscapeRounds; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _log($"失败处理：发送 Esc（{i + 1}/{EscapeRounds}）。");
                await _screen.Mouse.SendEscapeAsync(window.Handle, cancellationToken);
                await Task.Delay(AfterEscapeMs, cancellationToken);
                window = _screen.Refresh(window);
                await _promo.DismissAllAsync(window, cancellationToken);
                window = _screen.Refresh(window);
                if (await IsHomeVisibleAsync(window, cancellationToken))
                {
                    _log($"失败处理：{taskName} — Esc 后已确认主界面。");
                    return true;
                }

                if (await TryLeaveSettlementAsync(window, cancellationToken))
                {
                    window = _screen.Refresh(window);
                    if (await IsHomeVisibleAsync(window, cancellationToken))
                    {
                        _log($"失败处理：{taskName} — Esc 后点ホームへ已确认主界面。");
                        return true;
                    }
                }
            }

            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
            await Task.Delay(AfterHomeMs, cancellationToken);
            window = _screen.Refresh(window);
            await _promo.DismissAllAsync(window, cancellationToken);
            window = _screen.Refresh(window);

            TemplateProbeResult quest = await WaitForHomeAsync(window, cancellationToken);
            if (quest.IsMatch)
            {
                _log($"失败处理：{taskName} — 已确认主界面（クエスト {quest.Score:F3}，无返回主页按钮）。");
                return true;
            }

            _log($"失败处理：{taskName} — 未能确认主界面（クエスト最高 {quest.Score:F3}）。");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"失败处理：{taskName} — 恢复异常：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> IsHomeVisibleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        ConfigSize size = ScreenAutomation.EnsureFitsTemplate(_config.FirstSearchSize, _questMatcher);
        TemplateProbeResult quest = await _screen.ProbeAsync(
            window, _questMatcher, _config.SearchTopLeft, size,
            cancellationToken, QuestPresenceThreshold);
        if (!quest.IsMatch)
            return false;

        TemplateProbeResult hudHome = await _screen.ProbeAsync(
            window, _hudHomeMatcher, HudHomeTopLeft, HudHomeSize,
            cancellationToken, HudHomeThreshold);
        return !hudHome.IsMatch;
    }

    /// <summary>结算/战败页：优先「ホームへ」，否则连点「次へ」翻页。</summary>
    private async Task<bool> TryLeaveSettlementAsync(GameWindow window, CancellationToken cancellationToken)
    {
        bool interacted = false;
        for (int i = 0; i < 5; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            if (await IsHomeVisibleAsync(window, cancellationToken))
                return true;

            if (await _resultHome.TryClickHomeAsync(window, "失败处理", cancellationToken))
            {
                interacted = true;
                await Task.Delay(600, cancellationToken);
                continue;
            }

            TemplateProbeResult next = await _screen.ProbeAsync(
                window, _nextMatcher,
                _config.MainQuestNextTopLeft, _config.MainQuestNextSize,
                cancellationToken, 0.70);
            if (next.IsMatch)
            {
                _log($"失败处理：结算页点击「次へ」（{next.Score:F3}）。");
                await _screen.ClickProbeAsync(window, next, "失败恢复次へ", cancellationToken, 700);
                interacted = true;
                await Task.Delay(400, cancellationToken);
                continue;
            }

            break;
        }

        return interacted;
    }

    private async Task<TemplateProbeResult> WaitForHomeAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        ConfigSize size = ScreenAutomation.EnsureFitsTemplate(_config.FirstSearchSize, _questMatcher);
        var timer = Stopwatch.StartNew();
        TemplateProbeResult best = TemplateProbes.Empty;
        int ticks = 0;
        do
        {
            if (ticks > 0 && ticks % 4 == 0)
            {
                window = _screen.Refresh(window);
                await _promo.DismissAllAsync(window, cancellationToken);
                window = _screen.Refresh(window);
                await _screen.FocusAsync(window.Handle, cancellationToken);
            }

            TemplateProbeResult probe = await _screen.ProbeAsync(
                window, _questMatcher, _config.SearchTopLeft, size,
                cancellationToken, QuestPresenceThreshold);
            if (probe.Score > best.Score)
                best = probe;
            if (probe.IsMatch && await IsHomeVisibleAsync(window, cancellationToken))
                return probe;

            if (timer.ElapsedMilliseconds < HomeConfirmTimeoutMs)
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
            ticks++;
        }
        while (timer.ElapsedMilliseconds < HomeConfirmTimeoutMs);

        return best;
    }
}
