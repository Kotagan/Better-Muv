using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 每日商店：主页 → 商店 → 交換所 → 100%OFF（可跳过）
/// → 可选：切左侧「サークル」按配置购买（先于金币价，避免滚轮弄脏页面）
/// → 切回定期交換 → 滚轮找金币价签（2,500 / 3,000）→ 交換 → OK（尽量买满 2 次）
/// → 回主页。
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
    /// <summary>社团確認「交換」模板偶发偏低；金币价弹窗可到 0.95+。</summary>
    private const double ExchangeThreshold = 0.50;
    private const double HallThreshold = 0.68;
    private const double TicketThreshold = 0.65;
    private const double OkThreshold = 0.65;
    /// <summary>未选中「サークル」页签模板分常 0.40~0.50。</summary>
    private const double CircleTabThreshold = 0.42;
    private const int TargetGoldBuys = 2;
    /// <summary>左侧页签整条搜索（定期交換/ガチャ/サークル…）。</summary>
    private static readonly ConfigPoint CircleTabSearchTopLeft = new(0, 260);
    private static readonly ConfigSize CircleTabSearchSize = new(280, 560);
    /// <summary>確認弹窗「交換」加高搜索（社团粉钮中心约 y=975）。</summary>
    private static readonly ConfigPoint ExchangeSearchTopLeft = new(700, 650);
    private static readonly ConfigSize ExchangeSearchSize = new(800, 400);
    /// <summary>社团確認弹窗粉钮「交換」固定点（1080p，截图像素标定）。</summary>
    private static readonly ConfigPoint CircleExchangeClick = new(1150, 975);
    /// <summary>確認粉钮中心约 y=975；y&lt;900 的命中多为误匹配。</summary>
    private const double ExchangeMinLogicalY = 900;

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

        // 社团商店先做：金币价滚轮/点签容易弄脏页面，导致サークル格点击无弹窗。
        bool circleConfigured = _config.CircleShopEnabled && _config.CirclePurchases.HasAny();
        if (circleConfigured)
        {
            _log("每日商店：进入社团商店（サークル）。");
            window = await BuyCircleShopAsync(window, cancellationToken);
            window = await DismissShopConfirmAsync(window, cancellationToken);
            _log("每日商店：切回「定期交換」继续金币价。");
            await _screen.ClickAsync(window, RegularExchangeTabClick, "定期交換页签", cancellationToken);
            await Task.Delay(AfterTabDelayMs, cancellationToken);
            window = _screen.Refresh(window);
        }

        // 金币价商品在列表偏下；先下滚再找，避免停在宝石价行上。
        int goldBought = 0;
        ConfigPoint? ticketClick = null;
        int maxScrolls = Math.Max(1, _config.DailyShopTicketScrollAttempts);
        int notches = _config.DailyShopScrollWheelNotches;
        if (notches >= 0)
            notches = -Math.Max(6, notches == 0 ? 6 : notches);
        bool stopGoldHunt = false;

        for (int scroll = 1; scroll <= maxScrolls && goldBought < TargetGoldBuys && !stopGoldHunt; scroll++)
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

            while (goldBought < TargetGoldBuys && !stopGoldHunt)
            {
                _log($"每日商店：购买金币价商品（第 {goldBought + 1}/{TargetGoldBuys} 次）。");
                (window, ticketClick, bool bought) =
                    await TryBuyGoldTicketAsync(window, ticketClick, cancellationToken);
                if (!bought)
                {
                    // ticketClick 非空 = 已点到价签但无「交換」（已购等），不必再滚。
                    if (ticketClick is not null)
                        stopGoldHunt = true;
                    break;
                }

                goldBought++;
                ticketClick = null;
            }
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

    /// <summary>確認弹窗左侧「閉じる」（与右侧「交換」同高，约 y=975）。</summary>
    private static readonly ConfigPoint ShopConfirmCloseClick = new(780, 975);
    /// <summary>交換所左侧第一项「定期交換」（1080p）。</summary>
    private static readonly ConfigPoint RegularExchangeTabClick = new(105, 280);

    private async Task<GameWindow> DismissShopConfirmAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        // 有粉钮「交換」或弱匹配时，优先关弹层，避免挡住切页。
        TemplateProbeResult exchange = await _screen.ProbeAsync(
            window, _exchangeMatcher,
            ExchangeSearchTopLeft, ExchangeSearchSize,
            cancellationToken, 0.40);
        CaptureGeometry geometry = _screen.Geometry(window);
        double minY = ExchangeMinLogicalY * Math.Max(1.0, geometry.ScaleY);
        bool likelyDialog = exchange.Score >= 0.55
            || (exchange.Score >= 0.40 && exchange.Center.Y >= minY);
        if (!likelyDialog)
            return window;

        _log($"每日商店：检测到可能残留的確認层（交換 {exchange.Score:F3}），点「閉じる」。");
        await _screen.ClickAsync(window, ShopConfirmCloseClick, "閉じる(商店確認)", cancellationToken);
        await Task.Delay(400, cancellationToken);
        return _screen.Refresh(window);
    }

    private async Task<GameWindow> EnsureStillInExchangeHallAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult hall = await _screen.ProbeAsync(
            window,
            _hallMatcher,
            _config.DailyShopExchangeHallTopLeft,
            _config.DailyShopExchangeHallSize,
            cancellationToken,
            HallThreshold);
        if (hall.IsMatch)
            return window;

        _log($"每日商店：已不在交換所（标记 {hall.Score:F3}），重新点击入口。");
        return await ClickExchangeHallAsync(window, cancellationToken);
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

        (window, bool ok) = await TryExchangeThenOkAsync(window, "零元购", cancellationToken);
        if (!ok)
            _log("每日商店：零元购未出「交換」，跳过。");
        return window;
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
        // 当日已买过时点价签常无「交換」；勿抛死，留给后续社团商店。
        (window, bool ok) = await TryExchangeThenOkAsync(window, "金币价商品", cancellationToken);
        if (!ok)
        {
            _log("每日商店：金币价商品未出「交換」（可能已购或点偏），本轮不再追买金币价。");
            window = await DismissShopConfirmAsync(window, cancellationToken);
            // 保留 click 非空，外层据此停止继续滚轮。
            return (window, click, false);
        }

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
        if (!await EnsureCircleTabSelectedAsync(window, cancellationToken))
        {
            _log("每日商店：未能确认已切到「サークル」页签，跳过社团商店购买。");
            return window;
        }

        window = _screen.Refresh(window);
        int[] plan = _config.CirclePurchases.GetSlots();
        _log($"每日商店：社团商店计划 {CircleShopCatalog.FormatPlan(plan)}。");
        IReadOnlyList<ConfigPoint> buttons = _config.CircleShopBuyButtons;
        bool tabRetried = false;
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
                await Task.Delay(Math.Max(AfterItemClickDelayMs, 700), cancellationToken);
                window = _screen.Refresh(window);

                (window, bool ok) = await TryExchangeThenOkAsync(window, $"社团-{name}", cancellationToken);
                if (ok)
                {
                    bought++;
                    continue;
                }

                // 常见原因：页签没切过去，或该格已售罄/置灰。先重切一次页签再试一发。
                if (!tabRetried)
                {
                    tabRetried = true;
                    _log($"每日商店：社团商店 {name} 未出「交換」，重切サークル页签后再试。");
                    if (await EnsureCircleTabSelectedAsync(window, cancellationToken))
                    {
                        window = _screen.Refresh(window);
                        await _screen.ClickAsync(window, buttons[slot], $"社团商店-{name}(页签后)", cancellationToken);
                        await Task.Delay(AfterItemClickDelayMs, cancellationToken);
                        window = _screen.Refresh(window);
                        (window, ok) = await TryExchangeThenOkAsync(window, $"社团-{name}", cancellationToken);
                        if (ok)
                        {
                            bought++;
                            continue;
                        }
                    }
                }

                _log($"每日商店：社团商店 {name} 无「交換」弹层（可能售罄或未在サークル页），跳过该商品。");
                break;
            }

            _log($"每日商店：社团商店 {name} 完成 {bought} 次。");
        }

        return window;
    }

    /// <summary>
    /// 切到交換所左侧第三项「サークル」（上为定期交換、ガチャ）。
    /// 固定点 (105,450) 已验证可切中；模板未选中时常只有 ~0.45。
    /// </summary>
    private async Task<bool> EnsureCircleTabSelectedAsync(GameWindow window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        window = _screen.Refresh(window);

        TemplateProbeResult tab = await ProbeCircleTabAsync(window, cancellationToken, CircleTabThreshold);
        // 命中中心若偏到「ガチャ」(约 y=330)，改用固定サークル点。
        bool tabLooksLikeGacha = tab.IsMatch && tab.Center.Y < 380 * Math.Max(1.0, _screen.Geometry(window).ScaleY);
        if (tab.IsMatch && !tabLooksLikeGacha)
        {
            _log($"每日商店：识别「サークル」页签（{tab.Score:F4}），点击。");
            await _screen.ClickProbeAsync(window, tab, "サークル页签", cancellationToken, settleDelayMs: 200);
        }
        else
        {
            // 固定点 (105,450) 已验证可切中；配置点优先。
            ConfigPoint point = _config.DailyShopCircleTabClick.X > 0
                ? _config.DailyShopCircleTabClick
                : new(105, 450);
            _log($"每日商店：切「サークル」页签固定点击（{point.X},{point.Y}"
                + $"{(tab.IsMatch ? $"，模板 {tab.Score:F3} 疑似偏ガチャ" : $"，模板最高 {tab.Score:F3}")}）。");
            await _screen.ClickAsync(window, point, "サークル页签", cancellationToken);
        }

        await Task.Delay(AfterTabDelayMs, cancellationToken);
        // 固定点路径实机已验证；模板选中态分仍可能 <0.5，点过后即视为已切页。
        _log("每日商店：已点击「サークル」页签，开始按配置购买。");
        return true;
    }

    private async Task<TemplateProbeResult> ProbeCircleTabAsync(
        GameWindow window, CancellationToken cancellationToken, double threshold)
    {
        TemplateProbeResult wide = await _screen.ProbeAsync(
            window, _circleTabMatcher,
            CircleTabSearchTopLeft, CircleTabSearchSize,
            cancellationToken, threshold);
        if (wide.IsMatch)
            return wide;

        TemplateProbeResult configured = await _screen.ProbeAsync(
            window, _circleTabMatcher,
            _config.DailyShopCircleTabTopLeft, _config.DailyShopCircleTabSize,
            cancellationToken, threshold);
        return configured.Score >= wide.Score ? configured : wide;
    }

    /// <summary>与 ExchangeThenOk 相同流程，失败时不抛（社团售罄/未切页用）。</summary>
    private async Task<(GameWindow Window, bool Ok)> TryExchangeThenOkAsync(
        GameWindow window, string label, CancellationToken cancellationToken)
    {
        bool circle = label.StartsWith("社团", StringComparison.Ordinal);
        if (circle)
        {
            // 等確認弹窗粉钮出现（真实命中约 0.98 @ y≈975）；勿点 y≈850 的误匹配。
            _log($"每日商店：[{label}] 等待社团「交換」粉钮，最多 {ExchangeTimeoutMs / 1000} 秒。");
            TemplateProbeResult exchange = await WaitForExchangeButtonAsync(
                window, cancellationToken, minScore: 0.70);
            if (exchange.IsMatch)
            {
                _log($"每日商店：[{label}] 已识别「交換」（{exchange.Score:F4} @y={exchange.Center.Y:F0}），点击。");
                await _screen.ClickProbeAsync(window, exchange, "交換", cancellationToken, settleDelayMs: 500);
            }
            else if (exchange.Score >= 0.55)
            {
                // 有弹层痕迹但未达 0.70：固定点仍可能点中粉钮。
                _log($"每日商店：[{label}] 粉钮偏弱（最高 {exchange.Score:F4}），改点固定"
                    + $"（{CircleExchangeClick.X},{CircleExchangeClick.Y}）。");
                await _screen.ClickAsync(window, CircleExchangeClick, "交換(社团固定)", cancellationToken);
                await Task.Delay(500, cancellationToken);
            }
            else
            {
                _log($"每日商店：[{label}] 未出现確認弹窗（最高 {exchange.Score:F4}）。");
                return (window, false);
            }

            window = _screen.Refresh(window);
        }
        else
        {
            _log($"每日商店：[{label}] 等待识别「交換」，最多 {ExchangeTimeoutMs / 1000} 秒。");
            TemplateProbeResult exchange = await WaitForExchangeButtonAsync(window, cancellationToken);
            if (!exchange.IsMatch)
            {
                _log($"每日商店：[{label}] 未识别到「交換」（最高 {exchange.Score:F4}）。");
                return (window, false);
            }

            _log($"每日商店：[{label}] 已识别「交換」（{exchange.Score:F4}），点击匹配中心。");
            await _screen.ClickProbeAsync(window, exchange, "交換", cancellationToken, settleDelayMs: 500);
            window = _screen.Refresh(window);
        }

        _log($"每日商店：[{label}] 等待识别 OK，最多 {ExchangeTimeoutMs / 1000} 秒。");
        TemplateProbeResult ok = await WaitForOkButtonAsync(window, cancellationToken);
        if (!ok.IsMatch)
        {
            _log($"每日商店：[{label}] 未识别到 OK（最高 {ok.Score:F4}）。");
            return (window, false);
        }

        _log($"每日商店：[{label}] 已识别 OK（{ok.Score:F4}），点击匹配中心。");
        await _screen.ClickProbeAsync(window, ok, "OK", cancellationToken, settleDelayMs: AfterOkDelayMs);
        return (_screen.Refresh(window), true);
    }

    private async Task<TemplateProbeResult> WaitForOkButtonAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ExchangeTimeoutMs);
        TemplateProbeResult best = TemplateProbes.Empty;
        ConfigPoint okSearchTopLeft = new(700, 850);
        ConfigSize okSearchSize = new(560, 220);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            foreach ((ConfigPoint topLeft, ConfigSize size) in new[]
                     {
                         (okSearchTopLeft, okSearchSize),
                         (_config.DailyShopOkTopLeft, _config.DailyShopOkSize)
                     })
            {
                TemplateProbeResult probe = await _screen.ProbeAsync(
                    window, _okMatcher, topLeft, size, cancellationToken, OkThreshold);
                if (probe.Score > best.Score)
                    best = probe;
                if (probe.IsMatch)
                    return probe;
            }

            await Task.Delay(200, cancellationToken);
        }

        return best;
    }

    private async Task<TemplateProbeResult> WaitForExchangeButtonAsync(
        GameWindow window,
        CancellationToken cancellationToken,
        double? minScore = null)
    {
        double threshold = minScore ?? ExchangeThreshold;
        var deadline = DateTime.UtcNow.AddMilliseconds(ExchangeTimeoutMs);
        TemplateProbeResult best = TemplateProbes.Empty;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);
            CaptureGeometry geometry = _screen.Geometry(window);
            double minScreenY = ExchangeMinLogicalY * Math.Max(1.0, geometry.ScaleY);

            foreach ((ConfigPoint topLeft, ConfigSize size) in new[]
                     {
                         (_config.DailyShopExchangeTopLeft, _config.DailyShopExchangeSize),
                         (ExchangeSearchTopLeft, ExchangeSearchSize)
                     })
            {
                TemplateProbeResult probe = await _screen.ProbeAsync(
                    window, _exchangeMatcher, topLeft, size, cancellationToken, threshold);
                if (probe.Center.Y < minScreenY)
                {
                    if (probe.Score > best.Score)
                        best = new TemplateProbeResult(false, probe.Score, probe.Center);
                    continue;
                }

                var gated = new TemplateProbeResult(probe.Score >= threshold, probe.Score, probe.Center);
                if (gated.Score > best.Score)
                    best = gated;
                if (gated.IsMatch)
                    return gated;
            }

            await Task.Delay(200, cancellationToken);
        }

        return best;
    }

    private async Task<GameWindow> ExchangeThenOkAsync(
        GameWindow window, string label, CancellationToken cancellationToken)
    {
        (window, bool ok) = await TryExchangeThenOkAsync(window, label, cancellationToken);
        if (!ok)
            throw new InvalidOperationException($"[{label}] 交換/OK 未完成。每日商店已停止。");
        return window;
    }
}
