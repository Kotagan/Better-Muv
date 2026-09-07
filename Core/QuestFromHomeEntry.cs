using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 有主页钮则回主页，再固定连点：任务 → 目标（主线横幅 / メイズ）。中间不识别任务选择页。
/// </summary>
public sealed class QuestFromHomeEntry
{
    /// <summary>任务页切换较慢，点完任务后多等一会再点目标。</summary>
    private const int AfterQuestDelayMs = 1500;
    private const int AfterTargetDelayMs = 800;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;

    public QuestFromHomeEntry(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
    }

    public static ConfigPoint MainQuestBannerClick(AutomationConfig config) => new(
        config.MainQuestBannerTopLeft.X + config.MainQuestBannerSize.Width / 2,
        config.MainQuestBannerTopLeft.Y + config.MainQuestBannerSize.Height / 2);

    public async Task<GameWindow> RunAsync(
        GameWindow window,
        ConfigPoint targetClick,
        string targetName,
        CancellationToken cancellationToken)
    {
        // 有主页钮则点一次并已在内部等 500ms。
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);

        _log($"进关：任务 → {targetName}（双击，不识别任务页）。");
        window = await DoubleClickAsync(window, _config.FirstClick, "任务", cancellationToken);
        await Task.Delay(AfterQuestDelayMs, cancellationToken);

        window = await DoubleClickAsync(window, targetClick, targetName, cancellationToken);
        await Task.Delay(AfterTargetDelayMs, cancellationToken);
        return _screen.Refresh(window);
    }

    private async Task<GameWindow> DoubleClickAsync(
        GameWindow window, ConfigPoint point, string reason, CancellationToken cancellationToken)
    {
        int gapMs = Math.Max(_config.DoubleClickIntervalMs, 50);
        window = await _screen.ClickAsync(window, point, $"{reason} 1/2", cancellationToken);
        await Task.Delay(gapMs, cancellationToken);
        return await _screen.ClickAsync(window, point, $"{reason} 2/2", cancellationToken);
    }
}
