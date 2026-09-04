using System.Diagnostics;
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

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly TemplateMatcher _settlementMatcher;
    private readonly TemplateMatcher _lvMaxMatcher;
    private readonly TemplateMatcher _buyDisabledMatcher;
    private readonly TemplateMatcher _confirmMatcher;
    private readonly TemplateMatcher _multiplierX1Matcher;
    private readonly TemplateMatcher _multiplierX10Matcher;
    private readonly TemplateMatcher _multiplierMaxMatcher;
    private readonly Action<string> _log;

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
        _lvMaxMatcher = TemplateAssets.Load(templateDirectory, "lv-max.png");
        _buyDisabledMatcher = TemplateAssets.Load(templateDirectory, "buy-disabled.png");
        _confirmMatcher = TemplateAssets.Load(templateDirectory, "settlement-confirm.png");
        _multiplierX1Matcher = TemplateAssets.Load(templateDirectory, "multiplier-x1.png");
        _multiplierX10Matcher = TemplateAssets.Load(templateDirectory, "multiplier-x10.png");
        _multiplierMaxMatcher = TemplateAssets.Load(templateDirectory, "multiplier-max.png");
    }

    public TemplateMatcher SettlementMatcher => _settlementMatcher;

    public async Task<bool> IsSettlementVisibleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult probe = await _screen.ProbeAsync(
            window, _settlementMatcher, _config.SettlementSearchTopLeft, _config.SettlementSearchSize,
            cancellationToken);
        return probe.IsMatch;
    }

    public async Task<bool> RunAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("识别到迷宫结算界面，开始商店购买（完了暂不点击）。");
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
                _config.LastDailyShopDay = DailyShopSchedule.CurrentShopDayKey(now);
                ConfigStore.Save(_config);
                _log($"日常已按配置买够，等到 {DailyShopSchedule.NextReset(now):MM-dd HH:mm} 刷新后再买。");
            }
        }
        if (purchases.Equipment.HasAny())
            await RunEquipmentAsync(window, purchases.Equipment, cancellationToken);
        if (purchases.Excavation.HasAny())
            await RunExcavationAsync(window, purchases.Excavation, cancellationToken);
        if (purchases.Artifactor.HasAny())
            await RunArtifactorAsync(window, purchases.Artifactor, cancellationToken);

        _log("商店购买流程结束，点击完了离开。");
        var leaveTimer = Stopwatch.StartNew();
        int attempts = 0;
        while (leaveTimer.ElapsedMilliseconds < 20000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsSettlementVisibleAsync(window, cancellationToken))
            {
                _log(attempts == 0
                    ? "结算界面已自行消失。"
                    : $"已离开结算界面（完了点击 {attempts} 次）。");
                return true;
            }

            attempts++;
            window = await _screen.ClickAsync(
                window, _config.SettlementTopLeft, $"结算完了#{attempts}", cancellationToken);
            await Task.Delay(700, cancellationToken);
            await HandleLeftoverConfirmAsync(window, cancellationToken);
        }

        _log($"结算完了点击超时（{attempts} 次），界面仍在。");
        return false;
    }

    private async Task RunDailyAsync(
        GameWindow window, DailySettlementPurchases daily, CancellationToken cancellationToken)
    {
        window = await _screen.ClickAsync(window, _config.SettlementCategoryDaily, "结算大类：日常", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        await BuySubcategorySlotsAsync(window, "日常", "skillBook1", 0, daily.SkillBook1, cancellationToken);
        await BuySubcategorySlotsAsync(window, "日常", "skillBook2", 1, daily.SkillBook2, cancellationToken);
        await BuySubcategorySlotsAsync(window, "日常", "disk", 2, daily.Disk, cancellationToken);
        await BuySubcategorySlotsAsync(window, "日常", "unit", 3, daily.Unit, cancellationToken);
    }

    private async Task RunEquipmentAsync(
        GameWindow window, EquipmentSettlementPurchases equipment, CancellationToken cancellationToken)
    {
        window = await _screen.ClickAsync(window, _config.SettlementCategoryEquipment, "结算大类：装备", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        await BuySubcategorySlotsAsync(window, "装备", "physics", 0, equipment.Physics, cancellationToken);
        await BuySubcategorySlotsAsync(window, "装备", "en", 1, equipment.En, cancellationToken);
        await BuySubcategorySlotsAsync(window, "装备", "agility", 2, equipment.Agility, cancellationToken);
    }

    private async Task RunExcavationAsync(
        GameWindow window, ExcavationSettlementPurchases excavation, CancellationToken cancellationToken)
    {
        window = await _screen.ClickAsync(window, _config.SettlementCategoryExcavation, "结算大类：挖掘", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        await BuySlotsAsync(window, "挖掘", excavation.ToSlots(), cancellationToken);
    }

    private async Task RunArtifactorAsync(
        GameWindow window, ArtifactorSettlementPurchases artifactor, CancellationToken cancellationToken)
    {
        window = await _screen.ClickAsync(window, _config.SettlementCategoryArtifactor, "结算大类：Artifactor", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        await BuySubcategorySlotsAsync(window, "Artifactor", "physics", 0, artifactor.Physics, cancellationToken);
        await BuySubcategorySlotsAsync(window, "Artifactor", "en", 1, artifactor.En, cancellationToken);
        await BuySubcategorySlotsAsync(window, "Artifactor", "agility", 2, artifactor.Agility, cancellationToken);
    }

    private async Task BuySubcategorySlotsAsync(
        GameWindow window,
        string categoryName,
        string subcategoryKey,
        int tabIndex,
        int[] quantities,
        CancellationToken cancellationToken)
    {
        if (!quantities.Any(v => v != 0))
            return;

        ConfigPoint tab = _config.SettlementSubcategoryTabs[tabIndex];
        window = await _screen.ClickAsync(
            window, tab, $"结算小类：{categoryName}/{subcategoryKey}", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        await BuySlotsAsync(window, $"{categoryName}/{subcategoryKey}", quantities, cancellationToken);
    }

    private async Task BuySlotsAsync(
        GameWindow window, string scope, int[] quantities, CancellationToken cancellationToken)
    {
        for (int i = 0; i < quantities.Length && i < _config.SettlementBuyButtons.Count; i++)
        {
            int quantity = quantities[i];
            if (quantity == 0)
                continue;

            ConfigPoint button = _config.SettlementBuyButtons[i];
            if (await IsBuySlotBlockedAsync(window, button, cancellationToken))
            {
                _log($"{scope} 第 {i + 1} 格已满级或达上限，跳过。");
                continue;
            }

            if (quantity < 0)
            {
                await EnsureMultiplierAsync(window, MultiplierState.Max, cancellationToken);
                if (await IsBuySlotBlockedAsync(window, button, cancellationToken))
                {
                    _log($"{scope} 第 {i + 1} 格已满级或达上限，跳过。");
                    continue;
                }

                window = await _screen.ClickAsync(window, button, $"{scope} 第 {i + 1} 格全买", cancellationToken);
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                if (!await IsBuySlotBlockedAsync(window, button, cancellationToken))
                {
                    window = await _screen.ClickAsync(window, button, $"{scope} 第 {i + 1} 格全买兜底", cancellationToken);
                    await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                }
                continue;
            }

            int tens = quantity / 10;
            int ones = quantity % 10;
            if (tens > 0)
            {
                await EnsureMultiplierAsync(window, MultiplierState.X10, cancellationToken);
                for (int n = 0; n < tens; n++)
                {
                    if (await IsBuySlotBlockedAsync(window, button, cancellationToken))
                        break;
                    window = await _screen.ClickAsync(
                        window, button, $"{scope} 第 {i + 1} 格 ×10 ({n + 1}/{tens})", cancellationToken);
                    await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                }
            }

            if (ones > 0)
            {
                await EnsureMultiplierAsync(window, MultiplierState.X1, cancellationToken);
                for (int n = 0; n < ones; n++)
                {
                    if (await IsBuySlotBlockedAsync(window, button, cancellationToken))
                        break;
                    window = await _screen.ClickAsync(
                        window, button, $"{scope} 第 {i + 1} 格 ×1 ({n + 1}/{ones})", cancellationToken);
                    await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                }
            }
        }
    }

    private async Task EnsureMultiplierAsync(
        GameWindow window, MultiplierState target, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            MultiplierState? current = await ReadMultiplierAsync(window, cancellationToken);
            if (current == target)
            {
                _log($"倍率已是 {Describe(target)}。");
                return;
            }

            window = await _screen.ClickAsync(
                window, _config.SettlementMultiplierToggle,
                $"切换倍率 → {Describe(target)}（当前 {(current is null ? "未知" : Describe(current.Value))}）",
                cancellationToken);
            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }

        MultiplierState? finalState = await ReadMultiplierAsync(window, cancellationToken);
        if (finalState != target)
            _log($"警告：未能切换到倍率 {Describe(target)}，当前为 {(finalState is null ? "未知" : Describe(finalState.Value))}。");
    }

    private async Task<MultiplierState?> ReadMultiplierAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult x1 = await _screen.ProbeAsync(
            window, _multiplierX1Matcher, _config.SettlementMultiplierTopLeft,
            _config.SettlementMultiplierSize, cancellationToken);
        TemplateProbeResult x10 = await _screen.ProbeAsync(
            window, _multiplierX10Matcher, _config.SettlementMultiplierTopLeft,
            _config.SettlementMultiplierSize, cancellationToken);
        TemplateProbeResult max = await _screen.ProbeAsync(
            window, _multiplierMaxMatcher, _config.SettlementMultiplierTopLeft,
            _config.SettlementMultiplierSize, cancellationToken);

        var candidates = new List<(MultiplierState State, double Score)>();
        if (x1.IsMatch) candidates.Add((MultiplierState.X1, x1.Score));
        if (x10.IsMatch) candidates.Add((MultiplierState.X10, x10.Score));
        if (max.IsMatch) candidates.Add((MultiplierState.Max, max.Score));
        if (candidates.Count == 0)
            return null;
        return candidates.OrderByDescending(c => c.Score).First().State;
    }

    private async Task<bool> IsBuySlotBlockedAsync(
        GameWindow window, ConfigPoint button, CancellationToken cancellationToken)
    {
        ConfigPoint inset = _config.SettlementBuyButtonSearchInset;
        ConfigSize size = _config.SettlementBuyButtonSearchSize;
        var topLeft = new ConfigPoint(button.X - inset.X, button.Y - inset.Y);
        TemplateProbeResult lvMax = await _screen.ProbeAsync(
            window, _lvMaxMatcher, topLeft, size, cancellationToken);
        if (lvMax.IsMatch)
            return true;

        TemplateProbeResult disabled = await _screen.ProbeAsync(
            window, _buyDisabledMatcher, topLeft, size, cancellationToken);
        return disabled.IsMatch;
    }

    private async Task HandleLeftoverConfirmAsync(GameWindow window, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 3000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateProbeResult confirm = await _screen.ProbeAsync(
                window, _confirmMatcher, _config.SettlementConfirmTopLeft,
                _config.SettlementConfirmSize, cancellationToken);
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
