using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>活动/商店宣传弹窗右上角白色 X。</summary>
public sealed class PromoPopupDismisser
{
    /// <summary>过低会把右上角 HUD 误当成关闭钮，形成连点死循环。</summary>
    private const double Threshold = 0.78;
    private const int MaxFailedBursts = 3;
    private const int SuppressAfterFailMs = 20000;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _matcher;
    private int _failedBursts;
    private DateTime _suppressUntilUtc = DateTime.MinValue;

    public PromoPopupDismisser(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _matcher = TemplateAssets.Load("popup-close.png");
    }

    /// <returns>true 仅表示已成功关掉弹窗；未识别/点了仍在/抑制期内均返回 false。</returns>
    public async Task<bool> TryAsync(GameWindow window, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _suppressUntilUtc)
            return false;

        TemplateProbeResult probe = await _screen.ProbeAsync(
            window, _matcher,
            _config.PopupCloseTopLeft, _config.PopupCloseSize,
            cancellationToken, Threshold);
        if (!probe.IsMatch)
        {
            _failedBursts = 0;
            return false;
        }

        _log($"检测到宣传弹窗关闭钮（{probe.Score:F3}），点击关闭。");
        await _screen.ClickScreenAsync(window, probe.Center, cancellationToken);
        await Task.Delay(400, cancellationToken);

        TemplateProbeResult still = await _screen.ProbeAsync(
            window, _matcher,
            _config.PopupCloseTopLeft, _config.PopupCloseSize,
            cancellationToken, Threshold);
        if (!still.IsMatch)
        {
            _failedBursts = 0;
            return true;
        }

        _log($"关闭钮仍在（{still.Score:F3}），再点一次固定点。");
        await _screen.ClickAsync(window, _config.PopupCloseClick, "宣传弹窗关闭", cancellationToken);
        await Task.Delay(400, cancellationToken);

        TemplateProbeResult after = await _screen.ProbeAsync(
            window, _matcher,
            _config.PopupCloseTopLeft, _config.PopupCloseSize,
            cancellationToken, Threshold);
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
}
