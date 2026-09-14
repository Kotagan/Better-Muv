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
        AtLimit
    }

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly TemplateMatcher _settlementMatcher;
    private readonly TemplateMatcher _lvMaxMatcher;
    private readonly TemplateMatcher _dailyLimitTipMatcher;
    private readonly TemplateMatcher _greenDoneMatcher;
    private readonly TemplateMatcher _confirmMatcher;
    private readonly TemplateMatcher _multiplierX1Matcher;
    private readonly TemplateMatcher _multiplierX10Matcher;
    private readonly TemplateMatcher _multiplierMaxMatcher;
    private readonly TemplateMatcher _catDailyOn;
    private readonly TemplateMatcher _catEquipmentOn;
    private readonly TemplateMatcher _catExcavationOn;
    private readonly TemplateMatcher _catArtifactorOn;
    private readonly Dictionary<string, TemplateMatcher> _subcategoryOn;
    private readonly Action<string> _log;
    private bool _sessionBuyDone;
    /// <summary>本会话已确认的倍率，避免每次全买前重复识别/切换。</summary>
    private MultiplierState? _confirmedMultiplier;
    /// <summary>本轮结算购买已跑完；离开失败后只点完了 + 余矿确认，不再重复购买。</summary>
    private bool _shopPassCompleted;
    private string? _sessionBuyDoneReason;

    /// <summary>大类/小类选中模板阈值。</summary>
    private const double CategorySelectedThreshold = 0.72;
    /// <summary>小类页签蓝底兜底阈值。</summary>
    private const double SubcategorySelectedBlueThreshold = 0.38;

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
        _dailyLimitTipMatcher = TemplateAssets.Load(templateDirectory, "shop-daily-limit-tip.png");
        _greenDoneMatcher = TemplateAssets.Load(templateDirectory, "shop-green-done.png");
        _confirmMatcher = TemplateAssets.Load(templateDirectory, "settlement-confirm.png");
        _multiplierX1Matcher = TemplateAssets.Load(templateDirectory, "multiplier-x1.png");
        _multiplierX10Matcher = TemplateAssets.Load(templateDirectory, "multiplier-x10.png");
        _multiplierMaxMatcher = TemplateAssets.Load(templateDirectory, "multiplier-max.png");
        _catDailyOn = TemplateAssets.Load(templateDirectory, "shop-cat-daily-on.png");
        _catEquipmentOn = TemplateAssets.Load(templateDirectory, "shop-cat-equipment-on.png");
        _catExcavationOn = TemplateAssets.Load(templateDirectory, "shop-cat-excavation-on.png");
        _catArtifactorOn = TemplateAssets.Load(templateDirectory, "shop-cat-artifactor-on.png");
        _subcategoryOn = new Dictionary<string, TemplateMatcher>(StringComparer.OrdinalIgnoreCase)
        {
            ["daily/skillBook1"] = TemplateAssets.Load(templateDirectory, "shop-sub-daily-skillBook1-on.png"),
            ["daily/skillBook2"] = TemplateAssets.Load(templateDirectory, "shop-sub-daily-skillBook2-on.png"),
            ["daily/disk"] = TemplateAssets.Load(templateDirectory, "shop-sub-daily-disk-on.png"),
            ["daily/unit"] = TemplateAssets.Load(templateDirectory, "shop-sub-daily-unit-on.png"),
            ["equipment/physics"] = TemplateAssets.Load(templateDirectory, "shop-sub-equipment-physics-on.png"),
            ["equipment/en"] = TemplateAssets.Load(templateDirectory, "shop-sub-equipment-en-on.png"),
            ["equipment/agility"] = TemplateAssets.Load(templateDirectory, "shop-sub-equipment-agility-on.png"),
            ["artifactor/physics"] = TemplateAssets.Load(templateDirectory, "shop-sub-artifactor-physics-on.png"),
            ["artifactor/en"] = TemplateAssets.Load(templateDirectory, "shop-sub-artifactor-en-on.png"),
            ["artifactor/agility"] = TemplateAssets.Load(templateDirectory, "shop-sub-artifactor-agility-on.png"),
        };
    }

    public TemplateMatcher SettlementMatcher => _settlementMatcher;

    public void ResetVisit() => _shopPassCompleted = false;

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
        if (_shopPassCompleted)
        {
            _log("结算仍在，购买已结束，重试完了离开（余矿则点确认）。");
            bool leftOnly = await LeaveSettlementAsync(window, cancellationToken);
            if (leftOnly)
                _shopPassCompleted = false;
            return leftOnly;
        }

        _log("识别到迷宫结算界面，开始商店购买（完了暂不点击）。");
        var totalTimer = Stopwatch.StartNew();
        const int ShopBudgetMs = 180000;
        _sessionBuyDone = false;
        _sessionBuyDoneReason = null;
        _confirmedMultiplier = null;

        try
        {
            await RefreshCurrencySkipFlagAsync(window, cancellationToken);

            SettlementPurchases purchases = _config.SettlementPurchases;

            if (!_sessionBuyDone && purchases.Daily.HasAny())
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
            {
                await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                if (!_sessionBuyDone)
                    await RunEquipmentAsync(window, purchases.Equipment, cancellationToken);
            }
            if (!_sessionBuyDone && totalTimer.ElapsedMilliseconds < ShopBudgetMs && purchases.Excavation.HasAny())
            {
                await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                if (!_sessionBuyDone)
                    await RunExcavationAsync(window, purchases.Excavation, cancellationToken);
            }
            if (!_sessionBuyDone && totalTimer.ElapsedMilliseconds < ShopBudgetMs && purchases.Artifactor.HasAny())
            {
                await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                if (!_sessionBuyDone)
                    await RunArtifactorAsync(window, purchases.Artifactor, cancellationToken);
            }

            if (_sessionBuyDone)
                _log(_sessionBuyDoneReason ?? "本次商店购买结束，进入完了。");
            else if (totalTimer.ElapsedMilliseconds >= ShopBudgetMs)
                _log($"商店购买超时（{ShopBudgetMs / 1000}s），跳过剩余项，直接点完了。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"商店购买异常：{ex.Message}，停止购买并保留当前页面，避免在错误分类购买。");
            _shopPassCompleted = true;
            return false;
        }

        _shopPassCompleted = true;
        bool left = await LeaveSettlementAsync(window, cancellationToken);
        if (left)
            _shopPassCompleted = false;
        return left;
    }

    private async Task<bool> LeaveSettlementAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("商店购买流程结束，点击完了离开。");
        var leaveTimer = Stopwatch.StartNew();
        int attempts = 0;
        while (leaveTimer.ElapsedMilliseconds < 20000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsSettlementVisibleAsync(window, cancellationToken))
            {
                // 点完了后若货币未花完会出余矿确认，点确认离开。
                await HandleLeftoverConfirmAsync(window, cancellationToken, maxWaitMs: 4000);
                if (!await IsSettlementVisibleAsync(window, cancellationToken))
                {
                    _log(attempts == 0
                        ? "结算界面已自行消失。"
                        : $"已离开结算界面（完了点击 {attempts} 次）。");
                    return true;
                }
            }

            attempts++;
            TemplateProbeResult doneProbe = await ProbeSettlementDoneAsync(window, cancellationToken);
            if (doneProbe.IsMatch)
            {
                await _screen.ClickProbeAsync(
                    window, doneProbe, $"结算完了#{attempts}", cancellationToken, settleDelayMs: 200);
            }
            else
            {
                window = await _screen.ClickAsync(
                    window, _config.SettlementTopLeft, $"结算完了#{attempts}", cancellationToken);
            }
            await Task.Delay(500, cancellationToken);
            await HandleLeftoverConfirmAsync(window, cancellationToken, maxWaitMs: 4000);
        }

        _log("结算离开超时，兜底：余矿确认 OK → 完了匹配中心 → 伙伴离开点。");
        await _screen.ClickAsync(window, _config.SettlementConfirmOk, "结算余矿确认(兜底)", cancellationToken);
        await Task.Delay(500, cancellationToken);
        TemplateProbeResult fallbackDone = await ProbeSettlementDoneAsync(window, cancellationToken);
        if (fallbackDone.IsMatch)
            await _screen.ClickProbeAsync(window, fallbackDone, "结算完了(兜底)", cancellationToken, settleDelayMs: 200);
        else
            await _screen.ClickAsync(window, _config.SettlementTopLeft, "结算完了(兜底)", cancellationToken);
        await Task.Delay(600, cancellationToken);
        await HandleLeftoverConfirmAsync(window, cancellationToken, maxWaitMs: 4000);
        if (!await IsSettlementVisibleAsync(window, cancellationToken))
        {
            _log("兜底后已离开结算。");
            return true;
        }

        await _screen.ClickAsync(window, _config.PartnerClick, "结算离开(伙伴点兜底)", cancellationToken);
        await Task.Delay(700, cancellationToken);
        bool left = !await IsSettlementVisibleAsync(window, cancellationToken);
        _log(left
            ? "伙伴点兜底后已离开结算。"
            : $"结算完了点击超时（{attempts} 次），界面仍在。");
        return left;
    }

    private async Task<TemplateProbeResult> ProbeSettlementDoneAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var topLeft = new ConfigPoint(
            Math.Max(0, _config.SettlementSearchTopLeft.X - 40),
            Math.Max(0, _config.SettlementSearchTopLeft.Y - 40));
        var size = new ConfigSize(
            Math.Max(_config.SettlementSearchSize.Width + 80, 360),
            Math.Max(_config.SettlementSearchSize.Height + 80, 180));
        return await _screen.ProbeAsync(
            window, _settlementMatcher, topLeft, size, cancellationToken, matchThreshold: 0.48);
    }

    private async Task RunDailyAsync(
        GameWindow window, DailySettlementPurchases daily, CancellationToken cancellationToken)
    {
        if (!await TrySelectCategoryAsync(
                window, _config.SettlementCategoryDaily, "结算大类：日常", _catDailyOn, cancellationToken))
            return;
        await Task.Delay(350, cancellationToken);
        if (!await BuyDailySubcategoryOrSkipAsync(window, "skillBook1", 0, daily.SkillBook1, cancellationToken))
            return;
        if (!await BuyDailySubcategoryOrSkipAsync(window, "skillBook2", 1, daily.SkillBook2, cancellationToken))
            return;
        if (!await BuyDailySubcategoryOrSkipAsync(window, "disk", 2, daily.Disk, cancellationToken))
            return;
        await BuyDailySubcategoryOrSkipAsync(window, "unit", 3, daily.Unit, cancellationToken);
    }

    private async Task<bool> BuyDailySubcategoryOrSkipAsync(
        GameWindow window,
        string subcategoryKey,
        int tabIndex,
        int[] quantities,
        CancellationToken cancellationToken)
    {
        await RefreshCurrencySkipFlagAsync(window, cancellationToken);
        if (_sessionBuyDone)
            return false;
        return await BuySubcategorySlotsAsync(window, "daily", subcategoryKey, tabIndex, quantities, cancellationToken);
    }

    private async Task RunEquipmentAsync(
        GameWindow window, EquipmentSettlementPurchases equipment, CancellationToken cancellationToken)
    {
        if (!await TrySelectCategoryAsync(
                window, _config.SettlementCategoryEquipment, "结算大类：装备", _catEquipmentOn, cancellationToken))
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
        if (!await TrySelectCategoryAsync(
                window, _config.SettlementCategoryExcavation, "结算大类：挖掘", _catExcavationOn, cancellationToken))
            return;
        await Task.Delay(350, cancellationToken);
        await BuySlotsAsync(window, "excavation", null, excavation.ToSlots(), cancellationToken);
    }

    private async Task RunArtifactorAsync(
        GameWindow window, ArtifactorSettlementPurchases artifactor, CancellationToken cancellationToken)
    {
        if (!await TrySelectCategoryAsync(
                window, _config.SettlementCategoryArtifactor, "结算大类：Artifactor", _catArtifactorOn, cancellationToken))
            return;
        await Task.Delay(350, cancellationToken);
        if (!await BuySubcategorySlotsAsync(window, "artifactor", "physics", 0, artifactor.Physics, cancellationToken))
            return;
        if (!await BuySubcategorySlotsAsync(window, "artifactor", "en", 1, artifactor.En, cancellationToken))
            return;
        await BuySubcategorySlotsAsync(window, "artifactor", "agility", 2, artifactor.Agility, cancellationToken);
    }

    private async Task<bool> TrySelectCategoryAsync(
        GameWindow window,
        ConfigPoint tab,
        string name,
        TemplateMatcher selectedMatcher,
        CancellationToken cancellationToken)
    {
        // 写死点击；选中模板仅日志，失败不阻断（避免旧裁图/分辨率差异导致整类跳过）。
        await SelectFixedTabAsync(
            window, tab, name, selectedMatcher, CategorySearchRoi(tab), cancellationToken);
        return true;
    }

    private (ConfigPoint TopLeft, ConfigSize Size) CategorySearchRoi(ConfigPoint tab)
    {
        int w = Math.Max(140, _config.SettlementCategorySearchSize.Width);
        int h = Math.Max(60, _config.SettlementCategorySearchSize.Height);
        return (
            new ConfigPoint(Math.Max(0, tab.X - w / 2), Math.Max(0, tab.Y - h / 2)),
            new ConfigSize(w, h));
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
        string matcherKey = $"{category}/{subcategoryKey}";
        // 日常小类即使已选中也再点一次，避免切大类后内容未刷新就读格（曾误判技能书1木为暗色）。
        bool forceSubClick = category.Equals("daily", StringComparison.OrdinalIgnoreCase);
        if (_subcategoryOn.TryGetValue(matcherKey, out TemplateMatcher? selectedMatcher))
        {
            await SelectFixedTabAsync(
                window, tab, $"结算小类：{scope}", selectedMatcher, SubcategorySearchRoi(tab),
                cancellationToken, forceClick: forceSubClick);
        }
        else
        {
            try
            {
                window = await SelectTabAsync(window, tab, $"结算小类：{scope}", cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _log($"{ex.Message}，跳过该小类，继续后续购买。");
                return !_sessionBuyDone;
            }
        }

        await Task.Delay(forceSubClick ? 280 : 150, cancellationToken);

        // 日常每个小类（技能书/磁带/升级单元）有独立的「本日の購入回数」；切入后再读，为 0 只跳过本小类。
        if (category.Equals("daily", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshCurrencySkipFlagAsync(window, cancellationToken);
            if (_sessionBuyDone)
                return false;

            int? purchaseLeft = await TryReadPurchaseCountLeftAsync(window, cancellationToken);
            if (purchaseLeft is 0)
            {
                _log($"{scope}：本小类购买次数为 0，跳过该小类。");
                return true;
            }

            if (purchaseLeft is int left)
                _log($"{scope}：本小类购买次数剩余 {left}。");
        }

        return await BuySlotsAsync(window, category, subcategoryKey, quantities, cancellationToken);
    }

    /// <summary>写死点点击；选中模板仅日志，失败不阻断。</summary>
    private async Task SelectFixedTabAsync(
        GameWindow window,
        ConfigPoint tab,
        string name,
        TemplateMatcher selectedMatcher,
        (ConfigPoint TopLeft, ConfigSize Size) roi,
        CancellationToken cancellationToken,
        bool forceClick = false)
    {
        window = _screen.Refresh(window);
        TemplateProbeResult before = await _screen.ProbeAsync(
            window, selectedMatcher, roi.TopLeft, roi.Size, cancellationToken, CategorySelectedThreshold);
        if (before.IsMatch && !forceClick)
        {
            _log($"{name}：已是选中态（模板 {before.Score:F2}），无需点击。");
            return;
        }

        if (before.IsMatch && forceClick)
            _log($"{name}：已是选中态（模板 {before.Score:F2}），仍点击以刷新内容。");

        await _screen.ClickAsync(window, tab, name, cancellationToken);
        await Task.Delay(450, cancellationToken);
        window = _screen.Refresh(window);
        TemplateProbeResult after = await _screen.ProbeAsync(
            window, selectedMatcher, roi.TopLeft, roi.Size, cancellationToken, CategorySelectedThreshold);
        if (after.IsMatch)
            _log($"{name}：点击后选中确认（模板 {after.Score:F2}）。");
        else
            _log($"{name}：点击写死点后选中模板 {after.Score:F2} < {CategorySelectedThreshold:F2}，仍继续。");
    }

    private (ConfigPoint TopLeft, ConfigSize Size) SubcategorySearchRoi(ConfigPoint tab)
    {
        // 顶栏小类收紧后 skillBook 逻辑宽约 193；搜索区略放大避免裁切。
        const int w = 220;
        const int h = 50;
        return (
            new ConfigPoint(Math.Max(0, tab.X - w / 2), Math.Max(0, tab.Y - h / 2)),
            new ConfigSize(w, h));
    }

    // 小类页签仍用蓝底比例作兜底（顶栏横条选中态稳定）。
    private async Task<GameWindow> SelectTabAsync(
        GameWindow window, ConfigPoint tab, string name, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        double already = SelectedBlueFraction(window, tab);
        if (already >= SubcategorySelectedBlueThreshold)
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
            if (selected < SubcategorySelectedBlueThreshold)
                continue;
            await Task.Delay(120, cancellationToken);
            if (SelectedBlueFraction(_screen.Refresh(window), tab) >= SubcategorySelectedBlueThreshold)
                return window;
        }

        throw new InvalidOperationException($"{name}未确认选中（已重试 5 次）");
    }

    private double SelectedBlueFraction(GameWindow window, ConfigPoint tab)
    {
        int halfW = Math.Max(12, _config.SettlementCategoryProbeHalfSize.Width);
        int halfH = Math.Max(12, _config.SettlementCategoryProbeHalfSize.Height);
        var topLeft = new ConfigPoint(Math.Max(0, tab.X - halfW), Math.Max(0, tab.Y - halfH));
        var size = new ConfigSize(halfW * 2, halfH * 2);
        var region = _screen.CaptureRegion(window, topLeft, size);
        var bitmap = new FormatConvertedBitmap(region.Image, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        int blue = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            // 选中：中等～亮蓝；白底未选中不会进此分支。
            if (b > 120 && g > 50 && r < 150 && b - r > 40 && b > g)
                blue++;
        }
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
        bool isDaily = category.Equals("daily", StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < quantities.Length && i < _config.SettlementBuyButtons.Count; i++)
        {
            if (_sessionBuyDone)
                return false;

            int quantity = quantities[i];
            if (quantity == 0)
                continue;

            ConfigPoint button = _config.SettlementBuyButtons[i];
            // 日常不以暗色/最大購入钮预判，点到粉红「これ以上購入できません」再停。
            if (!isDaily)
            {
                BuySlotVisual visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                if (visual == BuySlotVisual.AtLimit)
                {
                    DisableSlotInConfig(category, subcategory, quantities, i, scope);
                    continue;
                }
            }

            if (quantity < 0)
            {
                // 全买：MAX → ×10 → ×1 逐级降；每级连点直到售罄/买不动/货币归零。
                MultiplierState[] multipliers =
                [
                    MultiplierState.Max,
                    MultiplierState.X10,
                    MultiplierState.X1
                ];
                bool slotFinished = false;
                foreach (MultiplierState mult in multipliers)
                {
                    if (_sessionBuyDone || slotFinished)
                        break;

                    if (!await EnsureMultiplierAsync(window, mult, cancellationToken))
                    {
                        _log($"{scope} 第 {i + 1} 格未确认倍率 {FormatMultiplier(mult)}，尝试更低倍率。");
                        continue;
                    }

                    for (int n = 0; n < 20; n++)
                    {
                        if (_sessionBuyDone)
                            return false;

                        window = await _screen.ClickAsync(
                            window,
                            button,
                            $"{scope} 第 {i + 1} 格全买 {FormatMultiplier(mult)}({n + 1}/20)",
                            cancellationToken);
                        await Task.Delay(80, cancellationToken);

                        if (isDaily && await DetectDailyLimitTipAsync(window, cancellationToken))
                        {
                            _log($"{scope} 第 {i + 1} 格命中「これ以上購入できません」，停止该格。");
                            slotFinished = true;
                            break;
                        }

                        if (!isDaily)
                        {
                            BuySlotVisual visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                            if (visual == BuySlotVisual.AtLimit)
                            {
                                DisableSlotInConfig(category, subcategory, quantities, i, scope);
                                slotFinished = true;
                                break;
                            }
                        }

                        if (await DetectGreenDoneAsync(window, cancellationToken))
                            return false;

                        await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                        if (_sessionBuyDone)
                            return false;
                    }
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

                        if (isDaily && await DetectDailyLimitTipAsync(window, cancellationToken))
                        {
                            _log($"{scope} 第 {i + 1} 格命中「これ以上購入できません」，停止该格。");
                            ones = 0;
                            break;
                        }

                        if (!isDaily)
                        {
                            BuySlotVisual visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                            if (visual == BuySlotVisual.AtLimit)
                            {
                                DisableSlotInConfig(category, subcategory, quantities, i, scope);
                                ones = 0;
                                break;
                            }
                        }

                        if (await DetectGreenDoneAsync(window, cancellationToken))
                            return false;

                        await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                        if (_sessionBuyDone)
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

                        if (isDaily && await DetectDailyLimitTipAsync(window, cancellationToken))
                        {
                            _log($"{scope} 第 {i + 1} 格命中「これ以上購入できません」，停止该格。");
                            break;
                        }

                        if (!isDaily)
                        {
                            BuySlotVisual visual = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                            if (visual == BuySlotVisual.AtLimit)
                            {
                                DisableSlotInConfig(category, subcategory, quantities, i, scope);
                                break;
                            }
                        }

                        if (await DetectGreenDoneAsync(window, cancellationToken))
                            return false;

                        await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                        if (_sessionBuyDone)
                            return false;
                    }
                }
            }
        }

        return !_sessionBuyDone;
    }

    private static string FormatMultiplier(MultiplierState state) => state switch
    {
        MultiplierState.X1 => "×1",
        MultiplierState.X10 => "×10",
        MultiplierState.Max => "MAX",
        _ => state.ToString()
    };

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
        // 模板 shop-orange-limit≈330×94（4K），搜索区须盖住 LvMAX。
        ConfigSize size = ScreenAutomation.EnsureFitsTemplate(
            _config.SettlementBuyButtonSearchSize, _lvMaxMatcher);
        var topLeft = new ConfigPoint(
            Math.Max(0, button.X - inset.X),
            Math.Max(0, button.Y - inset.Y));

        window = _screen.Refresh(window);
        RegionCapture region = _screen.CaptureRegion(window, topLeft, size);
        BitmapSource image = region.Image;
        image.Freeze();

        TemplateMatchResult lvMax = await Task.Run(
            () => _screen.Match(_lvMaxMatcher, image, region.LogicalWidth, region.LogicalHeight),
            cancellationToken);

        const double lvMaxThreshold = 0.80;
        double lvMaxScore = lvMax.Score;
        bool lvMaxHit = lvMaxScore >= lvMaxThreshold;
        bool orangeHint = OrangeLimitFraction(image) >= 0.08;

        if (lvMaxHit || (orangeHint && lvMaxScore >= 0.55))
        {
            if (!lvMaxHit)
                _log($"格子已购完(橙色兜底)：LvMAX={lvMaxScore:F2}");
            return BuySlotVisual.AtLimit;
        }

        return BuySlotVisual.Available;
    }

    /// <summary>日常点到上限：粉红「これ以上購入できません」→ 停当前格，不结束整次商店。</summary>
    private async Task<bool> DetectDailyLimitTipAsync(GameWindow window, CancellationToken cancellationToken)
    {
        // 提示在商品区中部；逻辑坐标覆盖实机 tip 中心约 (960,538)。
        var tipTopLeft = new ConfigPoint(500, 450);
        var tipSize = ScreenAutomation.EnsureFitsTemplate(new ConfigSize(900, 220), _dailyLimitTipMatcher);

        TemplateProbeResult tip = await _screen.ProbeAsync(
            window, _dailyLimitTipMatcher, tipTopLeft, tipSize,
            cancellationToken, matchThreshold: 0.80);
        return tip.IsMatch;
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

    /// <summary>绿色「强化素材不足」→ 结束购买，随后进完了；余矿确认在离开时点确认。</summary>
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
            cancellationToken, matchThreshold: 0.80);
        if (!tip.IsMatch)
            return false;

        _sessionBuyDone = true;
        _sessionBuyDoneReason = $"命中绿色提示「强化素材不足」（得分 {tip.Score:F2}），进入完了。";
        _log(_sessionBuyDoneReason);
        return true;
    }

    private async Task RefreshCurrencySkipFlagAsync(GameWindow window, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        int? currency = await TryReadCurrencyAsync(window, cancellationToken);
        if (currency is 0)
        {
            if (!_sessionBuyDone)
            {
                _sessionBuyDoneReason = "右上剩余货币识别为 0，结束本次商店购买。";
                _log(_sessionBuyDoneReason);
            }
            _sessionBuyDone = true;
        }
    }

    private async Task<int?> TryReadPurchaseCountLeftAsync(GameWindow window, CancellationToken cancellationToken)
    {
        string? text = await DigitOcrService.TryReadTextAsync(
            _screen.CaptureRegion(window, _config.SettlementPurchaseCountTopLeft, _config.SettlementPurchaseCountSize).Image,
            cancellationToken);
        int? left = DigitOcrService.TryParseRatioLeft(text);
        if (left is null && !string.IsNullOrWhiteSpace(text))
            _log($"购买次数 OCR 原文「{text}」，未能解析 N/M。");
        return left;
    }

    private async Task<int?> TryReadCurrencyAsync(GameWindow window, CancellationToken cancellationToken)
    {
        string? text = await DigitOcrService.TryReadTextAsync(
            _screen.CaptureRegion(window, _config.SettlementCurrencyTopLeft, _config.SettlementCurrencySize).Image,
            cancellationToken);
        int? value = DigitOcrService.TryParseNonNegativeInt(text);
        if (value is null && !string.IsNullOrWhiteSpace(text))
            _log($"货币 OCR 原文「{text}」，未能解析数字。");
        return value;
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

    private async Task HandleLeftoverConfirmAsync(
        GameWindow window, CancellationToken cancellationToken, int maxWaitMs = 8000)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < maxWaitMs)
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
