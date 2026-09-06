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
    /// <summary>本会话已确认的倍率，避免每次全买前重复识别/切换。</summary>
    private MultiplierState? _confirmedMultiplier;

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
        _confirmedMultiplier = null;

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
        if (!await TrySelectCategoryAsync(window, _config.SettlementCategoryDaily, "结算大类：日常", cancellationToken))
            return;
        await Task.Delay(350, cancellationToken);
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
        if (!await TrySelectCategoryAsync(window, _config.SettlementCategoryEquipment, "结算大类：装备", cancellationToken))
            return;
        await Task.Delay(350, cancellationToken);
        if (!await BuySubcategorySlotsAsync(window, "equipment", "physics", 0, equipment.Physics, cancellationToken))
            return;
        if (!await BuySubcategorySlotsAsync(window, "equipment", "en", 1, equipment.En, cancellationToken))
            return;
        await BuySubcategorySlotsAsync(window, "equipment", "agility", 2, equipment.Agility, cancellationToken);
    }

    private async Task RunExcavationAsync(
        GameWindow window, ExcavationSettlementPurchases excavation, CancellationToken cancellationToken)
    {
        if (!await TrySelectCategoryAsync(window, _config.SettlementCategoryExcavation, "结算大类：挖掘", cancellationToken))
            return;
        await Task.Delay(350, cancellationToken);
        await BuySlotsAsync(window, "excavation", null, excavation.ToSlots(), cancellationToken);
    }

    private async Task RunArtifactorAsync(
        GameWindow window, ArtifactorSettlementPurchases artifactor, CancellationToken cancellationToken)
    {
        if (!await TrySelectCategoryAsync(window, _config.SettlementCategoryArtifactor, "结算大类：Artifactor", cancellationToken))
            return;
        await Task.Delay(350, cancellationToken);
        if (!await BuySubcategorySlotsAsync(window, "artifactor", "physics", 0, artifactor.Physics, cancellationToken))
            return;
        if (!await BuySubcategorySlotsAsync(window, "artifactor", "en", 1, artifactor.En, cancellationToken))
            return;
        await BuySubcategorySlotsAsync(window, "artifactor", "agility", 2, artifactor.Agility, cancellationToken);
    }

    private async Task<bool> TrySelectCategoryAsync(
        GameWindow window, ConfigPoint tab, string name, CancellationToken cancellationToken)
    {
        try
        {
            await SelectTabAsync(window, tab, name, cancellationToken);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _log($"{ex.Message}，跳过该大类。");
            return false;
        }
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
        try
        {
            window = await SelectTabAsync(
                window, tab, $"结算小类：{scope}", cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _log($"{ex.Message}，跳过该小类，继续后续购买。");
            return !_sessionBuyDone;
        }

        await Task.Delay(150, cancellationToken);
        return await BuySlotsAsync(window, category, subcategoryKey, quantities, cancellationToken);
    }

    // 分类选中态是大面积蓝底，未选中态只有蓝字/细边框。
    // 只检查点击点附近的内部小块，不使用整个商品页作为模板。
    private async Task<GameWindow> SelectTabAsync(
        GameWindow window, ConfigPoint tab, string name, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        double already = SelectedBlueFraction(window, tab);
        if (already >= 0.45)
        {
            _log($"{name}：已是选中态（{already:P0}），无需点击。");
            return window;
        }

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            window = _screen.Refresh(window);
            await _screen.ClickAsync(window, tab, $"{name}（{attempt}/5）", cancellationToken);
            // 大类切页后小类条会晚一拍出现；等 UI 稳定再验蓝底。
            await Task.Delay(attempt <= 2 ? 450 : 600, cancellationToken);
            window = _screen.Refresh(window);
            double selected = SelectedBlueFraction(window, tab);
            _log($"{name}：选中蓝底占比 {selected:P0}。");
            if (selected < 0.45)
                continue;
            await Task.Delay(120, cancellationToken);
            if (SelectedBlueFraction(_screen.Refresh(window), tab) >= 0.45)
                return window;
        }

        throw new InvalidOperationException($"{name}未确认选中（已重试 5 次）");
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
                _log($"{scope} 第 {i + 1} 格为每日暗色/已买完，跳过。");
                continue;
            }

            if (quantity < 0)
            {
                if (!await EnsureMultiplierAsync(window, MultiplierState.Max, cancellationToken))
                {
                    _log($"{scope} 第 {i + 1} 格未确认倍率 MAX，跳过购买。");
                    continue;
                }

                // 全买：点一下就复查是否已购完；已购完则不再点第二次。
                for (int n = 0; n < 2; n++)
                {
                    if (_sessionBuyDone)
                        return false;

                    window = await _screen.ClickAsync(
                        window, button, $"{scope} 第 {i + 1} 格全买({n + 1}/2)", cancellationToken);
                    await Task.Delay(50, cancellationToken);

                    visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                    if (visual == BuySlotVisual.AtLimit)
                    {
                        DisableSlotInConfig(category, subcategory, quantities, i, scope);
                        break;
                    }
                    if (visual == BuySlotVisual.DailyDark)
                    {
                        _log($"{scope} 第 {i + 1} 格已变为暗色，停止该格。");
                        break;
                    }

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

                        window = await _screen.ClickAsync(
                            window, button, $"{scope} 第 {i + 1} 格 ×10 ({n + 1}/{tens})", cancellationToken);
                        await Task.Delay(50, cancellationToken);
                        visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                        if (visual is BuySlotVisual.AtLimit or BuySlotVisual.DailyDark)
                        {
                            if (visual == BuySlotVisual.AtLimit)
                                DisableSlotInConfig(category, subcategory, quantities, i, scope);
                            break;
                        }
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

                        window = await _screen.ClickAsync(
                            window, button, $"{scope} 第 {i + 1} 格 ×1 ({n + 1}/{ones})", cancellationToken);
                        await Task.Delay(50, cancellationToken);
                        visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                        if (visual is BuySlotVisual.AtLimit or BuySlotVisual.DailyDark)
                        {
                            if (visual == BuySlotVisual.AtLimit)
                                DisableSlotInConfig(category, subcategory, quantities, i, scope);
                            break;
                        }
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
        if (_confirmedMultiplier == target)
            return true;

        MultiplierState? current = await ReadMultiplierAsync(window, cancellationToken);
        if (current == target)
        {
            _confirmedMultiplier = target;
            return true;
        }

        int plannedClicks = current is null ? 3 : ClicksToReach(current.Value, target);
        _log($"右上角倍率当前 {(current is null ? "未知" : Describe(current.Value))}，目标 {Describe(target)}，点击切换。");
        _confirmedMultiplier = null;

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
                await Task.Delay(Math.Max(_config.DetectionPollIntervalMs, 150), cancellationToken);
            }

            current = await ReadMultiplierAsync(window, cancellationToken);
            if (current == target)
            {
                _confirmedMultiplier = target;
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
        // 倍率字小、底色深；同一 ROI 只截一次，三模板并行匹配后取最高分。
        window = _screen.Refresh(window);
        RegionCapture region = _screen.CaptureRegion(
            window, _config.SettlementMultiplierTopLeft, _config.SettlementMultiplierSize);
        BitmapSource image = region.Image;
        image.Freeze();

        TemplateMatchResult[] matches = await Task.Run(() =>
        {
            var results = new TemplateMatchResult[3];
            Parallel.Invoke(
                () => results[0] = _screen.Match(
                    _multiplierX1Matcher, image, region.LogicalWidth, region.LogicalHeight),
                () => results[1] = _screen.Match(
                    _multiplierX10Matcher, image, region.LogicalWidth, region.LogicalHeight),
                () => results[2] = _screen.Match(
                    _multiplierMaxMatcher, image, region.LogicalWidth, region.LogicalHeight));
            return results;
        }, cancellationToken);

        var candidates = new (MultiplierState State, double Score)[]
        {
            (MultiplierState.X1, matches[0].Score),
            (MultiplierState.X10, matches[1].Score),
            (MultiplierState.Max, matches[2].Score)
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
        // 模板 shop-orange-limit≈340×100、shop-daily-dark≈542×170，搜索区必须不小于模板。
        ConfigSize size = ScreenAutomation.EnsureFitsTemplate(
            ScreenAutomation.EnsureFitsTemplate(_config.SettlementBuyButtonSearchSize, _lvMaxMatcher),
            _buyDisabledMatcher);
        var topLeft = new ConfigPoint(
            Math.Max(0, button.X - inset.X),
            Math.Max(0, button.Y - inset.Y));

        window = _screen.Refresh(window);
        RegionCapture region = _screen.CaptureRegion(window, topLeft, size);
        BitmapSource image = region.Image;
        image.Freeze();

        TemplateMatchResult[] matches = await Task.Run(() =>
        {
            var results = new TemplateMatchResult[2];
            Parallel.Invoke(
                () => results[0] = _screen.Match(
                    _lvMaxMatcher, image, region.LogicalWidth, region.LogicalHeight),
                () => results[1] = _screen.Match(
                    _buyDisabledMatcher, image, region.LogicalWidth, region.LogicalHeight));
            return results;
        }, cancellationToken);

        // 已购完判定阈值低于全局 0.78；再辅以橙色像素兜底。
        const double soldOutThreshold = 0.55;
        double lvMaxScore = matches[0].Score;
        double darkScore = matches[1].Score;
        bool lvMaxHit = lvMaxScore >= soldOutThreshold;
        bool disabledHit = darkScore >= soldOutThreshold;
        bool orangeHint = OrangeLimitFraction(image) >= 0.08;

        if (lvMaxHit || (orangeHint && lvMaxScore >= 0.40))
        {
            if (!lvMaxHit)
                _log($"格子已购完(橙色兜底)：LvMAX={lvMaxScore:F2} 暗色={darkScore:F2}");
            return BuySlotVisual.AtLimit;
        }

        if (disabledHit)
            return BuySlotVisual.DailyDark;

        return BuySlotVisual.Available;
    }

    /// <summary>LvMAX 橙底占比：模板漏检时的颜色兜底。</summary>
    private static double OrangeLimitFraction(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        int orange = 0;
        int total = bitmap.PixelWidth * bitmap.PixelHeight;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (r > 200 && g is > 70 and < 170 && b < 90 && r > g + 40)
                orange++;
        }
        return total == 0 ? 0 : orange / (double)total;
    }

    private async Task<bool> DetectGreenDoneAsync(GameWindow window, CancellationToken cancellationToken)
    {
        // 旧配置可能把 tip ROI 拉到 1120×520，4K 下每次全买匹配可达数秒；运行时夹紧。
        ConfigPoint tipTopLeft = _config.SettlementShopTipTopLeft;
        ConfigSize tipSize = _config.SettlementShopTipSize;
        if (tipSize.Width > 480 || tipSize.Height > 160)
        {
            tipTopLeft = new ConfigPoint(
                tipTopLeft.X + Math.Max(0, (tipSize.Width - 480) / 2),
                tipTopLeft.Y + Math.Max(0, (tipSize.Height - 140) / 2));
            tipSize = new ConfigSize(480, 140);
        }

        TemplateProbeResult tip = await _screen.ProbeAsync(
            window, _greenDoneMatcher, tipTopLeft, tipSize,
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
