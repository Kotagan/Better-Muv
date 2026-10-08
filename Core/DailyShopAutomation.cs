using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日商店：主页 → 商店 → 交換所 → 100%OFF（可跳过）→ 交換 → OK
/// → 滚轮下拉 → 左下角 2500 → 交換 → OK（×2；首次未识别则回主页结束）→ 回主页。
/// </summary>
public sealed class DailyShopAutomation
{
    private const int AfterHomeDelayMs = 400;
    private const int AfterHallOpenDelayMs = 250;
    private const int AfterItemClickDelayMs = 450;
    private const int AfterScrollDelayMs = 400;
    private const int AfterOkDelayMs = 450;
    private const int RecognizeTimeoutMs = 5000;
    private const int ExchangeTimeoutMs = 4000;
    private const double OffThreshold = 0.75;
    private const double ExchangeThreshold = 0.70;
    private const double HallThreshold = 0.68;
    private const double TicketThreshold = 0.65;
    private const double OkThreshold = 0.70;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _hallMatcher;
    private readonly TemplateMatcher _hubExchangeMatcher;
    private readonly TemplateMatcher _offMatcher;
    private readonly TemplateMatcher _ticketMatcher;
    private readonly TemplateMatcher _exchangeMatcher;
    private readonly TemplateMatcher _okMatcher;
    /// <summary>LIMITED SHOP 枢纽内「交換所」卡片搜索区（1080p）。</summary>
    private static readonly ConfigPoint HubExchangeTopLeft = new(1200, 620);
    private static readonly ConfigSize HubExchangeSize = new(560, 360);

