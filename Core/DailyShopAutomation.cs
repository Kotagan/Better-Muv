using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日商店：回主页（已在主页则跳过）后按固定坐标连点。
/// </summary>
public sealed class DailyShopAutomation
{
    private const int AfterHomeDelayMs = 600;
    private const int BetweenClicksMs = 900;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;

    public DailyShopAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"每日商店：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return;
        }

        await Task.Delay(200, cancellationToken);
        window = _screen.Refresh(window);
        _screen.EnsureUsableViewport(window);
        _log($"每日商店：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        await Task.Delay(AfterHomeDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        (string Name, ConfigPoint Point)[] steps =
        [
            ("商店入口", _config.DailyShopEntryClick),
            ("每日页签", _config.DailyShopTabClick),
            ("商品", _config.DailyShopItemClick),
            ("购买/确认", _config.DailyShopConfirmClick),
            ("完成", _config.DailyShopDoneClick)
        ];

        foreach ((string name, ConfigPoint point) in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _log($"每日商店：点击{name}（{point.X},{point.Y}）。");
            window = await _screen.ClickAsync(window, point, name, cancellationToken);
            await Task.Delay(BetweenClicksMs, cancellationToken);
            window = _screen.Refresh(window);
        }

        _log("每日商店：连点完成，返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日商店：结束。");
    }
}
