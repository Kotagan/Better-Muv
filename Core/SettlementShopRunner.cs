using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterMuv.Services;

namespace BetterMuv.Core;

public sealed class SettlementShopRunner
{
    private enum MultiplierState
    {
        X1,
        X10,
        Max
    }

    private enum BuySlotVisual
    {
        Available,
        DailyDark,
        AtLimit
    }

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly TemplateMatcher _settlementMatcher;
    private readonly TemplateMatcher _lvMaxMatcher;
    private readonly TemplateMatcher _buyDisabledMatcher;
    private readonly TemplateMatcher _greenDoneMatcher;
    private readonly TemplateMatcher _confirmMatcher;
    private readonly TemplateMatcher _multiplierX1Matcher;
    private readonly TemplateMatcher _multiplierX10Matcher;
    private readonly TemplateMatcher _multiplierMaxMatcher;
    private readonly Action<string> _log;
    private bool _sessionBuyDone;

    public SettlementShopRunner(
        AutomationConfig config,
        ScreenAutomation screen,
        string templateDirectory,
        Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _settlementMatcher = TemplateAssets.Load(templateDirectory, "settlement-complete.png");
        _lvMaxMatcher = TemplateAssets.Load(templateDirectory, "shop-orange-limit.png");
        _buyDisabledMatcher = TemplateAssets.Load(templateDirectory, "shop-daily-dark.png");
        _greenDoneMatcher = TemplateAssets.Load(templateDirectory, "shop-green-done.png");
        _confirmMatcher = TemplateAssets.Load(templateDirectory, "settlement-confirm.png");
        _multiplierX1Matcher = TemplateAssets.Load(templateDirectory, "multiplier-x1.png");
        _multiplierX10Matcher = TemplateAssets.Load(templateDirectory, "multiplier-x10.png");
        _multiplierMaxMatcher = TemplateAssets.Load(templateDirectory, "multiplier-max.png");
    }

    public TemplateMatcher SettlementMatcher => _settlementMatcher;

