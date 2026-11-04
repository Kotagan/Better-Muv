using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日商店：主页 → 商店 → 交換所 → 100%OFF（可跳过）→ 交換 → OK
/// → 滚轮找金币价签（2,500 / 3,000）→ 交換 → OK（尽量买满 2 次；至少 1 次即算成功）
/// → 可选：切左侧「サークル」按配置购买 → 回主页。
/// </summary>
public sealed class DailyShopAutomation
{
    private const int AfterHomeDelayMs = 400;
    private const int AfterHallOpenDelayMs = 250;
    private const int AfterItemClickDelayMs = 450;
    private const int AfterScrollDelayMs = 550;
    private const int AfterOkDelayMs = 450;
    private const int AfterTabDelayMs = 700;
    private const int RecognizeTimeoutMs = 5000;
    private const int ExchangeTimeoutMs = 4000;
    private const double OffThreshold = 0.75;
    private const double ExchangeThreshold = 0.70;
    private const double HallThreshold = 0.68;
    private const double TicketThreshold = 0.65;
    private const double OkThreshold = 0.70;
    private const double CircleTabThreshold = 0.70;
    private const int TargetGoldBuys = 2;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _hallMatcher;
    private readonly TemplateMatcher _hubExchangeMatcher;
    private readonly TemplateMatcher _offMatcher;
    private readonly TemplateMatcher _ticket2500Matcher;
    private readonly TemplateMatcher _ticket3000Matcher;
    private readonly TemplateMatcher _exchangeMatcher;
    private readonly TemplateMatcher _okMatcher;
    private readonly TemplateMatcher _circleTabMatcher;
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
        _ticket2500Matcher = TemplateAssets.Load("daily-shop-ticket-2500.png");
        _ticket3000Matcher = TemplateAssets.Load("daily-shop-ticket-3000.png");
        _exchangeMatcher = TemplateAssets.Load("daily-shop-exchange.png");
        _okMatcher = TemplateAssets.Load("daily-shop-ok.png");
        _circleTabMatcher = TemplateAssets.Load("daily-shop-tab-circle.png");
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

        window = await ClickExchangeHallAsync(window, cancellationToken);
        window = await TryBuyFreeOffAsync(window, cancellationToken);

        // 金币价商品在列表偏下；先下滚再找，避免停在宝石价行上。
        int goldBought = 0;
        ConfigPoint? ticketClick = null;
        int maxScrolls = Math.Max(1, _config.DailyShopTicketScrollAttempts);
        int notches = _config.DailyShopScrollWheelNotches;
        if (notches >= 0)
            notches = -Math.Max(6, notches == 0 ? 6 : notches);

        for (int scroll = 1; scroll <= maxScrolls && goldBought < TargetGoldBuys; scroll++)
        {
            _log($"每日商店：列表下滚 {notches} 格（{scroll}/{maxScrolls}）。");
            await _screen.WheelAsync(
                window,
                _config.DailyShopScrollPoint,
                notches,
                "商店列表滚轮",
                cancellationToken);
            await Task.Delay(AfterScrollDelayMs, cancellationToken);
            window = _screen.Refresh(window);
            ticketClick = null;

            while (goldBought < TargetGoldBuys)
            {
                _log($"每日商店：购买金币价商品（第 {goldBought + 1}/{TargetGoldBuys} 次）。");
                (window, ticketClick, bool bought) =
                    await TryBuyGoldTicketAsync(window, ticketClick, cancellationToken);
                if (!bought)
                    break;
                goldBought++;
                ticketClick = null;
            }
        }

        bool circleConfigured = _config.CircleShopEnabled && _config.CirclePurchases.HasAny();
        if (circleConfigured)
        {
            _log("每日商店：进入社团商店（サークル）。");
            window = await BuyCircleShopAsync(window, cancellationToken);
        }

        if (goldBought <= 0 && !circleConfigured)
        {
            _log("每日商店：滚动后仍未识别到金币价签，返回主页并结束任务。");
            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
            _log("每日商店：结束。");
            return TaskRunResult.Fail("未识别到金币价签");
        }

        if (goldBought < TargetGoldBuys)
            _log($"每日商店：金币价商品仅买到 {goldBought}/{TargetGoldBuys}，继续结束流程。");

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

