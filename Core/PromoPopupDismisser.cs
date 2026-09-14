using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 挡住流程的弹窗：宣传右上角 X，以及奖励确认粉钮 OK。
/// </summary>
public sealed class PromoPopupDismisser
{
    /// <summary>过低会把右上角 HUD 误当成关闭钮，形成连点死循环。</summary>
    private const double CloseThreshold = 0.78;
    private const double RewardOkThreshold = 0.85;
    private const int MaxFailedBursts = 3;
    private const int SuppressAfterFailMs = 20000;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _closeMatcher;
    private readonly TemplateMatcher _rewardOkMatcher;
    private int _failedBursts;
    private DateTime _suppressUntilUtc = DateTime.MinValue;

    /// <summary>奖励确认弹窗粉钮 OK 搜索区（1080p）。</summary>
    private static readonly ConfigPoint RewardOkTopLeft = new(700, 720);
    private static readonly ConfigSize RewardOkSize = new(520, 240);

    public PromoPopupDismisser(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _closeMatcher = TemplateAssets.Load("popup-close.png");
        _rewardOkMatcher = TemplateAssets.Load("daily-shop-ok.png");
    }

    /// <returns>true 仅表示已成功关掉弹窗；未识别/点了仍在/抑制期内均返回 false。</returns>
    public async Task<bool> TryAsync(GameWindow window, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _suppressUntilUtc)
            return false;

        if (await TryDismissRewardOkAsync(window, cancellationToken))
            return true;

        TemplateProbeResult probe = await _screen.ProbeAsync(
            window, _closeMatcher,
            _config.PopupCloseTopLeft, _config.PopupCloseSize,
            cancellationToken, CloseThreshold);
        if (!probe.IsMatch)
        {
            _failedBursts = 0;
            return false;
        }

        _log($"检测到宣传弹窗关闭钮（{probe.Score:F3}），点击关闭。");
        await _screen.ClickScreenAsync(window, probe.Center, cancellationToken);
        await Task.Delay(400, cancellationToken);

        TemplateProbeResult still = await _screen.ProbeAsync(
            window, _closeMatcher,
            _config.PopupCloseTopLeft, _config.PopupCloseSize,
            cancellationToken, CloseThreshold);
        if (!still.IsMatch)
        {
            _failedBursts = 0;
            return true;
        }

        _log($"关闭钮仍在（{still.Score:F3}），再点一次固定点。");
        await _screen.ClickAsync(window, _config.PopupCloseClick, "宣传弹窗关闭", cancellationToken);
        await Task.Delay(400, cancellationToken);

        TemplateProbeResult after = await _screen.ProbeAsync(
            window, _closeMatcher,
            _config.PopupCloseTopLeft, _config.PopupCloseSize,
            cancellationToken, CloseThreshold);
        if (!after.IsMatch)
        {
            _failedBursts = 0;
            return true;
        }

        _failedBursts++;
        _log($"宣传弹窗关闭无效（仍 {after.Score:F3}），连续失败 {_failedBursts}/{MaxFailedBursts}。");
        if (_failedBursts >= MaxFailedBursts)
        {
            _suppressUntilUtc = DateTime.UtcNow.AddMilliseconds(SuppressAfterFailMs);
            _failedBursts = 0;
            _log($"宣传关闭连续失败，暂停识别 {SuppressAfterFailMs / 1000}s，继续主流程。");
        }

        // 未关掉时返回 false，避免主线/迷宫外层一直 continue 空转。
        return false;
    }

    private async Task<bool> TryDismissRewardOkAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult ok = await _screen.ProbeAsync(
            window, _rewardOkMatcher, RewardOkTopLeft, RewardOkSize,
            cancellationToken, RewardOkThreshold);
        if (!ok.IsMatch)
            return false;

        _log($"检测到奖励确认 OK（{ok.Score:F3}），点击关闭。");
        await _screen.ClickProbeAsync(window, ok, "奖励确认OK", cancellationToken, settleDelayMs: 200);
        await Task.Delay(600, cancellationToken);

        TemplateProbeResult still = await _screen.ProbeAsync(
            window, _rewardOkMatcher, RewardOkTopLeft, RewardOkSize,
            cancellationToken, RewardOkThreshold);
        if (!still.IsMatch)
        {
            _failedBursts = 0;
            return true;
        }

        _log($"奖励确认 OK 仍在（{still.Score:F3}），再点一次中心。");
        await _screen.ClickProbeAsync(window, still, "奖励确认OK再点", cancellationToken, settleDelayMs: 200);
        await Task.Delay(500, cancellationToken);
        TemplateProbeResult after = await _screen.ProbeAsync(
            window, _rewardOkMatcher, RewardOkTopLeft, RewardOkSize,
            cancellationToken, RewardOkThreshold);
        bool gone = !after.IsMatch;
        if (gone)
            _failedBursts = 0;
        return gone;
    }
}
