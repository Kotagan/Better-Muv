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
            _log("结算仍在，购买已结束，重试完了离开（随后检查确定）。");
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
                // 四个日常小类各自读取左下「本日の購入回数」；上限提示/绿色提示作兜底。
                DateTime now = DateTime.Now;
                bool dailyOk = await RunDailyAsync(window, purchases.Daily, cancellationToken);
                if (!_sessionBuyDone && dailyOk)
                {
                    _config.LastDailyShopDay = DailyShopSchedule.CurrentShopDayKey(now);
                    _config.LastDailyShopDayVerified = true;
                    ConfigStore.Save(_config);
                    _log($"日常四小类已按左下次数与配置推完，等到 {DailyShopSchedule.NextReset(now):MM-dd HH:mm} 刷新。");
                }
                else if (!_sessionBuyDone)
                {
                    _config.LastDailyShopDayVerified = false;
                    ConfigStore.Save(_config);
                    _log("日常未全部走完，本日不写完成标记，下次结算按小类重试。");
                }
                else
                {
                    _log($"日常中途结束（{_sessionBuyDoneReason ?? "会话结束"}），本日标记不写入。");
                }
            }
            else if (!_sessionBuyDone)
            {
                _log("日常购买配置全为 0，跳过日常（含磁带/升级单元）。");
            }

            if (!_sessionBuyDone && totalTimer.ElapsedMilliseconds < ShopBudgetMs && purchases.Equipment.HasAny())
            {
                await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                if (!_sessionBuyDone)
                    await RunEquipmentAsync(window, purchases.Equipment, cancellationToken);
            }
            else if (_sessionBuyDone && purchases.Equipment.HasAny())
            {
                _log($"装备购买跳过（商店已结束：{_sessionBuyDoneReason ?? "未知"}）。");
            }

            if (!_sessionBuyDone && totalTimer.ElapsedMilliseconds < ShopBudgetMs && purchases.Excavation.HasAny())
            {
                await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                if (!_sessionBuyDone)
                    await RunExcavationAsync(window, purchases.Excavation, cancellationToken);
            }
            else if (_sessionBuyDone && purchases.Excavation.HasAny())
            {
                _log($"挖掘购买跳过（商店已结束：{_sessionBuyDoneReason ?? "未知"}）。");
            }

            if (!_sessionBuyDone && totalTimer.ElapsedMilliseconds < ShopBudgetMs && purchases.Artifactor.HasAny())
            {
                await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                if (!_sessionBuyDone)
                    await RunArtifactorAsync(window, purchases.Artifactor, cancellationToken);
            }
            else if (_sessionBuyDone && purchases.Artifactor.HasAny())
            {
                _log($"Artifactor 购买跳过（商店已结束：{_sessionBuyDoneReason ?? "未知"}）。");
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
        // 点完了 → 识别 OK 位置（有则点）→ 再认完了；都认不到=已退出；仍有完了则重试，满 3 次报错并截图。
        _log("商店购买流程结束，点完了离开（随后识别 OK）。");
        const int maxRounds = 3;
        const double doneClickThreshold = 0.72;

        if (!await IsSettlementVisibleAsync(window, cancellationToken))
        {
            _log("结算界面已自行消失。");
            return true;
        }

        for (int round = 1; round <= maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TemplateProbeResult doneProbe = await ProbeSettlementDoneAsync(window, cancellationToken);
            if (!doneProbe.IsMatch)
            {
                _log($"未检测到完了，视为已离开（第 {round} 轮前）。");
                return true;
            }

            if (doneProbe.Score >= doneClickThreshold)
            {
                await _screen.ClickProbeAsync(
                    window, doneProbe, $"结算完了#{round}", cancellationToken, settleDelayMs: 200);
            }
            else
            {
                _log($"结算完了分偏低（{doneProbe.Score:F3} < {doneClickThreshold:F2}），改点写死坐标。");
                window = await _screen.ClickAsync(
                    window, _config.SettlementTopLeft, $"结算完了#{round}", cancellationToken);
            }

            await Task.Delay(500, cancellationToken);
            if (await TryClickConfirmAfterDoneAsync(window, cancellationToken))
                await Task.Delay(500, cancellationToken);
            else
                _log($"第 {round} 轮未识别到 OK。");

            // 再认完了：没有 = 已退出；还有 = 本轮失败。
            await Task.Delay(300, cancellationToken);
            TemplateProbeResult stillDone = await ProbeSettlementDoneAsync(window, cancellationToken);
            if (!stillDone.IsMatch)
            {
                _log($"已离开结算（第 {round} 轮，完了消失）。");
                return true;
            }

            _log($"离开未成功：仍检测到完了 {stillDone.Score:F2}（{round}/{maxRounds}）。");
        }

        // 截图由 MainWindow 在错误自动停止时统一处理；此处只抛错停止。
        throw new InvalidOperationException("结算离开失败：完了仍在（已重试 3 次）");
    }

    /// <summary>点完了后短扫 OK 按钮；命中则点其中心。</summary>
    private async Task<bool> TryClickConfirmAfterDoneAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        const int maxWaitMs = 3000;
        bool loggedMiss = false;
        while (timer.ElapsedMilliseconds < maxWaitMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryClickLeftoverConfirmAsync(window, cancellationToken, logMiss: !loggedMiss))
                return true;
            loggedMiss = true;
            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }

        return false;
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

    private async Task<bool> RunDailyAsync(
        GameWindow window, DailySettlementPurchases daily, CancellationToken cancellationToken)
    {
        _log("购买日常：开始（以左下角四小类独立次数为准） " +
             $"技能书I[{SettlementShopCatalog.FormatSlotPlan("daily", "skillBook1", daily.SkillBook1)}] " +
             $"技能书II[{SettlementShopCatalog.FormatSlotPlan("daily", "skillBook2", daily.SkillBook2)}] " +
             $"磁带[{SettlementShopCatalog.FormatSlotPlan("daily", "disk", daily.Disk)}] " +
             $"升级单元[{SettlementShopCatalog.FormatSlotPlan("daily", "unit", daily.Unit)}]");
        if (!await TrySelectCategoryAsync(
                window, _config.SettlementCategoryDaily, "结算大类：日常", _catDailyOn, cancellationToken))
        {
            _log("购买日常：切入大类失败，后续日常小类全部跳过。");
            return false;
        }
        await Task.Delay(350, cancellationToken);
        bool verified = true;
        if (!await BuyDailySubcategoryOrSkipAsync(window, "skillBook1", 0, daily.SkillBook1, cancellationToken))
        {
            _log($"购买日常：在技能书 I 后结束（{_sessionBuyDoneReason ?? "会话结束"}），磁带/升级单元未执行。");
            return false;
        }
        verified &= _lastDailySubcategoryVerified;
        if (!await BuyDailySubcategoryOrSkipAsync(window, "skillBook2", 1, daily.SkillBook2, cancellationToken))
        {
            _log($"购买日常：在技能书 II 后结束（{_sessionBuyDoneReason ?? "会话结束"}），磁带/升级单元未执行。");
            return false;
        }
        verified &= _lastDailySubcategoryVerified;
        if (!await BuyDailySubcategoryOrSkipAsync(window, "disk", 2, daily.Disk, cancellationToken))
        {
            _log($"购买日常：在磁带后结束（{_sessionBuyDoneReason ?? "会话结束"}），升级单元未执行。");
            return false;
        }
        verified &= _lastDailySubcategoryVerified;
        if (!await BuyDailySubcategoryOrSkipAsync(window, "unit", 3, daily.Unit, cancellationToken))
            return false;
        verified &= _lastDailySubcategoryVerified;
        _log(_sessionBuyDone
            ? $"购买日常：结束（{_sessionBuyDoneReason ?? "会话结束"}）。"
            : "购买日常：四个小类流程走完。");
        return verified && !_sessionBuyDone;
    }

    private bool _lastDailySubcategoryVerified;

    private async Task<bool> BuyDailySubcategoryOrSkipAsync(
        GameWindow window,
        string subcategoryKey,
        int tabIndex,
        int[] quantities,
        CancellationToken cancellationToken)
    {
        _lastDailySubcategoryVerified = true;
        string subName = SettlementShopCatalog.SubcategoryDisplayName("daily", subcategoryKey);

        // 不再用「今天已确认完成」整类跳过：次数 OCR 曾把 N/N 误当成买完，整天清零跳过。
        // 每个小类每次结算都按配置再买；买不动靠上限提示/左下次数停。
        await RefreshCurrencySkipFlagAsync(window, cancellationToken);
        if (_sessionBuyDone)
        {
            _log($"购买日常/{subName}：跳过（商店已结束：{_sessionBuyDoneReason ?? "未知"}）。");
            return false;
        }
        bool result = await BuySubcategorySlotsAsync(
            window, "daily", subcategoryKey, tabIndex, quantities, cancellationToken);
        if (result && _lastDailySubcategoryVerified)
        {
            string shopDay = DailyShopSchedule.CurrentShopDayKey(DateTime.Now);
            _config.DailyShopCompletedSubcategories ??= [];
            if (!string.Equals(_config.DailyShopSubcategoryStateDay, shopDay, StringComparison.Ordinal))
            {
                _config.DailyShopSubcategoryStateDay = shopDay;
                _config.DailyShopCompletedSubcategories.Clear();
            }
            if (!_config.DailyShopCompletedSubcategories.Contains(subcategoryKey, StringComparer.OrdinalIgnoreCase))
                _config.DailyShopCompletedSubcategories.Add(subcategoryKey);
            ConfigStore.Save(_config);
        }
        return result;
    }

    private async Task RunEquipmentAsync(
        GameWindow window, EquipmentSettlementPurchases equipment, CancellationToken cancellationToken)
    {
        _log("购买装备：开始 " +
             $"物理[{SettlementShopCatalog.FormatSlotPlan("equipment", "physics", equipment.Physics)}] " +
             $"EN[{SettlementShopCatalog.FormatSlotPlan("equipment", "en", equipment.En)}] " +
             $"敏捷[{SettlementShopCatalog.FormatSlotPlan("equipment", "agility", equipment.Agility)}]");
        if (!await TrySelectCategoryAsync(
                window, _config.SettlementCategoryEquipment, "结算大类：装备", _catEquipmentOn, cancellationToken))
        {
            _log("购买装备：切入大类失败，跳过。");
            return;
        }
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
        _log($"购买挖掘：开始，计划 {SettlementShopCatalog.FormatSlotPlan("excavation", null, excavation.ToSlots())}");
        if (!await TrySelectCategoryAsync(
                window, _config.SettlementCategoryExcavation, "结算大类：挖掘", _catExcavationOn, cancellationToken))
        {
            _log("购买挖掘：切入大类失败，跳过。");
            return;
        }
        await Task.Delay(350, cancellationToken);
        await BuySlotsAsync(window, "excavation", null, excavation.ToSlots(), cancellationToken);
    }

    private async Task RunArtifactorAsync(
        GameWindow window, ArtifactorSettlementPurchases artifactor, CancellationToken cancellationToken)
    {
        _log("购买 Artifactor：开始 " +
             $"物理[{SettlementShopCatalog.FormatSlotPlan("artifactor", "physics", artifactor.Physics)}] " +
             $"EN[{SettlementShopCatalog.FormatSlotPlan("artifactor", "en", artifactor.En)}] " +
             $"敏捷[{SettlementShopCatalog.FormatSlotPlan("artifactor", "agility", artifactor.Agility)}]");
        if (!await TrySelectCategoryAsync(
                window, _config.SettlementCategoryArtifactor, "结算大类：Artifactor", _catArtifactorOn, cancellationToken))
        {
            _log("购买 Artifactor：切入大类失败，跳过。");
            return;
        }
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
        string subName = SettlementShopCatalog.SubcategoryDisplayName(category, subcategoryKey);
        string scope = $"{SettlementShopCatalog.CategoryDisplayName(category)}/{subName}";
        string plan = SettlementShopCatalog.FormatSlotPlan(category, subcategoryKey, quantities);

        if (_sessionBuyDone)
        {
            _log($"购买 {scope}：跳过（商店已结束：{_sessionBuyDoneReason ?? "未知"}）。");
            return false;
        }
        if (!quantities.Any(v => v != 0))
        {
            _log($"购买 {scope}：配置全为 0，跳过。");
            return true;
        }

        _log($"购买 {scope}：开始，计划 {plan}");
        ConfigPoint tab = _config.SettlementSubcategoryTabs[tabIndex];
        string matcherKey = $"{category}/{subcategoryKey}";
        // 日常小类即使已选中也再点一次，避免切大类后内容未刷新就读格。
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
                _log($"购买 {scope}：切页失败（{ex.Message}），跳过该小类。");
                return !_sessionBuyDone;
            }
        }

        await Task.Delay(forceSubClick ? 450 : 150, cancellationToken);

        if (category.Equals("daily", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshCurrencySkipFlagAsync(window, cancellationToken);
            if (_sessionBuyDone)
            {
                _log($"购买 {scope}：切入后商店已结束（{_sessionBuyDoneReason ?? "未知"}）。");
                return false;
            }
        }

        int? purchaseLeft = null;
        if (category.Equals("daily", StringComparison.OrdinalIgnoreCase))
        {
            purchaseLeft = await TryReadPurchaseCountLeftAsync(window, cancellationToken);
            if (purchaseLeft is 0)
            {
                _log($"购买 {scope}：左下角确认本小类剩余 0，跳过（其它小类继续）。");
                _lastDailySubcategoryVerified = true;
                return true;
            }

            if (purchaseLeft is int left)
                _log($"购买 {scope}：左下角确认本小类剩余 {left}。");
            else
                _log($"购买 {scope}：左下角暂未读出，降级使用上限提示，且不写已验证完成。");
        }

        bool ok = await BuySlotsAsync(window, category, subcategoryKey, quantities, cancellationToken, purchaseLeft);
        if (category.Equals("daily", StringComparison.OrdinalIgnoreCase))
            _lastDailySubcategoryVerified = ok && purchaseLeft is not null;
        _log(ok
            ? $"购买 {scope}：小类完成，计划 {plan}。"
            : $"购买 {scope}：中途结束（{_sessionBuyDoneReason ?? "会话结束"}）。");
        return ok;
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
        CancellationToken cancellationToken,
        int? dailyPurchaseLeft = null)
    {
        string subName = SettlementShopCatalog.SubcategoryDisplayName(category, subcategory);
        string scope = subcategory is null
            ? SettlementShopCatalog.CategoryDisplayName(category)
            : $"{SettlementShopCatalog.CategoryDisplayName(category)}/{subName}";
        bool isDaily = category.Equals("daily", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> itemNames = SettlementShopCatalog.ItemNames(category, subcategory);

        for (int i = 0; i < quantities.Length && i < _config.SettlementBuyButtons.Count; i++)
        {
            if (_sessionBuyDone)
                return false;

            int quantity = quantities[i];
            if (quantity == 0)
                continue;

            string itemLabel = i < itemNames.Count && !string.IsNullOrWhiteSpace(itemNames[i])
                ? itemNames[i]
                : $"第{i + 1}格";
            string slotTag = $"{scope}/{itemLabel}";

            ConfigPoint button = _config.SettlementBuyButtons[i];
            BuySlotVisual visualBefore = await ReadBuySlotVisualAsync(window, button, cancellationToken);
            if (visualBefore == BuySlotVisual.AtLimit)
            {
                DisableSlotInConfig(category, subcategory, quantities, i, scope);
                continue;
            }

            // 全买：直接切到 MAX 点购买（日常/装备等同逻辑）。
            // MAX 一次即按当前可买上限买入；不再 MAX→×10→×1 逐级扫档空转。
            if (quantity < 0)
            {
                if (!await EnsureMultiplierAsync(window, MultiplierState.Max, cancellationToken))
                {
                    _log($"购买 {slotTag}：未确认倍率 MAX，跳过本格以免按错误倍率购买。");
                    continue;
                }

                window = await _screen.ClickAsync(
                    window, button, $"购买 {slotTag} MAX（全部购买）", cancellationToken);
                await Task.Delay(120, cancellationToken);

                if (isDaily && await DetectDailyLimitTipAsync(window, cancellationToken))
                {
                    _log($"购买 {slotTag}：命中每日购买上限提示，本格结束。");
                    if (await TryReadPurchaseCountLeftAsync(window, cancellationToken) is 0)
                    {
                        _log($"购买 {slotTag}：上限提示后左下角剩余 0，本小类购买完成。");
                        return true;
                    }
                    continue;
                }

                if (isDaily)
                {
                    int? leftAfterMax = await TryReadPurchaseCountLeftAsync(window, cancellationToken);
                    if (leftAfterMax is 0)
                    {
                        _log($"购买 {slotTag}：MAX 后左下角剩余 0，本小类购买完成。");
                        return true;
                    }

                    BuySlotVisual visualAfterMax = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                    if (visualAfterMax == BuySlotVisual.AtLimit)
                        DisableSlotInConfig(category, subcategory, quantities, i, scope);

                    if (await DetectGreenDoneAsync(window, cancellationToken))
                        return false;

                    await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                    if (_sessionBuyDone)
                        return false;

                    _log(leftAfterMax is int left
                        ? $"购买 {slotTag}：MAX 完成，左下角仍剩 {left}，继续下一个配置商品。"
                        : $"购买 {slotTag}：MAX 完成，左下角未稳定读出，继续按配置处理下一格。");
                    continue;
                }

                BuySlotVisual visualAfterMaxEquip = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                if (visualAfterMaxEquip == BuySlotVisual.AtLimit)
                    DisableSlotInConfig(category, subcategory, quantities, i, scope);

                if (await DetectGreenDoneAsync(window, cancellationToken))
                    return false;

                await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                if (_sessionBuyDone)
                    return false;

                _log($"购买 {slotTag}：MAX 全买完成。");
                continue;
            }

            // 单次购买上限，防止倍率识别失败时连点上百次卡死。
            int capped = Math.Min(quantity, 30);
            if (capped != quantity)
                _log($"购买 {slotTag}：配置 {quantity}，单次封顶按 {capped} 执行。");
            else
                _log($"购买 {slotTag}：目标数量 {capped}。");

            int tens = capped / 10;
            int ones = capped % 10;
            int boughtClicks = 0;
            if (tens > 0)
            {
                if (!await EnsureMultiplierAsync(window, MultiplierState.X10, cancellationToken))
                {
                    _log($"购买 {slotTag}：未确认倍率 ×10，跳过 ×10 段。");
                }
                else
                {
                    for (int n = 0; n < tens; n++)
                    {
                        if (_sessionBuyDone)
                            return false;

                        if (isDaily && n > 0 && await TryReadPurchaseCountLeftAsync(window, cancellationToken) is 0)
                        {
                            _log($"购买 {slotTag}：左下角剩余已变为 0，本小类购买完成。");
                            return true;
                        }

                        BuySlotVisual preClick = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                        if (preClick == BuySlotVisual.AtLimit)
                        {
                            DisableSlotInConfig(category, subcategory, quantities, i, scope);
                            ones = 0;
                            break;
                        }

                        window = await _screen.ClickAsync(
                            window, button, $"购买 {slotTag} ×10 ({n + 1}/{tens})", cancellationToken);
                        await Task.Delay(50, cancellationToken);
                        boughtClicks += 10;

                        if (isDaily && await DetectDailyLimitTipAsync(window, cancellationToken))
                        {
                            _log($"购买 {slotTag}：命中「これ以上購入できません」，停止该格。");
                            ones = 0;
                            break;
                        }

                        BuySlotVisual afterClick = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                        if (afterClick == BuySlotVisual.AtLimit)
                        {
                            DisableSlotInConfig(category, subcategory, quantities, i, scope);
                            ones = 0;
                            break;
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
                    _log($"购买 {slotTag}：未确认倍率 ×1，跳过 ×1 段。");
                }
                else
                {
                    for (int n = 0; n < ones; n++)
                    {
                        if (_sessionBuyDone)
                            return false;

                        if (isDaily && n > 0 && await TryReadPurchaseCountLeftAsync(window, cancellationToken) is 0)
                        {
                            _log($"购买 {slotTag}：左下角剩余已变为 0，本小类购买完成。");
                            return true;
                        }

                        BuySlotVisual preClick = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                        if (preClick == BuySlotVisual.AtLimit)
                        {
                            DisableSlotInConfig(category, subcategory, quantities, i, scope);
                            break;
                        }

                        window = await _screen.ClickAsync(
                            window, button, $"购买 {slotTag} ×1 ({n + 1}/{ones})", cancellationToken);
                        await Task.Delay(50, cancellationToken);
                        boughtClicks += 1;

                        if (isDaily && await DetectDailyLimitTipAsync(window, cancellationToken))
                        {
                            _log($"购买 {slotTag}：命中「これ以上購入できません」，停止该格。");
                            break;
                        }

                        BuySlotVisual afterClick = await ReadBuySlotVisualAsync(window, button, cancellationToken);
                        if (afterClick == BuySlotVisual.AtLimit)
                        {
                            DisableSlotInConfig(category, subcategory, quantities, i, scope);
                            break;
                        }

                        if (await DetectGreenDoneAsync(window, cancellationToken))
                            return false;

                        await RefreshCurrencySkipFlagAsync(window, cancellationToken);
                        if (_sessionBuyDone)
                            return false;
                    }
                }
            }

            if (!_sessionBuyDone)
                _log($"购买 {slotTag}：本格点击完成（约 {boughtClicks} 件）。");
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
        // 只认 LvMAX / 橙色已购完；不再用暗色購入钮跳过。
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

        double lvMaxScore = lvMax.Score;
        const double lvMaxThreshold = 0.80;
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
            cancellationToken, matchThreshold: 0.72);
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
        // 连读最多 3 次：两次一致立即采用；互相矛盾则不拿单帧结果决定整类跳过。
        var readings = new List<(int Value, string Text)>();
        string? lastUnparsed = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            window = _screen.Refresh(window);
            string? text = await DigitOcrService.TryReadTextAsync(
                _screen.CaptureRegion(window, _config.SettlementPurchaseCountTopLeft, _config.SettlementPurchaseCountSize).Image,
                cancellationToken);
            int? remaining = DigitOcrService.TryParseDailyPurchaseRemaining(text);
            if (remaining is int value)
            {
                readings.Add((value, text?.Trim() ?? string.Empty));
                if (readings.Count >= 2 && readings[^2].Value == value)
                {
                    _log($"左下存量 OCR「{readings[^1].Text}」→ 剩余 {value}（稳定）。");
                    return value;
                }
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                lastUnparsed = text.Trim();
            }

            if (attempt < 2)
                await Task.Delay(90, cancellationToken);
        }

        var majority = readings.GroupBy(r => r.Value).OrderByDescending(g => g.Count()).FirstOrDefault();
        // “剩余 0”会直接跳过整个小类，必须至少两帧一致；正数只影响安全点击上限，可接受唯一有效帧。
        if (majority is not null &&
            (majority.Count() >= 2 ||
             (majority.Key > 0 && readings.Select(r => r.Value).Distinct().Count() == 1)))
        {
            int value = majority.Key;
            _log($"左下存量 OCR → 剩余 {value}（{majority.Count()}/3 次有效）。");
            return value;
        }

        string detail = readings.Count > 0
            ? string.Join("、", readings.Select(r => $"{r.Text}=>{r.Value}"))
            : lastUnparsed ?? "空";
        _log($"左下存量 OCR 未稳定解析（{detail}）。");
        return null;
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
        // 必须先 DisableSlot 再清零工作数组：若先把共享数组置 0，DisableSlot 会认为无变更而不 Save。
        bool changed = SettlementShopCatalog.DisableSlot(
            _config.SettlementPurchases, category, subcategory, slotIndex);
        quantities[slotIndex] = 0;
        if (changed)
            ConfigStore.Save(_config);
        _log(changed
            ? $"{scope} 第 {slotIndex + 1} 格橙色到达上限，已关闭并保存对应购买栏位。"
            : $"{scope} 第 {slotIndex + 1} 格橙色到达上限，对应购买栏位已是关闭。");
    }

    /// <summary>识别确认弹窗（粉色 OK 模板）；命中后点配置的 OK/取消坐标。</summary>
    private async Task<bool> TryClickLeftoverConfirmAsync(
        GameWindow window, CancellationToken cancellationToken, bool logMiss = true)
    {
        var topLeft = new ConfigPoint(
            Math.Max(0, _config.SettlementConfirmTopLeft.X - 120),
            Math.Max(0, _config.SettlementConfirmTopLeft.Y - 80));
        var size = new ConfigSize(
            Math.Max(_config.SettlementConfirmSize.Width + 240, 700),
            Math.Max(_config.SettlementConfirmSize.Height + 160, 480));
        // 导出日志里曾出现 0.591，略低于原 0.62。
        TemplateProbeResult confirm = await _screen.ProbeAsync(
            window, _confirmMatcher, topLeft, size, cancellationToken, matchThreshold: 0.55);
        if (!confirm.IsMatch)
        {
            if (logMiss)
                _log($"OK 未命中（最高 {confirm.Score:F3}）。");
            return false;
        }

        bool leftover = _config.SettlementConfirmLeftover;
        ConfigPoint target = leftover
            ? _config.SettlementConfirmOk
            : _config.SettlementConfirmCancel;
        string action = leftover ? "OK" : "取消";
        _log($"识别到确认弹窗（{confirm.Score:F2}），点击配置{action} ({target.X},{target.Y})。");
        await _screen.ClickAsync(window, target, $"结算{action}", cancellationToken);
        await Task.Delay(600, cancellationToken);
        return true;
    }

    private static string Describe(MultiplierState state) => state switch
    {
        MultiplierState.X1 => "×1",
        MultiplierState.X10 => "×10",
        MultiplierState.Max => "MAX",
        _ => state.ToString()
    };
}
