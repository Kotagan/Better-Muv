using System.Diagnostics;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 确认已在主界面（底栏「クエスト」可见）；否则清弹窗 / Esc / 点主页钮后重试。
/// </summary>
public sealed class HomePresence
{
    private const double QuestThreshold = 0.80;
    private const int ConfirmTimeoutMs = 2500;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly PromoPopupDismisser _promo;
    private readonly TemplateMatcher _questMatcher;

    public HomePresence(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _promo = new PromoPopupDismisser(config, screen, log);
        _questMatcher = TemplateAssets.Load("quest.png");
    }

    public async Task<bool> IsHomeAsync(GameWindow window, CancellationToken cancellationToken)
    {
        ConfigSize size = ScreenAutomation.EnsureFitsTemplate(_config.FirstSearchSize, _questMatcher);
        TemplateProbeResult quest = await _screen.ProbeAsync(
            window, _questMatcher, _config.SearchTopLeft, size, cancellationToken, QuestThreshold);
        return quest.IsMatch;
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
        ConfigSize size = ScreenAutomation.EnsureFitsTemplate(_config.FirstSearchSize, _questMatcher);
        var timer = Stopwatch.StartNew();
        do
        {
            TemplateProbeResult quest = await _screen.ProbeAsync(
                window, _questMatcher, _config.SearchTopLeft, size, cancellationToken, QuestThreshold);
            if (quest.IsMatch)
                return true;
            if (timer.ElapsedMilliseconds < ConfirmTimeoutMs)
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
            window = _screen.Refresh(window);
        }
        while (timer.ElapsedMilliseconds < ConfirmTimeoutMs);
        return false;
    }
}
