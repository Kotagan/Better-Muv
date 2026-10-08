using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 若出现主界面房子按钮则点一次返回主页，再等页面切换。已在主页时通常没有该图标，会直接跳过。
/// 不清弹窗；弹窗遮挡导致无法回主页时，交由失败处理。
/// </summary>
public sealed class HudHomeReturn
{
    private const int AfterClickDelayMs = 500;
    /// <summary>须明显高于误检；曾降到 0.66 会在无主页钮时点到 0.68 假阳性。</summary>
    private const double HomeButtonThreshold = 0.78;
    private static readonly ConfigPoint TopRightAnchorTopLeft = new(1420, 0);
    private static readonly ConfigSize TopRightAnchorSize = new(500, 260);

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly TemplateMatcher _matcher;
    private readonly Action<string> _log;

    public HudHomeReturn(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _matcher = TemplateAssets.Load("hud-home.png");
    }

    public async Task<bool> TryAsync(GameWindow window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        window = _screen.Refresh(window);

        TemplateProbeResult probe = await ProbeHomeButtonAsync(window, cancellationToken);
        if (!probe.IsMatch)
        {
            _log($"未发现主界面按钮（{probe.Score:F2}），跳过返回。");
            return false;
        }

        return await ClickHomeAsync(window, probe, cancellationToken);
    }

    private async Task<TemplateProbeResult> ProbeHomeButtonAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult probe = await _screen.ProbeAsync(
            window, _matcher, _config.HudHomeTopLeft, _config.HudHomeSize,
            cancellationToken, HomeButtonThreshold);
        if (!probe.IsMatch &&
            (_config.HudHomeTopLeft != TopRightAnchorTopLeft ||
             _config.HudHomeSize != TopRightAnchorSize))
        {
            TemplateProbeResult anchorProbe = await _screen.ProbeAsync(
                window,
                _matcher,
                TopRightAnchorTopLeft,
                TopRightAnchorSize,
                cancellationToken,
                HomeButtonThreshold);
            if (anchorProbe.Score > probe.Score)
                probe = anchorProbe;
        }
        return probe;
    }

    private async Task<bool> ClickHomeAsync(
        GameWindow window, TemplateProbeResult probe, CancellationToken cancellationToken)
    {
        _log($"发现主界面按钮 {probe.Score:F3}，点击返回主页。");
        await _screen.ClickScreenAsync(window, probe.Center, cancellationToken);
        await Task.Delay(AfterClickDelayMs, cancellationToken);
        return true;
    }
}