    public async Task<bool> IsSettlementVisibleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        // 与迷宫探测一致：右下角放大 ROI，避免非 16:9 裁切漏检「完了」。
        var topLeft = new ConfigPoint(
            Math.Max(0, _config.SettlementSearchTopLeft.X - 40),
            Math.Max(0, _config.SettlementSearchTopLeft.Y - 40));
        var size = new ConfigSize(
            Math.Max(_config.SettlementSearchSize.Width + 80, 360),
            Math.Max(_config.SettlementSearchSize.Height + 80, 180));
        TemplateProbeResult probe = await _screen.ProbeAsync(
            window, _settlementMatcher, topLeft, size, cancellationToken, matchThreshold: 0.48);
        return probe.IsMatch;
    }

    public async Task<bool> RunAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("识别到迷宫结算界面，开始商店购买（完了暂不点击）。");
        var totalTimer = Stopwatch.StartNew();
        const int ShopBudgetMs = 90000;
        _sessionBuyDone = false;

        try
        {
            SettlementPurchases purchases = _config.SettlementPurchases;

            if (purchases.Daily.HasAny())
            {
                DateTime now = DateTime.Now;
                if (DailyShopSchedule.QuotaFilledThisShopDay(_config.LastDailyShopDay, now))
                {
                    _log($"日常已买够配置数量，等到 {DailyShopSchedule.NextReset(now):MM-dd HH:mm}（每天 {DailyShopSchedule.ResetHour} 点）刷新后再买。");
                }
                else
                {
                    await RunDailyAsync(window, purchases.Daily, cancellationToken);
                    if (!_sessionBuyDone)
                    {
                        _config.LastDailyShopDay = DailyShopSchedule.CurrentShopDayKey(now);
                        ConfigStore.Save(_config);
                        _log($"日常已按配置买够，等到 {DailyShopSchedule.NextReset(now):MM-dd HH:mm} 刷新后再买。");
                    }
                }
            }

            if (!_sessionBuyDone && totalTimer.ElapsedMilliseconds < ShopBudgetMs && purchases.Equipment.HasAny())
                await RunEquipmentAsync(window, purchases.Equipment, cancellationToken);
            if (!_sessionBuyDone && totalTimer.ElapsedMilliseconds < ShopBudgetMs && purchases.Excavation.HasAny())
                await RunExcavationAsync(window, purchases.Excavation, cancellationToken);
            if (!_sessionBuyDone && totalTimer.ElapsedMilliseconds < ShopBudgetMs && purchases.Artifactor.HasAny())
                await RunArtifactorAsync(window, purchases.Artifactor, cancellationToken);

            if (_sessionBuyDone)
                _log("检测到绿色「强化素材不足」，本次商店购买结束。");
            else if (totalTimer.ElapsedMilliseconds >= ShopBudgetMs)
                _log($"商店购买超时（{ShopBudgetMs / 1000}s），跳过剩余项，直接点完了。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"商店购买异常：{ex.Message}，停止购买并保留当前页面，避免在错误分类购买。");
            return false;
        }

        return await LeaveSettlementAsync(window, cancellationToken);
    }

    private async Task<bool> LeaveSettlementAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("商店购买流程结束，点击完了离开。");
        var leaveTimer = Stopwatch.StartNew();
        int attempts = 0;
        while (leaveTimer.ElapsedMilliseconds < 25000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsSettlementVisibleAsync(window, cancellationToken))
            {
                // 可能已弹出余矿确认：再处理一次确认。
                await HandleLeftoverConfirmAsync(window, cancellationToken);
                if (!await IsSettlementVisibleAsync(window, cancellationToken))
                {
                    _log(attempts == 0
                        ? "结算界面已自行消失。"
                        : $"已离开结算界面（完了点击 {attempts} 次）。");
                    return true;
                }
            }

            attempts++;
            window = await _screen.ClickAsync(
                window, _config.SettlementTopLeft, $"结算完了#{attempts}", cancellationToken);
            await Task.Delay(700, cancellationToken);
            await HandleLeftoverConfirmAsync(window, cancellationToken);
        }

        // 最后兜底：坐标点确认 OK，再点一次完了。
        _log("结算离开超时，兜底点击余矿确认 OK 与完了。");
        await _screen.ClickAsync(window, _config.SettlementConfirmOk, "结算余矿确认(兜底)", cancellationToken);
        await Task.Delay(600, cancellationToken);
        await _screen.ClickAsync(window, _config.SettlementTopLeft, "结算完了(兜底)", cancellationToken);
        await Task.Delay(800, cancellationToken);
        bool left = !await IsSettlementVisibleAsync(window, cancellationToken);
        _log(left ? "兜底后已离开结算。" : $"结算完了点击超时（{attempts} 次），界面仍在。");
        return left;
    }

    private async Task RunDailyAsync(
        GameWindow window, DailySettlementPurchases daily, CancellationToken cancellationToken)
    {
        window = await SelectTabAsync(window, _config.SettlementCategoryDaily, "结算大类：日常", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        if (!await BuySubcategorySlotsAsync(window, "daily", "skillBook1", 0, daily.SkillBook1, cancellationToken))
            return;
        if (!await BuySubcategorySlotsAsync(window, "daily", "skillBook2", 1, daily.SkillBook2, cancellationToken))
            return;
        if (!await BuySubcategorySlotsAsync(window, "daily", "disk", 2, daily.Disk, cancellationToken))
            return;
        await BuySubcategorySlotsAsync(window, "daily", "unit", 3, daily.Unit, cancellationToken);
    }

    private async Task RunEquipmentAsync(
        GameWindow window, EquipmentSettlementPurchases equipment, CancellationToken cancellationToken)
    {
        window = await SelectTabAsync(window, _config.SettlementCategoryEquipment, "结算大类：装备", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        if (!await BuySubcategorySlotsAsync(window, "equipment", "physics", 0, equipment.Physics, cancellationToken))
            return;
        if (!await BuySubcategorySlotsAsync(window, "equipment", "en", 1, equipment.En, cancellationToken))
            return;
        await BuySubcategorySlotsAsync(window, "equipment", "agility", 2, equipment.Agility, cancellationToken);
    }

    private async Task RunExcavationAsync(
        GameWindow window, ExcavationSettlementPurchases excavation, CancellationToken cancellationToken)
    {
        window = await SelectTabAsync(window, _config.SettlementCategoryExcavation, "结算大类：挖掘", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        await BuySlotsAsync(window, "excavation", null, excavation.ToSlots(), cancellationToken);
    }

    private async Task RunArtifactorAsync(
        GameWindow window, ArtifactorSettlementPurchases artifactor, CancellationToken cancellationToken)
    {
        window = await SelectTabAsync(window, _config.SettlementCategoryArtifactor, "结算大类：Artifactor", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        if (!await BuySubcategorySlotsAsync(window, "artifactor", "physics", 0, artifactor.Physics, cancellationToken))
            return;
        if (!await BuySubcategorySlotsAsync(window, "artifactor", "en", 1, artifactor.En, cancellationToken))
            return;
        await BuySubcategorySlotsAsync(window, "artifactor", "agility", 2, artifactor.Agility, cancellationToken);
    }

    private async Task<bool> BuySubcategorySlotsAsync(
        GameWindow window,
        string category,
        string subcategoryKey,
        int tabIndex,
        int[] quantities,
        CancellationToken cancellationToken)
    {
        if (_sessionBuyDone || !quantities.Any(v => v != 0))
            return !_sessionBuyDone;

        string scope = $"{SettlementShopCatalog.CategoryDisplayName(category)}/{subcategoryKey}";
        ConfigPoint tab = _config.SettlementSubcategoryTabs[tabIndex];
        window = await SelectTabAsync(
            window, tab, $"结算小类：{scope}", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        return await BuySlotsAsync(window, category, subcategoryKey, quantities, cancellationToken);
    }

    // 分类选中态是大面积蓝底，未选中态只有蓝字/细边框。
    // 只检查点击点附近的内部小块，不使用整个商品页作为模板。
    private async Task<GameWindow> SelectTabAsync(
        GameWindow window, ConfigPoint tab, string name, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            window = _screen.Refresh(window);
            await _screen.ClickAsync(window, tab, $"{name}（{attempt}/3）", cancellationToken);
            await Task.Delay(450, cancellationToken);
            window = _screen.Refresh(window);
            double selected = SelectedBlueFraction(window, tab);
            _log($"{name}：选中蓝底占比 {selected:P0}。");
            if (selected < 0.45)
                continue;
            await Task.Delay(180, cancellationToken);
            if (SelectedBlueFraction(_screen.Refresh(window), tab) >= 0.45)
                return window;
        }
        throw new InvalidOperationException($"{name}未确认选中（已重试 3 次）");
    }

    private double SelectedBlueFraction(GameWindow window, ConfigPoint tab)
    {
        var region = _screen.CaptureRegion(window,
            new ConfigPoint(Math.Max(0, tab.X - 24), Math.Max(0, tab.Y - 10)), new ConfigSize(48, 20));
        var bitmap = new FormatConvertedBitmap(region.Image, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        int blue = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 180 && pixels[i + 1] > 90 && pixels[i + 2] < 100 &&
                pixels[i] - pixels[i + 2] > 90)
                blue++;
        return blue / (double)(bitmap.PixelWidth * bitmap.PixelHeight);
    }

    /// <summary>购买一组格子。返回 false 表示绿色提示出现，本次商店应结束。</summary>
    private async Task<bool> BuySlotsAsync(
        GameWindow window,
        string category,
        string? subcategory,
        int[] quantities,
        CancellationToken cancellationToken)
    {
        string scope = subcategory is null
            ? SettlementShopCatalog.CategoryDisplayName(category)
            : $"{SettlementShopCatalog.CategoryDisplayName(category)}/{subcategory}";

        for (int i = 0; i < quantities.Length && i < _config.SettlementBuyButtons.Count; i++)
        {
            if (_sessionBuyDone)
                return false;

            int quantity = quantities[i];
            if (quantity == 0)
                continue;

            ConfigPoint button = _config.SettlementBuyButtons[i];
            BuySlotVisual visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
            if (visual == BuySlotVisual.AtLimit)
            {
                DisableSlotInConfig(category, subcategory, quantities, i, scope);
                continue;
            }

            if (visual == BuySlotVisual.DailyDark)
            {
                _log($"{scope} 第 {i + 1} 格为每日暗色，无法再买，跳过。");
                continue;
            }

            if (quantity < 0)
            {
                if (!await EnsureMultiplierAsync(window, MultiplierState.Max, cancellationToken))
                {
                    _log($"{scope} 第 {i + 1} 格未确认倍率 MAX，跳过购买。");
                    continue;
                }

                visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                if (visual == BuySlotVisual.AtLimit)
                {
                    DisableSlotInConfig(category, subcategory, quantities, i, scope);
                    continue;
                }

                if (visual == BuySlotVisual.DailyDark)
                {
                    _log($"{scope} 第 {i + 1} 格为每日暗色，无法再买，跳过。");
                    continue;
                }

                // -1：最多点 2 次，避免禁用/满级模板漏检时死循环。
                for (int n = 0; n < 2; n++)
                {
                    if (_sessionBuyDone)
                        return false;
                    if (!await EnsureMultiplierAsync(window, MultiplierState.Max, cancellationToken))
                    {
                        _log($"{scope} 第 {i + 1} 格购买前倍率 MAX 丢失，停止该格。");
                        break;
                    }

                    visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                    if (visual == BuySlotVisual.AtLimit)
                    {
                        DisableSlotInConfig(category, subcategory, quantities, i, scope);
                        break;
                    }

                    if (visual == BuySlotVisual.DailyDark)
                        break;

                    window = await _screen.ClickAsync(
                        window, button, $"{scope} 第 {i + 1} 格全买({n + 1}/2)", cancellationToken);
                    await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                    if (await DetectGreenDoneAsync(window, cancellationToken))
                        return false;
                }
                continue;
            }

            // 单次购买上限，防止倍率识别失败时连点上百次卡死。
            int capped = Math.Min(quantity, 30);
            if (capped != quantity)
                _log($"{scope} 第 {i + 1} 格配置 {quantity}，单次封顶按 {capped} 执行。");

            int tens = capped / 10;
            int ones = capped % 10;
            if (tens > 0)
            {
                if (!await EnsureMultiplierAsync(window, MultiplierState.X10, cancellationToken))
                {
                    _log($"{scope} 第 {i + 1} 格未确认倍率 ×10，跳过 ×10 段。");
                }
                else
                {
                    for (int n = 0; n < tens; n++)
                    {
                        if (_sessionBuyDone)
                            return false;
                        if (!await EnsureMultiplierAsync(window, MultiplierState.X10, cancellationToken))
                        {
                            _log($"{scope} 第 {i + 1} 格购买前倍率 ×10 丢失，停止 ×10 段。");
                            break;
                        }

                        visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                        if (visual == BuySlotVisual.AtLimit)
                        {
                            DisableSlotInConfig(category, subcategory, quantities, i, scope);
                            break;
                        }

                        if (visual == BuySlotVisual.DailyDark)
                            break;

                        window = await _screen.ClickAsync(
                            window, button, $"{scope} 第 {i + 1} 格 ×10 ({n + 1}/{tens})", cancellationToken);
                        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                        if (await DetectGreenDoneAsync(window, cancellationToken))
                            return false;
                    }
                }
            }

            if (ones > 0 && !_sessionBuyDone && quantities[i] != 0)
            {
                if (!await EnsureMultiplierAsync(window, MultiplierState.X1, cancellationToken))
                {
                    _log($"{scope} 第 {i + 1} 格未确认倍率 ×1，跳过 ×1 段。");
                }
                else
                {
                    for (int n = 0; n < ones; n++)
                    {
                        if (_sessionBuyDone)
                            return false;
                        if (!await EnsureMultiplierAsync(window, MultiplierState.X1, cancellationToken))
                        {
                            _log($"{scope} 第 {i + 1} 格购买前倍率 ×1 丢失，停止 ×1 段。");
                            break;
                        }

                        visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                        if (visual == BuySlotVisual.AtLimit)
                        {
                            DisableSlotInConfig(category, subcategory, quantities, i, scope);
                            break;
                        }

                        if (visual == BuySlotVisual.DailyDark)
                            break;

                        window = await _screen.ClickAsync(
                            window, button, $"{scope} 第 {i + 1} 格 ×1 ({n + 1}/{ones})", cancellationToken);
                        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                        if (await DetectGreenDoneAsync(window, cancellationToken))
                            return false;
                    }
                }
            }
        }

        return !_sessionBuyDone;
    }

    /// <summary>切换并确认右上角倍率；未到目标就点击切换，确认成功才允许购买。</summary>
    private async Task<bool> EnsureMultiplierAsync(
        GameWindow window, MultiplierState target, CancellationToken cancellationToken)
    {
        MultiplierState? current = await ReadMultiplierAsync(window, cancellationToken);
        if (current == target)
            return true;

        int plannedClicks = current is null ? 3 : ClicksToReach(current.Value, target);
        _log($"右上角倍率当前 {(current is null ? "未知" : Describe(current.Value))}，目标 {Describe(target)}，点击切换。");

        for (int attempt = 0; attempt < 8; attempt++)
        {
            // 已知当前态时，先按循环差点击；未知则每次点一下再读。
            int burst = current is null ? 1 : Math.Max(1, plannedClicks);
            for (int n = 0; n < burst; n++)
            {
                window = await _screen.ClickAsync(
                    window, _config.SettlementMultiplierToggle,
                    $"切换倍率 → {Describe(target)}（轮 {attempt + 1} 点 {n + 1}/{burst}）",
                    cancellationToken);
                await Task.Delay(Math.Max(_config.DetectionPollIntervalMs, 350), cancellationToken);
            }

            current = await ReadMultiplierAsync(window, cancellationToken);
            if (current == target)
            {
                _log($"已确认右上角倍率为 {Describe(target)}。");
                return true;
            }

            plannedClicks = current is null ? 1 : ClicksToReach(current.Value, target);
        }

        _log($"未能切到倍率 {Describe(target)}，当前为 {(current is null ? "未知" : Describe(current.Value))}。");
        return false;
    }

    /// <summary>×1 → ×10 → MAX → ×1 循环，计算最少点击次数。</summary>
    private static int ClicksToReach(MultiplierState current, MultiplierState target)
    {
        if (current == target)
            return 0;
        // 顺序：X1=0, X10=1, Max=2
        int from = current switch
        {
            MultiplierState.X1 => 0,
            MultiplierState.X10 => 1,
            _ => 2
        };
        int to = target switch
        {
            MultiplierState.X1 => 0,
            MultiplierState.X10 => 1,
            _ => 2
        };
        return (to - from + 3) % 3;
    }

    private async Task<MultiplierState?> ReadMultiplierAsync(GameWindow window, CancellationToken cancellationToken)
    {
        // 倍率字小、底色深，默认 0.78 过高；用较低阈值并取最高分。
        const double threshold = 0.50;
        TemplateProbeResult x1 = await _screen.ProbeAsync(
            window, _multiplierX1Matcher, _config.SettlementMultiplierTopLeft,
            _config.SettlementMultiplierSize, cancellationToken, matchThreshold: threshold);
        TemplateProbeResult x10 = await _screen.ProbeAsync(
            window, _multiplierX10Matcher, _config.SettlementMultiplierTopLeft,
            _config.SettlementMultiplierSize, cancellationToken, matchThreshold: threshold);
        TemplateProbeResult max = await _screen.ProbeAsync(
            window, _multiplierMaxMatcher, _config.SettlementMultiplierTopLeft,
            _config.SettlementMultiplierSize, cancellationToken, matchThreshold: threshold);

        var candidates = new List<(MultiplierState State, double Score)>
        {
            (MultiplierState.X1, x1.Score),
            (MultiplierState.X10, x10.Score),
            (MultiplierState.Max, max.Score)
        };
        (MultiplierState State, double Score) best = candidates.OrderByDescending(c => c.Score).First();
        if (best.Score < 0.40)
            return null;
        return best.State;
    }

    private async Task<BuySlotVisual> ReadBuySlotVisualAsync(
        GameWindow window, ConfigPoint button, CancellationToken cancellationToken)
    {
        ConfigPoint inset = _config.SettlementBuyButtonSearchInset;
        ConfigSize size = _config.SettlementBuyButtonSearchSize;
        var topLeft = new ConfigPoint(button.X - inset.X, button.Y - inset.Y);

        TemplateProbeResult lvMax = await _screen.ProbeAsync(
            window, _lvMaxMatcher, topLeft, size, cancellationToken);
        TemplateProbeResult disabled = await _screen.ProbeAsync(
            window, _buyDisabledMatcher, topLeft, size, cancellationToken);

        // 橙色上限优先：暗色按钮也可能局部相似，但 LvMAX 更明确。
        if (lvMax.IsMatch && (!disabled.IsMatch || lvMax.Score >= disabled.Score))
            return BuySlotVisual.AtLimit;
        if (disabled.IsMatch)
            return BuySlotVisual.DailyDark;
        return BuySlotVisual.Available;
    }

    private async Task<bool> DetectGreenDoneAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult tip = await _screen.ProbeAsync(
            window, _greenDoneMatcher,
            _config.SettlementShopTipTopLeft, _config.SettlementShopTipSize,
            cancellationToken, matchThreshold: 0.55);
        if (!tip.IsMatch)
            return false;

        _sessionBuyDone = true;
        _log($"命中绿色提示「强化素材不足」（得分 {tip.Score:F2}），结束本次购买。");
        return true;
    }

    private void DisableSlotInConfig(
        string category, string? subcategory, int[] quantities, int slotIndex, string scope)
    {
        quantities[slotIndex] = 0;
        bool changed = SettlementShopCatalog.DisableSlot(
            _config.SettlementPurchases, category, subcategory, slotIndex);
        if (changed)
            ConfigStore.Save(_config);
        _log($"{scope} 第 {slotIndex + 1} 格橙色到达上限，已关闭对应购买栏位。");
    }

    private async Task HandleLeftoverConfirmAsync(GameWindow window, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 8000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateProbeResult confirm = await _screen.ProbeAsync(
                window, _confirmMatcher, _config.SettlementConfirmTopLeft,
                _config.SettlementConfirmSize, cancellationToken, matchThreshold: 0.55);
            if (confirm.IsMatch)
            {
                ConfigPoint target = _config.SettlementConfirmLeftover
                    ? _config.SettlementConfirmOk
                    : _config.SettlementConfirmCancel;
                string action = _config.SettlementConfirmLeftover ? "确认" : "取消";
                _log($"检测到余矿确认弹窗，点击{action}。");
                await _screen.ClickAsync(window, target, $"结算余矿{action}", cancellationToken);
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                return;
            }

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
    }

    private static string Describe(MultiplierState state) => state switch
    {
        MultiplierState.X1 => "×1",
        MultiplierState.X10 => "×10",
        MultiplierState.Max => "MAX",
        _ => state.ToString()
    };
}
