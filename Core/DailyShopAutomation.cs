using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日商店：主页 → 商店 → 交換所 → 100%OFF（可跳过）→ 交換 → OK
/// → 滚轮下拉 → 左下角 2500 → 交換 → OK（×2；首次未识别则回主页结束）→ 回主页。
/// </summary>
public sealed class DailyShopAutomation
{
    private const int AfterHomeDelayMs = 700;
    private const int AfterShopOpenDelayMs = 1000;
    private const int AfterHallOpenDelayMs = 1000;
    private const int AfterItemClickDelayMs = 800;
    private const int AfterScrollDelayMs = 600;
    private const int AfterOkDelayMs = 700;
    private const int RecognizeTimeoutMs = 10000;
    private const double OffThreshold = 0.85;
    private const double ExchangeThreshold = 0.72;
    private const double HallThreshold = 0.72;
    private const double TicketThreshold = 0.72;
    private const double OkThreshold = 0.72;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _hallMatcher;
    private readonly TemplateMatcher _offMatcher;
    private readonly TemplateMatcher _ticketMatcher;
    private readonly TemplateMatcher _exchangeMatcher;
    private readonly TemplateMatcher _okMatcher;

    public DailyShopAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _hallMatcher = TemplateAssets.Load("daily-shop-exchange-hall.png");
        _offMatcher = TemplateAssets.Load("daily-shop-100off.png");
        _ticketMatcher = TemplateAssets.Load("daily-shop-ticket-2500.png");
        _exchangeMatcher = TemplateAssets.Load("daily-shop-exchange.png");
        _okMatcher = TemplateAssets.Load("daily-shop-ok.png");
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
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"每日商店：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        await Task.Delay(AfterHomeDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        _log($"每日商店：点击商店入口（{_config.DailyShopEntryClick.X},{_config.DailyShopEntryClick.Y}）。");
        window = await _screen.ClickAsync(window, _config.DailyShopEntryClick, "商店入口", cancellationToken);
        await Task.Delay(AfterShopOpenDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        window = await ClickExchangeHallAsync(window, cancellationToken);
        await Task.Delay(AfterHallOpenDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        // 1) 100%OFF → 交換 → OK；未识别则跳过，直接滚轮。
        window = await TryBuyFreeOffAsync(window, cancellationToken);

        // 2) 滚轮下拉
        _log($"每日商店：列表滚轮下拉 {_config.DailyShopScrollWheelNotches} 格。");
        await _screen.WheelAsync(
            window,
            _config.DailyShopScrollPoint,
            _config.DailyShopScrollWheelNotches,
            "商店列表滚轮",
            cancellationToken);
        await Task.Delay(AfterScrollDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        // 3) 左下角 2500 ×2；首次未识别则回主页结束。
        ConfigPoint? ticketClick = null;
        for (int i = 1; i <= 2; i++)
        {
            _log($"每日商店：购买 2500 票券（第 {i}/2 次）。");
            (window, ticketClick, bool bought) =
                await TryBuyTicket2500Async(window, ticketClick, cancellationToken);
            if (!bought)
            {
                _log("每日商店：未识别到 2500，返回主页并结束任务。");
                await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
                _log("每日商店：结束。");
                return;
            }
        }

        _log("每日商店：完成，返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日商店：结束。");
    }

    private async Task<GameWindow> ClickExchangeHallAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log($"每日商店：点击「交換所」（{_config.DailyShopExchangeHallClick.X},{_config.DailyShopExchangeHallClick.Y}）。");
        TemplateProbeResult hall = await _screen.ProbeAsync(
            window,
            _hallMatcher,
            _config.DailyShopExchangeHallTopLeft,
            _config.DailyShopExchangeHallSize,
            cancellationToken,
            HallThreshold);
        if (hall.IsMatch)
        {
            _log($"每日商店：已识别「交換所」（{hall.Score:F4}），点击匹配中心。");
            await _screen.ClickProbeAsync(window, hall, "交換所", cancellationToken, settleDelayMs: 200);
            return _screen.Refresh(window);
        }

        return await _screen.ClickAsync(
            window, _config.DailyShopExchangeHallClick, "交換所", cancellationToken);
    }

    private async Task<GameWindow> TryBuyFreeOffAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log($"每日商店：等待识别「100%OFF」，最多 {RecognizeTimeoutMs / 1000} 秒。");
        TemplateProbeResult off = await _screen.WaitForProbeAsync(
            window,
            _offMatcher,
            _config.DailyShopFreeOffTopLeft,
            _config.DailyShopFreeOffSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: OffThreshold);
        if (!off.IsMatch)
        {
            _log($"每日商店：未识别到「100%OFF」（最高 {off.Score:F4}），跳过零元购，进入滚轮。");
            return window;
        }

        _log($"每日商店：已识别「100%OFF」（{off.Score:F4}），点击商品（{_config.DailyShopFreeItemClick.X},{_config.DailyShopFreeItemClick.Y}）。");
        window = await _screen.ClickAsync(
            window, _config.DailyShopFreeItemClick, "零元商品", cancellationToken);
        await Task.Delay(AfterItemClickDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        return await ExchangeThenOkAsync(window, "零元购", cancellationToken);
    }

    private async Task<(GameWindow Window, ConfigPoint? TicketClick, bool Bought)> TryBuyTicket2500Async(
        GameWindow window,
        ConfigPoint? knownClick,
        CancellationToken cancellationToken)
    {
        ConfigPoint click;
        if (knownClick is { } reuse)
        {
            click = reuse;
            _log($"每日商店：复用左下角 2500 点击（{click.X},{click.Y}）。");
        }
        else
        {
            _log($"每日商店：等待识别「2500」票价，最多 {RecognizeTimeoutMs / 1000} 秒。");
            TemplateProbeResult ticket = await _screen.WaitForProbeAsync(
                window,
                _ticketMatcher,
                _config.DailyShopTicketTopLeft,
                _config.DailyShopTicketSize,
                cancellationToken,
                timeoutMs: RecognizeTimeoutMs,
                matchThreshold: TicketThreshold);
            if (!ticket.IsMatch)
            {
                _log($"每日商店：未识别到「2500」票价（最高 {ticket.Score:F4}）。");
                return (window, null, false);
            }

            // 点价格条中心（实机验证可弹出交換）。
            click = new ConfigPoint(
                (int)Math.Round(ticket.Center.X),
                (int)Math.Round(ticket.Center.Y));
            _log($"每日商店：已识别「2500」（{ticket.Score:F4}），点击中心（{click.X},{click.Y}）。");
        }

        window = await _screen.ClickAsync(window, click, "2500票券", cancellationToken);
        await Task.Delay(AfterItemClickDelayMs, cancellationToken);
        window = _screen.Refresh(window);
        window = await ExchangeThenOkAsync(window, "2500票券", cancellationToken);
        return (window, click, true);
    }

    private async Task<GameWindow> ExchangeThenOkAsync(
        GameWindow window, string label, CancellationToken cancellationToken)
    {
        _log($"每日商店：[{label}] 等待识别「交換」，最多 {RecognizeTimeoutMs / 1000} 秒。");
        TemplateProbeResult exchange = await _screen.WaitForProbeAsync(
            window,
            _exchangeMatcher,
            _config.DailyShopExchangeTopLeft,
            _config.DailyShopExchangeSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: ExchangeThreshold);
        if (!exchange.IsMatch)
            throw new InvalidOperationException(
                $"[{label}] 未识别到「交換」（最高 {exchange.Score:F4}）。每日商店已停止。");

        _log($"每日商店：[{label}] 已识别「交換」（{exchange.Score:F4}），点击匹配中心。");
        await _screen.ClickProbeAsync(window, exchange, "交換", cancellationToken, settleDelayMs: 700);
        window = _screen.Refresh(window);

        _log($"每日商店：[{label}] 等待识别 OK，最多 {RecognizeTimeoutMs / 1000} 秒。");
        TemplateProbeResult ok = await _screen.WaitForProbeAsync(
            window,
            _okMatcher,
            _config.DailyShopOkTopLeft,
            _config.DailyShopOkSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: OkThreshold);
        if (!ok.IsMatch)
            throw new InvalidOperationException(
                $"[{label}] 未识别到 OK（最高 {ok.Score:F4}）。每日商店已停止。");

        _log($"每日商店：[{label}] 已识别 OK（{ok.Score:F4}），点击匹配中心。");
        await _screen.ClickProbeAsync(window, ok, "OK", cancellationToken, settleDelayMs: AfterOkDelayMs);
        return _screen.Refresh(window);
    }
}