    private async Task<(GameWindow Window, ConfigPoint? TicketClick, bool Bought)> TryBuyGoldTicketAsync(
        GameWindow window,
        ConfigPoint? knownClick,
        CancellationToken cancellationToken)
    {
        ConfigPoint click;
        if (knownClick is { } reuse)
        {
            click = reuse;
            _log($"每日商店：复用金币价点击（{click.X},{click.Y}）。");
        }
        else
        {
            _log($"每日商店：等待识别金币价签（2,500/3,000），最多 {RecognizeTimeoutMs / 1000} 秒。");
            (TemplateProbeResult ticket, string label) = await WaitForGoldTicketAsync(window, cancellationToken);
            if (!ticket.IsMatch)
            {
                _log($"每日商店：未识别到金币价签（最高 {ticket.Score:F4}）。");
                return (window, null, false);
            }

            click = new ConfigPoint(
                (int)Math.Round(ticket.Center.X),
                (int)Math.Round(ticket.Center.Y));
            _log($"每日商店：已识别「{label}」（{ticket.Score:F4}），屏幕中心（{click.X},{click.Y}）。");
        }

        _log($"每日商店：点击金币价商品屏幕坐标（{click.X},{click.Y}）。");
        await _screen.ClickScreenAsync(
            window, new System.Windows.Point(click.X, click.Y), cancellationToken);
        await Task.Delay(AfterItemClickDelayMs, cancellationToken);
        window = _screen.Refresh(window);
        window = await ExchangeThenOkAsync(window, "金币价商品", cancellationToken);
        return (window, click, true);
    }

    private async Task<(TemplateProbeResult Probe, string Label)> WaitForGoldTicketAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(RecognizeTimeoutMs);
        TemplateProbeResult best = TemplateProbes.Empty;
        string bestLabel = "金币价";
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            TemplateProbeResult p3000 = await _screen.ProbeAsync(
                window,
                _ticket3000Matcher,
                _config.DailyShopTicketTopLeft,
                _config.DailyShopTicketSize,
                cancellationToken,
                TicketThreshold);
            if (p3000.IsMatch)
                return (p3000, "3,000");

            TemplateProbeResult p2500 = await _screen.ProbeAsync(
                window,
                _ticket2500Matcher,
                _config.DailyShopTicketTopLeft,
                _config.DailyShopTicketSize,
                cancellationToken,
                TicketThreshold);
            if (p2500.IsMatch)
                return (p2500, "2,500");

            if (p3000.Score >= best.Score)
            {
                best = p3000;
                bestLabel = "3,000";
            }
            if (p2500.Score > best.Score)
            {
                best = p2500;
                bestLabel = "2,500";
            }

            await Task.Delay(200, cancellationToken);
        }

        return (best, bestLabel);
    }

    private async Task<GameWindow> BuyCircleShopAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult tab = await _screen.WaitForProbeAsync(
            window,
            _circleTabMatcher,
            _config.DailyShopCircleTabTopLeft,
            _config.DailyShopCircleTabSize,
            cancellationToken,
            timeoutMs: RecognizeTimeoutMs,
            matchThreshold: CircleTabThreshold);
        if (tab.IsMatch)
        {
            _log($"每日商店：已识别「サークル」页签（{tab.Score:F4}），点击。");
            await _screen.ClickProbeAsync(window, tab, "サークル页签", cancellationToken, settleDelayMs: 200);
        }
        else
        {
            _log($"每日商店：未识别「サークル」页签（最高 {tab.Score:F4}），改用固定点击"
                + $"（{_config.DailyShopCircleTabClick.X},{_config.DailyShopCircleTabClick.Y}）。");
            await _screen.ClickAsync(window, _config.DailyShopCircleTabClick, "サークル页签", cancellationToken);
        }

        await Task.Delay(AfterTabDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        int[] plan = _config.CirclePurchases.GetSlots();
        _log($"每日商店：社团商店计划 {CircleShopCatalog.FormatPlan(plan)}。");
        IReadOnlyList<ConfigPoint> buttons = _config.CircleShopBuyButtons;
        for (int slot = 0; slot < plan.Length && slot < buttons.Count; slot++)
        {
            int quantity = plan[slot];
            if (quantity == 0)
                continue;

            string name = slot < CircleShopCatalog.ItemNames.Length
                ? CircleShopCatalog.ItemNames[slot]
                : $"第{slot + 1}格";
            int target = quantity < 0 ? 20 : quantity;
            int bought = 0;
            for (int n = 0; n < target; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _log($"每日商店：社团商店购买 {name}（{n + 1}/{target}）。");
                await _screen.ClickAsync(window, buttons[slot], $"社团商店-{name}", cancellationToken);
                await Task.Delay(AfterItemClickDelayMs, cancellationToken);
                window = _screen.Refresh(window);
                try
                {
                    window = await ExchangeThenOkAsync(window, $"社团-{name}", cancellationToken);
                    bought++;
                }
                catch (InvalidOperationException ex)
                {
                    _log($"每日商店：社团商店 {name} 中止 — {ex.Message}");
                    break;
                }
            }

            _log($"每日商店：社团商店 {name} 完成 {bought} 次。");
        }

        return window;
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