    public DailyShopAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _hallMatcher = TemplateAssets.Load("daily-shop-exchange-hall.png");
        _hubExchangeMatcher = TemplateAssets.Load("daily-shop-hub-exchange.png");
        _offMatcher = TemplateAssets.Load("daily-shop-100off.png");
        _ticketMatcher = TemplateAssets.Load("daily-shop-ticket-2500.png");
        _exchangeMatcher = TemplateAssets.Load("daily-shop-exchange.png");
        _okMatcher = TemplateAssets.Load("daily-shop-ok.png");
    }

    public async Task<TaskRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"每日商店：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return TaskRunResult.Fail("未能将游戏置于前台");
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"每日商店：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        var home = new HomePresence(_config, _screen, _log);
        (window, bool onHome) = await home.EnsureAsync(window, "每日商店", cancellationToken);
        if (!onHome)
            return TaskRunResult.Fail("未能回到主界面");

        await Task.Delay(AfterHomeDelayMs, cancellationToken);
        window = _screen.Refresh(window);
        // 底栏ショップ → 枢纽「交換所」卡片（模板定位）→ 左页签确认落地。
        (window, bool shopOpened) = await new ShopEntryAccess(_config, _screen, _log).OpenAsync(
            window,
            "每日商店",
            _hallMatcher,
            _config.DailyShopExchangeHallTopLeft,
            _config.DailyShopExchangeHallSize,
            HallThreshold,
            cancellationToken,
            _hubExchangeMatcher,
            HubExchangeTopLeft,
            HubExchangeSize,
            portalThreshold: 0.62);
        if (!shopOpened)
            return TaskRunResult.Fail("未能进入商店");

        // 落地已确认在交換所时跳过重复点击；仅未命中时补点一次。
        window = await ClickExchangeHallAsync(window, cancellationToken);

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

        // 3) 左下角 2500 ×2；首次未识别则直接失败（清弹窗交由失败处理）。
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
                return TaskRunResult.Fail("未识别到 2500 票券");
            }
        }

        _log("每日商店：完成，返回主页。");
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        _log("每日商店：结束。");
        return TaskRunResult.Success();
    }

    private async Task<GameWindow> ClickExchangeHallAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult hall = await _screen.ProbeAsync(
            window,
            _hallMatcher,
            _config.DailyShopExchangeHallTopLeft,
            _config.DailyShopExchangeHallSize,
            cancellationToken,
            HallThreshold);
        if (!hall.IsMatch)
        {
            _log($"每日商店：补充识别「交換所」未命中（最高 {hall.Score:F4}），跳过。");
            return window;
        }

        // OpenAsync 已用同一落地模板确认成功时，再点页签只会空等；直接进入购物流。
        _log($"每日商店：已在交換所（{hall.Score:F4}），跳过重复点击。");
        await Task.Delay(AfterHallOpenDelayMs, cancellationToken);
        return _screen.Refresh(window);
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

        // 点折扣条下方价格区，避免只点到橙色条本身；偏移按当前客户区缩放。
        CaptureGeometry geometry = _screen.Geometry(window);
        var itemScreen = new System.Windows.Point(
            off.Center.X,
            off.Center.Y + geometry.ScaleY * 70);
        _log($"每日商店：已识别「100%OFF」（{off.Score:F4}），点击商品 screen({itemScreen.X:F0},{itemScreen.Y:F0})。");
        await _screen.ClickScreenAsync(window, itemScreen, cancellationToken);
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
            var ticketTopLeft = new ConfigPoint(
                Math.Max(0, _config.DailyShopTicketTopLeft.X - 60),
                Math.Max(0, _config.DailyShopTicketTopLeft.Y - 40));
            var ticketSize = new ConfigSize(
                Math.Max(_config.DailyShopTicketSize.Width + 120, 600),
                Math.Max(_config.DailyShopTicketSize.Height + 80, 360));
            TemplateProbeResult ticket = await _screen.WaitForProbeAsync(
                window,
                _ticketMatcher,
                ticketTopLeft,
                ticketSize,
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
            _log($"每日商店：已识别「2500」（{ticket.Score:F4}），屏幕中心（{click.X},{click.Y}）。");
        }

        // 模板中心已经是屏幕坐标，不能再按 1080p 配置点缩放一次。
        _log($"每日商店：点击 2500 票券屏幕坐标（{click.X},{click.Y}）。");
        await _screen.ClickScreenAsync(
            window, new System.Windows.Point(click.X, click.Y), cancellationToken);
        await Task.Delay(AfterItemClickDelayMs, cancellationToken);
        window = _screen.Refresh(window);
        window = await ExchangeThenOkAsync(window, "2500票券", cancellationToken);
        return (window, click, true);
    }

    private async Task<GameWindow> ExchangeThenOkAsync(
        GameWindow window, string label, CancellationToken cancellationToken)
    {
        _log($"每日商店：[{label}] 等待识别「交換」，最多 {ExchangeTimeoutMs / 1000} 秒。");
        TemplateProbeResult exchange = await _screen.WaitForProbeAsync(
            window,
            _exchangeMatcher,
            _config.DailyShopExchangeTopLeft,
            _config.DailyShopExchangeSize,
            cancellationToken,
            timeoutMs: ExchangeTimeoutMs,
            matchThreshold: ExchangeThreshold);
        if (!exchange.IsMatch)
            throw new InvalidOperationException(
                $"[{label}] 未识别到「交換」（最高 {exchange.Score:F4}）。每日商店已停止。");

        _log($"每日商店：[{label}] 已识别「交換」（{exchange.Score:F4}），点击匹配中心。");
        await _screen.ClickProbeAsync(window, exchange, "交換", cancellationToken, settleDelayMs: 400);
        window = _screen.Refresh(window);

        _log($"每日商店：[{label}] 等待识别 OK，最多 {ExchangeTimeoutMs / 1000} 秒。");
        TemplateProbeResult ok = await _screen.WaitForProbeAsync(
            window,
            _okMatcher,
            _config.DailyShopOkTopLeft,
            _config.DailyShopOkSize,
            cancellationToken,
            timeoutMs: ExchangeTimeoutMs,
            matchThreshold: OkThreshold);
        if (!ok.IsMatch)
            throw new InvalidOperationException(
                $"[{label}] 未识别到 OK（最高 {ok.Score:F4}）。每日商店已停止。");

        _log($"每日商店：[{label}] 已识别 OK（{ok.Score:F4}），点击匹配中心。");
        await _screen.ClickProbeAsync(window, ok, "OK", cancellationToken, settleDelayMs: AfterOkDelayMs);
        return _screen.Refresh(window);
    }
}
