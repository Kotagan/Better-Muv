using System.Diagnostics;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 确认已在主界面（底栏「クエスト」可见且不存在右上返回主页按钮）；否则清弹窗 / Esc / 点主页钮后重试。
/// </summary>
public sealed class HomePresence
{
    private const double QuestThreshold = 0.80;
    private const double HudHomeThreshold = 0.78;
    private const int ConfirmTimeoutMs = 2500;
    private static readonly ConfigPoint HudHomeTopLeft = new(1420, 0);
    private static readonly ConfigSize HudHomeSize = new(500, 260);

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly PromoPopupDismisser _promo;
    private readonly TemplateMatcher _questMatcher;
    private readonly TemplateMatcher _hudHomeMatcher;

    public HomePresence(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _promo = new PromoPopupDismisser(config, screen, log);
        _questMatcher = TemplateAssets.Load("quest.png");
        _hudHomeMatcher = TemplateAssets.Load("hud-home.png");
    }

    public async Task<bool> IsHomeAsync(GameWindow window, CancellationToken cancellationToken)
    {
        ConfigSize questSize = ScreenAutomation.EnsureFitsTemplate(_config.FirstSearchSize, _questMatcher);
        TemplateProbeResult quest = await _screen.ProbeAsync(
            window, _questMatcher, _config.SearchTopLeft, questSize, cancellationToken, QuestThreshold);
        if (!quest.IsMatch)
            return false;

        TemplateProbeResult hudHome = await _screen.ProbeAsync(
            window, _hudHomeMatcher, HudHomeTopLeft, HudHomeSize, cancellationToken, HudHomeThreshold);
        return !hudHome.IsMatch;
    }

    /// <summary>若已在主页直接 true；否则轻清弹窗 / 恢复后再确认。</summary>
    public async Task<(GameWindow Window, bool OnHome)> EnsureAsync(
        GameWindow window, string scope, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);

        // 快路径：已在主页则跳过弹窗扫描（任务开始最常见）。
        if (await IsHomeAsync(window, cancellationToken))
        {
            _log($"{scope}：已在主界面。");
            return (window, true);
        }

        _log($"{scope}：未在主界面，先清弹窗。");
        await _promo.DismissAllAsync(window, cancellationToken);
        window = _screen.Refresh(window);
        if (await IsHomeAsync(window, cancellationToken))
        {
            _log($"{scope}：清弹窗后已在主界面。");
            return (window, true);
        }

        _log($"{scope}：尝试回主页。");
        bool recovered = await new TaskFailureHandler(_config, _log)
            .RecoverToHomeAsync(scope, cancellationToken);
        window = _screen.Refresh(window);
        if (recovered || await WaitForHomeAsync(window, cancellationToken))
        {
            _log($"{scope}：已回到主界面。");
            return (_screen.Refresh(window), true);
        }

        _log($"{scope}：仍未能确认主界面。");
        return (_screen.Refresh(window), false);
    }

    private async Task<bool> WaitForHomeAsync(GameWindow window, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if (await IsHomeAsync(window, cancellationToken))
                return true;
            if (timer.ElapsedMilliseconds < ConfirmTimeoutMs)
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
            window = _screen.Refresh(window);
        }
        while (timer.ElapsedMilliseconds < ConfirmTimeoutMs);
        return false;
    }
}
