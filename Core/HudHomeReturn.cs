using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 若左上角出现主界面房子按钮则点击返回主页。已在主页时通常没有该图标，会直接跳过。
/// </summary>
public sealed class HudHomeReturn
{
    private const int MaxClicks = 3;
    private const int AfterClickDelayMs = 800;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly TemplateMatcher _matcher;
    private readonly Action<string> _log;

    public HudHomeReturn(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _matcher = new TemplateMatcher(Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "hud-home.png"));
    }

    public async Task TryAsync(GameWindow window, CancellationToken cancellationToken)
    {
        int clicked = 0;
        for (int i = 0; i < MaxClicks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            TemplateProbeResult probe = await _screen.ProbeAsync(
                window,
                _matcher,
                _config.HudHomeTopLeft,
                _config.HudHomeSize,
                cancellationToken);
            if (!probe.IsMatch)
            {
                if (clicked == 0)
                    _log($"未发现主界面按钮（{probe.Score:F2}），跳过返回。");
                else
                    _log("主界面按钮已消失，视为已返回。");
                return;
            }

            clicked++;
            _log($"发现主界面按钮 {probe.Score:F3}，点击返回主页（{clicked}/{MaxClicks}）。");
            await _screen.ClickScreenAsync(window, probe.Center, cancellationToken);
            await Task.Delay(AfterClickDelayMs, cancellationToken);
        }
    }
}
