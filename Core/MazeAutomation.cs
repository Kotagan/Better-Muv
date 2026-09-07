using System.Windows.Media.Imaging;
using System.Windows;
using System.Diagnostics;
using BetterMuv.Services;

namespace BetterMuv.Core;

public sealed class MazeAutomation
{
    private const string ThirdTaskName = "识别迷宫开始界面";
    private const string FourthTaskName = "识别探索准备界面";
    private const string FifthTaskName = "识别下一步";
    private const string PartnerSelectionTaskName = "识别选择伙伴/商店界面";
    private const string BattleSkipTaskName = "识别战斗界面";
    private const string EventChoiceTaskName = "识别事件选择界面";
    private const string SettlementTaskName = "识别迷宫结算界面";
    private const string TreasureTaskName = "识别宝物选择状态";
    private const string RouteSelectionTaskName = "识别路线选择界面";
    /// <summary>迷宫内界面存在判定阈值（略低于点击用 MatchThreshold，避免开局入环后空转）。</summary>
    private const double MazePresenceThreshold = 0.68;
    /// <summary>下一步（战斗结算 / SEARCH RESULTS）略放宽，避免结算页粉钮因背景裁切漏检。</summary>
    private const double NextPresenceThreshold = 0.58;
    /// <summary>路线选择标题判定阈值（需更高，避免主界面等误匹配）。</summary>
    private const double RoutePresenceThreshold = 0.72;
    /// <summary>探索按钮（クエスト準備右下「探索」）略放宽。</summary>
    private const double FourthPresenceThreshold = 0.68;
    /// <summary>自动校准命中阈值（可低于点击阈值，用偏移上限防止拉飞）。</summary>
    private const double ViewportCalibrateThreshold = 0.60;
    /// <summary>单次校准偏移上限（像素）；超出视为误匹配，拒绝应用。</summary>
    private const int MaxCalibrationAbsOffset = 120;
    /// <summary>迷宫探索：持续全未命中超过此时长则停止（对齐 1.0.0 的 10s 容忍）。</summary>
    private const int MazeMissTimeoutMs = 10000;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly TemplateMatcher _firstMatcher;
    private readonly TemplateMatcher _thirdMatcher;
    private readonly TemplateMatcher _fourthMatcher;
    private readonly TemplateMatcher _fifthMatcher;
    private readonly TemplateMatcher _partnerSelectionMatcher;
    private readonly TemplateMatcher _battleSkipMatcher;
    private readonly PromoPopupDismisser _promoPopup;
    private readonly TemplateMatcher _eventChoiceMatcher;
    private readonly SettlementShopRunner _settlementShop;
    private readonly MazeDifficultyRunner _difficulty;
    private readonly TemplateMatcher _treasureStateMatcher;
    private readonly TemplateMatcher _routeSelectionMatcher;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TemplateMatcher>> _treasureMatchers;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TemplateMatcher>> _routeTreasureMatchers;
    private readonly Action<string> _log;

    public MazeAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        string templateDirectory = TemplateAssets.DirectoryPath;
        _firstMatcher = TemplateAssets.Load("quest.png");
        _thirdMatcher = TemplateAssets.Load("exploration-ready.png");
        _fourthMatcher = TemplateAssets.Load("exploration-action.png");
        _fifthMatcher = TemplateAssets.Load("maze-next.png");
        _partnerSelectionMatcher = TemplateAssets.Load("partner-leave.png");
        _battleSkipMatcher = TemplateAssets.Load("battle-skip.png");
        _promoPopup = new PromoPopupDismisser(config, _screen, log);
        _eventChoiceMatcher = TemplateAssets.Load("event-choice.png");
        _settlementShop = new SettlementShopRunner(config, _screen, templateDirectory, log);
        _difficulty = new MazeDifficultyRunner(config, _screen, log);
        _treasureStateMatcher = TemplateAssets.Load("treasure-state.png");
        _routeSelectionMatcher = TemplateAssets.Load("route-selection.png");
        // 宝物选择用大图标；路线预览图标更小，用 route-* / treasure-sword（小剑）。
        _treasureMatchers = new Dictionary<string, IReadOnlyList<TemplateMatcher>>(StringComparer.OrdinalIgnoreCase)
        {
            ["diamond"] = [TemplateAssets.Load("treasure-diamond.png")],
            ["skull"] = [TemplateAssets.Load("treasure-skull.png")],
            ["sparkle"] = [TemplateAssets.Load("treasure-sparkle.png")],
            ["shield"] = [TemplateAssets.Load("treasure-shield.png")],
            ["sword"] = [TemplateAssets.Load("treasure-greatsword.png")],
            ["shoe"] = [TemplateAssets.Load("treasure-shoe.png")],
            ["heart"] =
            [
                TemplateAssets.Load("treasure-heart.png"),
                TemplateAssets.Load("treasure-heart-half.png")
            ]
        };
        _routeTreasureMatchers = new Dictionary<string, IReadOnlyList<TemplateMatcher>>(StringComparer.OrdinalIgnoreCase)
        {
            ["diamond"] = [TemplateAssets.Load("route-diamond.png")],
            ["skull"] = [TemplateAssets.Load("route-skull.png")],
            ["sparkle"] = [TemplateAssets.Load("route-sparkle.png")],
            ["shield"] = [TemplateAssets.Load("route-shield.png")],
            ["sword"] = [TemplateAssets.Load("treasure-sword.png")],
            ["shoe"] = [TemplateAssets.Load("route-shoe.png")],
            ["heart"] = [TemplateAssets.Load("route-heart.png")]
        };
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"已找到窗口：{window.Title}");
        bool focused = await _screen.FocusAsync(window.Handle, cancellationToken);
        window = _screen.Refresh(window);
        if (!focused)
        {
            _log("未能将游戏置于前台（可能已最小化）。请先手动点一下游戏窗口恢复完整全屏后再启动。");
            return;
        }
        _log("游戏已置于前台，等待界面稳定后再截图识别。");
        await Task.Delay(600, cancellationToken);
        window = _screen.Refresh(window);
        _log("开始截图识别。");

        _screen.EnsureUsableViewport(window);
        _log($"客户区：screen({window.ClientRect.Left},{window.ClientRect.Top}) " +
             $"{window.ClientRect.Width}×{window.ClientRect.Height}");
        ScreenRect viewport = _screen.Viewport(window);
        _log($"坐标基准：{(_config.WindowSelectionMode == "selected" ? "选择窗口/居中16:9画布" : "游戏窗口/居中16:9画布")} " +
             $"screen({viewport.Left},{viewport.Top}) {viewport.Width}×{viewport.Height}");
        // 难度选择/探索准备页也有房子按钮；已在这些页则不要点回去。
        TemplateProbeResult alreadyOnDifficulty = await _screen.ProbeAsync(
            window, _thirdMatcher, _config.ThirdSearchTopLeft, _config.ThirdSearchSize,
            cancellationToken, _config.MatchThreshold);
        TemplateProbeResult alreadyOnExplorePrep = await _screen.ProbeAsync(
            window, _fourthMatcher, _config.FourthSearchTopLeft, _config.FourthSearchSize,
            cancellationToken, FourthPresenceThreshold);
        if (alreadyOnDifficulty.IsMatch)
            _log($"已在迷宫开始/难度选择界面（{alreadyOnDifficulty.Score:F2}），跳过返回主页。");
        else if (alreadyOnExplorePrep.IsMatch)
            _log($"已在探索准备界面（{alreadyOnExplorePrep.Score:F2}），跳过返回主页。");
        else
            await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        int completedRuns = 0;
        string limitText = _config.MazeRunLimit == 0 ? "无限" : _config.MazeRunLimit.ToString();
        _log($"迷宫轮次配置：{limitText}。");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _log(completedRuns == 0
                ? "开始识别当前界面并进入本轮迷宫。"
                : $"开始第 {completedRuns + 1} 轮：结算后按当前界面继续（难度页/探索准备）。");

            bool entered;
            if (completedRuns == 0)
            {
                entered = await IdentifyAndEnterMazeAsync(window, cancellationToken);
            }
            else
            {
                // 商店「完了」后固定进入クエスト準備，直接点右下「探索」，不回主页识别。
                entered = await ContinueFromExplorePrepAsync(window, cancellationToken);
            }

            if (!entered)
            {
                _log("本轮未能进入/完成迷宫，尝试全场景识别再续一轮。");
                entered = await IdentifyAndEnterMazeAsync(window, cancellationToken);
            }

            if (!entered)
            {
                _log("识别流程已停止，结束本次运行。");
                return;
            }
            completedRuns++;
            _log($"第 {completedRuns} 轮迷宫已结算完成。");

            if (_config.MazeRunLimit > 0 && completedRuns >= _config.MazeRunLimit)
            {
                _log($"已达到配置次数 {_config.MazeRunLimit}，停止。");
                return;
            }

            _log(_config.MazeRunLimit == 0
                ? "配置为无限轮次，点击探索进入下一轮。"
                : $"未达上限（{completedRuns}/{_config.MazeRunLimit}），点击探索进入下一轮。");
        }
    }

    /// <summary>
    /// 结算「完了」后可能停在探索准备（探索）或迷宫开始（探索準備/难度）。
    /// 按当前界面推进，不要写死只点探索。
    /// </summary>
    private async Task<bool> ContinueFromExplorePrepAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        TemplateProbeResult onPrep = await _screen.ProbeAsync(
            window, _fourthMatcher, _config.FourthSearchTopLeft, _config.FourthSearchSize,
            cancellationToken, FourthPresenceThreshold);
        TemplateProbeResult onStart = await _screen.ProbeAsync(
            window, _thirdMatcher, _config.ThirdSearchTopLeft, _config.ThirdSearchSize,
            cancellationToken, _config.MatchThreshold);

        if (onPrep.IsMatch && (!onStart.IsMatch || onPrep.Score >= onStart.Score))
        {
            _log($"结算后在探索准备界面（{onPrep.Score:F3}），点击探索。");
            await _screen.ClickAsync(window, _config.FourthClick, FourthTaskName, cancellationToken);
            await Task.Delay(600, cancellationToken);
            _ = await _promoPopup.TryAsync(window, cancellationToken);
            return await RunMazeLoopAsync(window, 0, cancellationToken);
        }

        if (onStart.IsMatch)
        {
            _log($"结算后在迷宫开始/难度界面（{onStart.Score:F3}），点探索準備再进探索。");
            if (!await _difficulty.ApplyAsync(window, cancellationToken))
                _log("难度调整失败，跳过难度仍点击探索準備。");
            await _screen.ClickAsync(window, _config.ThirdClick, "探索準備", cancellationToken);
            await Task.Delay(400, cancellationToken);
            return await RunFromFourthTaskAsync(window, cancellationToken);
        }

        _log("结算后未认出探索准备/迷宫开始，改走全场景识别继续下一轮。");
        return await IdentifyAndEnterMazeAsync(window, cancellationToken);
    }

    /// <summary>关闭挡住迷宫的活动/商店宣传弹窗（右上角白色 X）。</summary>
    private Task<bool> TryDismissPromoPopupAsync(GameWindow window, CancellationToken cancellationToken) =>
        _promoPopup.TryAsync(window, cancellationToken);

    /// <summary>
    /// 迷宫循环全未命中时：若其实停在难度页/探索准备，点进去续跑，而不是空转到超时停死。
    /// </summary>
    private async Task<bool> TryRecoverEntryFromMazeMissAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        TemplateProbeResult onPrep = await _screen.ProbeAsync(
            window, _fourthMatcher, _config.FourthSearchTopLeft, _config.FourthSearchSize,
            cancellationToken, FourthPresenceThreshold);
        TemplateProbeResult onStart = await _screen.ProbeAsync(
            window, _thirdMatcher, _config.ThirdSearchTopLeft, _config.ThirdSearchSize,
            cancellationToken, _config.MatchThreshold);

        if (onPrep.IsMatch && (!onStart.IsMatch || onPrep.Score >= onStart.Score))
        {
            _log($"迷宫未命中但在探索准备（{onPrep.Score:F3}），补点探索。");
            await _screen.ClickAsync(window, _config.FourthClick, FourthTaskName, cancellationToken);
            await Task.Delay(600, cancellationToken);
            _ = await TryDismissPromoPopupAsync(window, cancellationToken);
            return true;
        }

        if (onStart.IsMatch)
        {
            _log($"迷宫未命中但在迷宫开始/难度（{onStart.Score:F3}），补点探索準備。");
            await _screen.ClickAsync(window, _config.ThirdClick, "探索準備", cancellationToken);
            await Task.Delay(500, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task<bool> IdentifyAndEnterMazeAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("定位迷宫内界面；若不在迷宫内则回主页连点 任务→メイズ（不识别任务页）。");
        _screen.ResetCalibration();
        // 仅用无徽章 quest 模板做一次校准；徽章中心偏右上，反复校准会把坐标拉飞。
        if (await TryCalibrateViewportAsync(window, cancellationToken))
            _log("开局已完成画布校准。");

        // 先快速扫一轮迷宫内界面；未命中则立刻连点进关。
        for (int attempt = 0; attempt < 1; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryDismissPromoPopupAsync(window, cancellationToken))
            {
                window = _screen.Refresh(window);
                await Task.Delay(400, cancellationToken);
            }

            IReadOnlyDictionary<string, TemplateProbeResult> probes =
                await ProbeManyConcurrentAsync(window, BuildStartupAllProbes(), cancellationToken);
            string? hit = PickFirstMatchedScreen(probes, StartupScreenPriority);
            if (hit is null)
                break;

            TemplateProbeResult matched = probes[hit];
            switch (hit)
            {
                case "treasure":
                    _log($"{TreasureTaskName}识别成功（{matched.Score:F4}），进入迷宫循环。");
                    return await RunMazeLoopAsync(window, 0, cancellationToken, preferredScreen: "treasure");
                case "route":
                    _log($"{RouteSelectionTaskName}识别成功（{matched.Score:F4}），进入迷宫循环。");
                    return await RunMazeLoopAsync(window, 0, cancellationToken, preferredScreen: "route");
                case "settlement":
                    _log($"{SettlementTaskName}识别成功（{matched.Score:F4}），进入迷宫循环。");
                    return await RunMazeLoopAsync(window, 0, cancellationToken, preferredScreen: "settlement");
                case "partner":
                    _log($"识别到伙伴/商店界面（{matched.Score:F4}），进入迷宫循环。");
                    return await RunMazeLoopAsync(window, 0, cancellationToken, preferredScreen: "partner");
                case "next":
                    _log($"识别到迷宫探索中界面（下一步 {matched.Score:F4}），进入迷宫循环。");
                    return await RunMazeLoopAsync(window, 0, cancellationToken, preferredScreen: "next");
                case "event":
                    _log($"识别到迷宫探索中界面（事件选择 {matched.Score:F4}），进入迷宫循环。");
                    return await RunMazeLoopAsync(window, 0, cancellationToken, preferredScreen: "event");
                case "battle":
                    _log($"识别到迷宫探索中界面（战斗 {matched.Score:F4}），进入迷宫循环。");
                    return await RunMazeLoopAsync(window, 0, cancellationToken, preferredScreen: "battle");
                case "fourth":
                    _log($"{FourthTaskName}识别成功（{matched.Score:F4}），点击并进入迷宫循环。");
                    await _screen.ClickAsync(window, _config.FourthClick, FourthTaskName, cancellationToken);
                    return await RunMazeLoopAsync(window, 0, cancellationToken);
                case "third":
                    _log($"{ThirdTaskName}识别成功（{matched.Score:F4}）。");
                    if (!await _difficulty.ApplyAsync(window, cancellationToken))
                        _log("难度调整失败，跳过难度仍点击探索準備。");
                    await _screen.ClickAsync(window, _config.ThirdClick, "探索準備", cancellationToken);
                    await Task.Delay(250, cancellationToken);
                    return await RunFromFourthTaskAsync(window, cancellationToken);
            }
        }

        _log("不在迷宫内：回主页后连点进メイズ探索。");
        window = await new QuestFromHomeEntry(_config, _screen, _log).RunAsync(
            window, _config.SecondClick, "メイズ探索", cancellationToken);
        if (await RunFromThirdTaskAsync(window, cancellationToken, waitTimeoutMs: 8000))
            return true;

        _log("连点后仍未进入迷宫开始页，按探索准备兜底。");
        return await ContinueFromExplorePrepAsync(window, cancellationToken);
    }

    private async Task<bool> TryCalibrateViewportAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        const int margin = 220;
        ConfigPoint baseTopLeft = _config.SearchTopLeft;
        ConfigSize baseSize = _config.FirstSearchSize;
        int leftMargin = Math.Min(margin, baseTopLeft.X);
        int topMargin = Math.Min(margin, baseTopLeft.Y);
        ConfigPoint expandedTopLeft = new(
            baseTopLeft.X - leftMargin,
            baseTopLeft.Y - topMargin);
        ConfigSize expandedSize = new(
            baseSize.Width + leftMargin + margin,
            baseSize.Height + topMargin + margin);
        // 开局校准只用无徽章 quest.png，避免粉色徽章中心把偏移算偏。
        RegionCapture region = _screen.CaptureRegion(
            window, expandedTopLeft, expandedSize, _firstMatcher);
        TemplateMatchResult match = await Task.Run(
            () => _screen.Match(_firstMatcher, region.Image, region.LogicalWidth, region.LogicalHeight),
            cancellationToken);
        if (match.Score < ViewportCalibrateThreshold)
            return false;

        CaptureGeometry geometry = _screen.Geometry(window);
        Point actualCenter = _screen.MatchCenterToScreen(region, match);
        Point expectedCenter = geometry.ToScreen(new ConfigPoint(
            baseTopLeft.X + _firstMatcher.LogicalWidth / 2,
            baseTopLeft.Y + _firstMatcher.LogicalHeight / 2));
        int deltaX = (int)Math.Round(actualCenter.X - expectedCenter.X);
        int deltaY = (int)Math.Round(actualCenter.Y - expectedCenter.Y);
        if (Math.Abs(deltaX) < 2 && Math.Abs(deltaY) < 2)
            return true;
        if (Math.Abs(deltaX) > MaxCalibrationAbsOffset || Math.Abs(deltaY) > MaxCalibrationAbsOffset)
        {
            _log($"自动校准放弃：扩大区主页 {match.Score:F4} 但偏移过大 ({deltaX:+#;-#;0},{deltaY:+#;-#;0})，" +
                 $"超过 ±{MaxCalibrationAbsOffset}。");
            return false;
        }

        _screen.CalibrateOffset(deltaX, deltaY);
        _log($"自动校准画布：通过 first 在扩大区域命中 {match.Score:F4}，" +
             $"修正偏移 ({deltaX:+#;-#;0},{deltaY:+#;-#;0})。");
        return true;
    }

    private async Task<bool> RunFromThirdTaskAsync(
        GameWindow window, CancellationToken cancellationToken, int waitTimeoutMs = 3000)
    {
        _log($"开始任务：{ThirdTaskName}。");
        _log($"等待迷宫开始界面，最多静默轮询 {waitTimeoutMs / 1000} 秒。");
        // 先确认在迷宫准备界面，再调难度，最后点探索準備。
        TemplateProbeResult probe = await _screen.WaitForProbeAsync(
            window, _thirdMatcher, _config.ThirdSearchTopLeft, _config.ThirdSearchSize,
            cancellationToken, timeoutMs: waitTimeoutMs, _config.MatchThreshold);
        if (!probe.IsMatch)
        {
            _log($"{ThirdTaskName}轮询 {waitTimeoutMs / 1000} 秒仍未命中（最高 {probe.Score:F4}），交回全场景匹配。");
            return false;
        }

        _log($"{ThirdTaskName}识别成功（{probe.Score:F4}）。");
        if (!await _difficulty.ApplyAsync(window, cancellationToken))
            _log("难度调整失败，跳过难度仍点击探索準備。");
        // ThirdClick = 探索準備 入口；随后再确认探索准备界面。
        await _screen.ClickAsync(window, _config.ThirdClick, "探索準備", cancellationToken);
        await Task.Delay(250, cancellationToken);
        return await RunFromFourthTaskAsync(window, cancellationToken);
    }

    private async Task<bool> RunFromFourthTaskAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log($"开始任务：{FourthTaskName}。");
        // 到了クエスト準備就点右下「探索」：阈值不够也点写死坐标，不停本轮。
        (bool matched, TemplateMatchResult match, _) = await _screen.MatchRegionAsync(
            window, _fourthMatcher, _config.FourthSearchTopLeft, _config.FourthSearchSize,
            cancellationToken, timeoutMs: 1500, matchThreshold: FourthPresenceThreshold);
        if (matched)
            _log($"{FourthTaskName}识别成功（{match.Score:F4}），点击探索。");
        else
            _log($"{FourthTaskName}分数 {match.Score:F4} < {FourthPresenceThreshold:F2}，仍点击写死点。");
        await _screen.ClickAsync(window, _config.FourthClick, FourthTaskName, cancellationToken);
        await Task.Delay(250, cancellationToken);
        return await RunMazeLoopAsync(window, 0, cancellationToken);
    }

    private static readonly string[] StartupScreenPriority =
    [
        // 不再识别主页/任务选择页；入口靠回主页后固定连点。
        "third", "fourth",
        "treasure", "route", "settlement", "partner", "next", "event", "battle"
    ];

    private static readonly string[] MazeExplorationScreenPriority =
    [
        // 先点「次へ」，消失后再判战斗/事件/伙伴/商店/宝物/分支。
        "next", "battle", "event", "partner", "settlement", "treasure", "route"
    ];

    private List<TemplateProbe> BuildMazeExplorationProbes() =>
    [
        new("battle", _battleSkipMatcher, _config.BattleSkipTopLeft, _config.BattleSkipSize, MazePresenceThreshold),
        new("event", _eventChoiceMatcher, _config.EventChoiceTopLeft, _config.EventChoiceSize, MazePresenceThreshold),
        new("partner", _partnerSelectionMatcher, _config.PartnerSelectionTopLeft, _config.PartnerSelectionSize,
            MazePresenceThreshold),
        new("settlement", _settlementShop.SettlementMatcher,
            new ConfigPoint(
                Math.Max(0, _config.SettlementSearchTopLeft.X - 40),
                Math.Max(0, _config.SettlementSearchTopLeft.Y - 40)),
            new ConfigSize(
                Math.Max(_config.SettlementSearchSize.Width + 80, 360),
                Math.Max(_config.SettlementSearchSize.Height + 80, 180)),
            // 战斗完成页「次へ」区也会弱匹配完了；须明显高于误检档。
            0.72),
        new("treasure", _treasureStateMatcher, _config.TreasureStateTopLeft, _config.TreasureStateSize,
            MazePresenceThreshold),
        new("route", _routeSelectionMatcher,
            new ConfigPoint(
                _config.RouteSelectionTopLeft.X,
                _config.RouteSelectionTopLeft.Y),
            new ConfigSize(
                _config.RouteSelectionSize.Width,
                _config.RouteSelectionSize.Height),
            RoutePresenceThreshold),
        // 右下角「次へ」：战斗胜利 / SEARCH RESULTS 通用，ROI 加大避免裁切与布局偏移。
        new("next", _fifthMatcher,
            new ConfigPoint(
                Math.Max(0, _config.FifthSearchTopLeft.X - 40),
                Math.Max(0, _config.FifthSearchTopLeft.Y - 40)),
            new ConfigSize(
                Math.Max(_config.FifthSearchSize.Width + 80, 360),
                Math.Max(_config.FifthSearchSize.Height + 80, 180)),
            NextPresenceThreshold)
    ];

    private List<TemplateProbe> BuildStartupAllProbes()
    {
        // 开局只认迷宫内界面；主页/任务页不识别，靠 HudHome + FirstClick + SecondClick 连点进入。
        var list = BuildMazeExplorationProbes();
        double entryThreshold = _config.MatchThreshold;
        list.Add(new("third", _thirdMatcher, _config.ThirdSearchTopLeft, _config.ThirdSearchSize, entryThreshold));
        list.Add(new("fourth", _fourthMatcher, _config.FourthSearchTopLeft, _config.FourthSearchSize,
            FourthPresenceThreshold));
        return list;
    }

    private static string? PickFirstMatchedScreen(
        IReadOnlyDictionary<string, TemplateProbeResult> probes, IReadOnlyList<string> priority)
    {
        foreach (string key in priority)
        {
            if (probes.TryGetValue(key, out TemplateProbeResult? probe) && probe.IsMatch)
                return key;
        }
        return null;
    }

    private static TemplateProbeResult GetProbe(
        IReadOnlyDictionary<string, TemplateProbeResult> probes, string key) =>
        probes.TryGetValue(key, out TemplateProbeResult? probe)
            ? probe
            : new TemplateProbeResult(false, 0, new Point());

    private async Task<bool> RunMazeLoopAsync(
        GameWindow window, int mazeLoopCount, CancellationToken cancellationToken,
        string? preferredScreen = null)
    {
        _log("进入迷宫循环：探索场景并发匹配（战斗/事件/伙伴/结算/宝物/路线/下一步）。");
        bool treasureHandled = false;
        bool routeHandled = false;
        bool nextHandled = false;
        bool battleSkipHandled = false;
        bool eventChoiceHandled = false;
        bool preferOnce = preferredScreen is not null;
        Stopwatch? missClock = null;
        Stopwatch? noProgressClock = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (preferOnce)
            {
                preferOnce = false;
                IReadOnlyDictionary<string, TemplateProbeResult> preferredProbes =
                    await ProbeManyConcurrentAsync(window, BuildMazeExplorationProbes(), cancellationToken);
                if (preferredScreen is not null &&
                    preferredProbes.TryGetValue(preferredScreen, out TemplateProbeResult? preferredHit) &&
                    preferredHit.IsMatch)
                {
                    MazeHandleResult handledPreferred = await HandleMazeProbesAsync(
                        window, preferredProbes, preferredScreen, mazeLoopCount,
                        treasureHandled, routeHandled, nextHandled, battleSkipHandled, eventChoiceHandled,
                        cancellationToken);
                    mazeLoopCount = handledPreferred.MazeLoopCount;
                    treasureHandled = handledPreferred.TreasureHandled;
                    routeHandled = handledPreferred.RouteHandled;
                    nextHandled = handledPreferred.NextHandled;
                    battleSkipHandled = handledPreferred.BattleSkipHandled;
                    eventChoiceHandled = handledPreferred.EventChoiceHandled;
                    if (handledPreferred.ExitLoop)
                        return true;
                    if (handledPreferred.Handled)
                    {
                        missClock = null;
                        noProgressClock = null;
                        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                        continue;
                    }
                }
            }

            IReadOnlyDictionary<string, TemplateProbeResult> probes =
                await ProbeManyConcurrentAsync(window, BuildMazeExplorationProbes(), cancellationToken);
            bool anyMatch = probes.Values.Any(p => p.IsMatch);
            if (!anyMatch)
            {
                // 全未命中时清空已处理标记，避免「下一步」等短暂消失后再现时只空转不点。
                treasureHandled = false;
                routeHandled = false;
                nextHandled = false;
                battleSkipHandled = false;
                eventChoiceHandled = false;

                bool firstMiss = missClock is null;
                missClock ??= Stopwatch.StartNew();
                // 只在未命中刚开始时截一次诊断图。
                if (_config.SaveDiagnostics && firstMiss)
                    await SaveMazeMissDiagnosticsAsync(window, probes, cancellationToken);

                // 周年活动/商店宣传弹窗会挡住迷宫；先关 X 再继续匹配。
                if (await TryDismissPromoPopupAsync(window, cancellationToken))
                {
                    missClock = null;
                    noProgressClock = null;
                    await Task.Delay(400, cancellationToken);
                    continue;
                }

                // 结算后常回到难度页/探索准备，迷宫循环认不出 → 在此续航，避免 10s 后整轮停死。
                if (await TryRecoverEntryFromMazeMissAsync(window, cancellationToken))
                {
                    missClock = null;
                    noProgressClock = null;
                    continue;
                }

                TemplateProbeResult battle = GetProbe(probes, "battle");
                TemplateProbeResult treasure = GetProbe(probes, "treasure");
                TemplateProbeResult route = GetProbe(probes, "route");
                TemplateProbeResult next = GetProbe(probes, "next");
                TemplateProbeResult partner = GetProbe(probes, "partner");
                _log($"迷宫探索场景均未命中（已未命中 {missClock.Elapsed.TotalSeconds:F1}s / {MazeMissTimeoutMs / 1000.0:0.#}s）。" +
                     $"（战斗 {battle.Score:F2} / 宝物 {treasure.Score:F2} / 路线 {route.Score:F2} / " +
                     $"下一步 {next.Score:F2} / 伙伴 {partner.Score:F2}）");
                if (missClock.ElapsedMilliseconds >= MazeMissTimeoutMs)
                {
                    _log($"迷宫探索持续 {MazeMissTimeoutMs / 1000.0:0.#} 秒未命中，停止。");
                    return false;
                }
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            missClock = null;
            string? screen = PickFirstMatchedScreen(probes, MazeExplorationScreenPriority);
            MazeHandleResult result = await HandleMazeProbesAsync(
                window, probes, screen, mazeLoopCount,
                treasureHandled, routeHandled, nextHandled, battleSkipHandled, eventChoiceHandled,
                cancellationToken);
            mazeLoopCount = result.MazeLoopCount;
            treasureHandled = result.TreasureHandled;
            routeHandled = result.RouteHandled;
            nextHandled = result.NextHandled;
            battleSkipHandled = result.BattleSkipHandled;
            eventChoiceHandled = result.EventChoiceHandled;
            if (result.ExitLoop)
                return true;
            if (result.Handled)
            {
                noProgressClock = null;
            }
            else
            {
                // 有弱匹配但本轮没点成：先清 handled 再试，不要像硬停那样 5s 就结束。
                noProgressClock ??= Stopwatch.StartNew();
                if (noProgressClock.ElapsedMilliseconds >= MazeMissTimeoutMs)
                {
                    TemplateProbeResult t = GetProbe(probes, "treasure");
                    TemplateProbeResult r = GetProbe(probes, "route");
                    _log($"迷宫探索 {MazeMissTimeoutMs / 1000.0:0.#} 秒无有效操作（当前屏={screen ?? "?"} " +
                         $"宝物 {t.Score:F2} / 路线 {r.Score:F2}），清空标记后继续。");
                    treasureHandled = false;
                    routeHandled = false;
                    nextHandled = false;
                    battleSkipHandled = false;
                    eventChoiceHandled = false;
                    noProgressClock = null;
                }
            }
            // 有匹配后稍候再探，避免点完立刻连点。
            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
    }

    private readonly record struct MazeHandleResult(
        bool Handled,
        bool ExitLoop,
        int MazeLoopCount,
        bool TreasureHandled,
        bool RouteHandled,
        bool NextHandled,
        bool BattleSkipHandled,
        bool EventChoiceHandled);

    private async Task<MazeHandleResult> HandleMazeProbesAsync(
        GameWindow window,
        IReadOnlyDictionary<string, TemplateProbeResult> probes,
        string? screen,
        int mazeLoopCount,
        bool treasureHandled,
        bool routeHandled,
        bool nextHandled,
        bool battleSkipHandled,
        bool eventChoiceHandled,
        CancellationToken cancellationToken)
    {
        MazeHandleResult State(bool handled, bool exit = false) =>
            new(handled, exit, mazeLoopCount, treasureHandled, routeHandled, nextHandled,
                battleSkipHandled, eventChoiceHandled);

        bool Is(string name) =>
            screen is null || screen.Equals(name, StringComparison.OrdinalIgnoreCase);

        TemplateProbeResult Probe(string key) => GetProbe(probes, key);

        // 有「次へ」只点下一步并结束本轮；消失后再判分支/商店等。
        // 识别只确认界面；点击一律用配置写死点（对齐 1.0.0，避免模板中心偏出按钮）。
        TemplateProbeResult nextProbe = Probe("next");
        if (nextProbe.IsMatch)
        {
            if (!nextHandled)
            {
                _log($"{FifthTaskName}识别成功（{nextProbe.Score:F4}），点击写死点。");
                await _screen.ClickAsync(window, _config.FifthClick, FifthTaskName, cancellationToken);
                mazeLoopCount++;
                _log($"{FifthTaskName}已执行 {mazeLoopCount} 次。");
                await Task.Delay(900, cancellationToken);

                TemplateProbeResult stillNext = await _screen.ProbeAsync(
                    window, _fifthMatcher,
                    new ConfigPoint(Math.Max(0, _config.FifthSearchTopLeft.X - 40),
                        Math.Max(0, _config.FifthSearchTopLeft.Y - 40)),
                    new ConfigSize(Math.Max(_config.FifthSearchSize.Width + 80, 360),
                        Math.Max(_config.FifthSearchSize.Height + 80, 180)),
                    cancellationToken, NextPresenceThreshold);
                // 只有达到存在阈值才算「仍在」；弱分不当残留、也不再二次乱点。
                if (stillNext.IsMatch)
                {
                    _log($"下一步仍在（{stillNext.Score:F3}），再点一次写死点。");
                    await _screen.ClickAsync(window, _config.FifthClick, "下一步(再点)", cancellationToken);
                    await Task.Delay(600, cancellationToken);
                    stillNext = await _screen.ProbeAsync(
                        window, _fifthMatcher,
                        new ConfigPoint(Math.Max(0, _config.FifthSearchTopLeft.X - 40),
                            Math.Max(0, _config.FifthSearchTopLeft.Y - 40)),
                        new ConfigSize(Math.Max(_config.FifthSearchSize.Width + 80, 360),
                            Math.Max(_config.FifthSearchSize.Height + 80, 180)),
                        cancellationToken, NextPresenceThreshold);
                }

                nextHandled = !stillNext.IsMatch;
                if (stillNext.IsMatch)
                    _log("下一步仍在，稍后继续点。");
                else
                    _log("下一步已消失（或弱于阈值），下轮再判分支/商店等。");
            }
            else
            {
                nextHandled = false;
            }

            return State(true);
        }

        nextHandled = false;

        if (Is("battle"))
        {
            TemplateProbeResult battleSkipProbe = Probe("battle");
            if (battleSkipProbe.IsMatch)
            {
                if (!battleSkipHandled)
                    _log($"{BattleSkipTaskName}识别成功（{battleSkipProbe.Score:F4}），点击 SKIP。");
                await _screen.ClickAsync(window, _config.BattleSkipClick, "战斗 SKIP", cancellationToken);
                battleSkipHandled = true;
                return State(true);
            }
            if (screen is not null)
                return State(false);
            battleSkipHandled = false;
        }

        if (Is("event"))
        {
            TemplateProbeResult eventChoiceProbe = Probe("event");
            if (eventChoiceProbe.IsMatch)
            {
                if (!eventChoiceHandled)
                {
                    _log($"{EventChoiceTaskName}识别成功（{eventChoiceProbe.Score:F4}），默认选择第二选项。");
                    window = await _screen.ClickAsync(
                        window, _config.EventChoiceSecondOption, "事件第二选项", cancellationToken);
                    eventChoiceHandled = true;
                }
                return State(true);
            }
            if (screen is not null)
                return State(false);
            eventChoiceHandled = false;
        }

        if (Is("partner"))
        {
            TemplateProbeResult partnerProbe = Probe("partner");
            if (partnerProbe.IsMatch)
            {
                _log($"{PartnerSelectionTaskName}识别成功（{partnerProbe.Score:F4}），点击离开。");
                await _screen.ClickAsync(window, _config.PartnerClick, PartnerSelectionTaskName, cancellationToken);
                return State(true);
            }
            if (screen is not null)
                return State(false);
        }

        if (Is("treasure"))
        {
            TemplateProbeResult treasureProbe = Probe("treasure");
            if (treasureProbe.IsMatch && !treasureHandled)
            {
                _log($"{TreasureTaskName}识别成功（{treasureProbe.Score:F4}），开始按优先级选择。");
                await SelectTreasureAsync(window, cancellationToken);
                treasureHandled = true;
                _log("宝物选择完成，继续检测后续界面。");
                await Task.Delay(500, cancellationToken);
                TemplateProbeResult treasureAfter = await _screen.ProbeAsync(
                    window, _treasureStateMatcher, _config.TreasureStateTopLeft,
                    _config.TreasureStateSize, cancellationToken, MazePresenceThreshold);
                if (!treasureAfter.IsMatch)
                {
                    treasureHandled = false;
                    return State(true);
                }

                _log("宝物状态仍残留，判定可能未点中，再选一次。");
                await SelectTreasureAsync(window, cancellationToken);
                treasureAfter = await _screen.ProbeAsync(
                    window, _treasureStateMatcher, _config.TreasureStateTopLeft,
                    _config.TreasureStateSize, cancellationToken, MazePresenceThreshold);
                if (!treasureAfter.IsMatch)
                    treasureHandled = false;
                else
                {
                    // 仍在宝物页：清掉 handled，下轮继续选，避免空转 5 秒后停。
                    treasureHandled = false;
                    _log("宝物状态仍残留，下轮继续选。");
                }
                return State(true);
            }
            if (!treasureProbe.IsMatch)
                treasureHandled = false;
            // 已 handled 但仍判为宝物时不 return false，让下轮能再次进入选择。
        }

        if (Is("route"))
        {
            TemplateProbeResult routeProbe = Probe("route");
            if (routeProbe.IsMatch && !routeHandled)
            {
                _log($"{RouteSelectionTaskName}识别成功，开始按优先级选择路线宝物。");
                string? selected = await SelectRouteTreasureAsync(window, cancellationToken);
                if (selected is null)
                    _log("路线区域未找到已提供的宝物模板，直接选择路线。");
                else
                    _log($"已选中路线宝物：{selected}，随后选择路线。");
                // 识别只确认界面；点击一律用配置写死点（对齐 1.0.0）。
                await _screen.ClickAsync(window, _config.RouteClick, "选择路线", cancellationToken);
                routeHandled = true;
                _log("已点击路线写死点，等待界面响应。");
                await Task.Delay(600, cancellationToken);
                TemplateProbeResult routeAfter = await _screen.ProbeAsync(
                    window, _routeSelectionMatcher, _config.RouteSelectionTopLeft,
                    _config.RouteSelectionSize, cancellationToken, RoutePresenceThreshold);
                routeHandled = false;
                if (routeAfter.IsMatch)
                {
                    _log($"路线按钮仍在（{routeAfter.Score:F3}），再点一次写死点。");
                    await _screen.ClickAsync(window, _config.RouteClick, "路线(再点)", cancellationToken);
                    await Task.Delay(500, cancellationToken);
                }
                else
                {
                    _log("路线按钮已消失（或弱于阈值），继续检测后续界面。");
                }
                return State(true);
            }
            if (screen is not null)
                return State(false);
            if (!routeProbe.IsMatch)
                routeHandled = false;
        }

        if (Is("settlement"))
        {
            TemplateProbeResult settlementProbe = Probe("settlement");
            if (settlementProbe.IsMatch)
            {
                bool left = await _settlementShop.RunAsync(window, cancellationToken);
                if (left)
                {
                    _log("本轮结算完成（商店完了后固定探索准备），结束本轮迷宫循环。");
                    return State(true, exit: true);
                }

                _log("结算界面仍在，稍后重试完了。");
                return State(true);
            }
            if (screen is not null)
                return State(false);
        }

        // 有匹配但都被 handled 跳过时，仍视为本轮已处理，避免误计未命中。
        if (screen is null && MazeExplorationScreenPriority.Any(k => Probe(k).IsMatch))
            return State(true);

        return State(false);
    }

    /// <summary>
    /// 各 ROI 串行截图（GDI），模板匹配并行，避免空等串行探测。
    /// </summary>
    private async Task<Dictionary<string, TemplateProbeResult>> ProbeManyConcurrentAsync(
        GameWindow window,
        IReadOnlyList<TemplateProbe> probeList,
        CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        // 默认游戏使用显示器标定；用户选择的浏览器窗口使用其客户区标定。
        CaptureGeometry geometry = _screen.Geometry(window);
        _screen.EnsureUsableViewport(window);

        // 非 16:9 客户区采用居中 cover 映射，虚拟画布可能超出客户区，
        // 所以各 ROI 直接按换算后的屏幕坐标截取，不能再从客户区整图按比例裁剪。

        var jobs = new (string Key, TemplateMatcher Matcher, BitmapSource Image, ScreenRect Rect, int Lw, int Lh, double Threshold)[probeList.Count];
        for (int i = 0; i < probeList.Count; i++)
        {
            TemplateProbe probe = probeList[i];
            ConfigPoint topLeft = probe.TopLeft;
            ConfigSize size = ScreenAutomation.EnsureFitsTemplate(probe.Size, probe.Matcher);
            if (size.Width != probe.Size.Width || size.Height != probe.Size.Height)
                _log($"探测 {probe.Key}：搜索区 {probe.Size.Width}×{probe.Size.Height} 小于模板逻辑 " +
                     $"{probe.Matcher.LogicalWidth}×{probe.Matcher.LogicalHeight}，已放大至 {size.Width}×{size.Height}。");
            ScreenRect rect = geometry.RegionFromTopLeftToScreen(topLeft, size);

            RegionCapture region = _screen.CaptureRegion(window, topLeft, size);
            BitmapSource image = region.Image;
            image.Freeze();

            if (_config.SaveDiagnostics &&
                probe.Key is "first" or "second" or "third" or "fourth")
            {
                string diagName = $"probe-{probe.Key}";
                BitmapSource diag = image;
                string key = probe.Key;
                _ = Task.Run(() =>
                {
                    try { SaveDiagnostic(diag, diagName, $"探测-{key}"); }
                    catch { /* 诊断失败不影响主流程 */ }
                });
            }

            jobs[i] = (probe.Key, probe.Matcher, image, region.ScreenRect, region.LogicalWidth, region.LogicalHeight, probe.Threshold);
        }

        Dictionary<string, TemplateProbeResult> dict = await Task.Run(() =>
        {
            var results = new TemplateProbeResult[jobs.Length];
            Parallel.For(0, jobs.Length, i =>
            {
                var job = jobs[i];
                if (job.Image is null)
                {
                    results[i] = new TemplateProbeResult(false, 0, new Point());
                    return;
                }
                TemplateMatchResult match = _screen.Match(job.Matcher, job.Image, job.Lw, job.Lh);
                Point center = _screen.MatchCenterToScreen(geometry, job.Rect, match, job.Lw, job.Lh);
                results[i] = new TemplateProbeResult(match.Score >= job.Threshold, match.Score, center);
            });

            var map = new Dictionary<string, TemplateProbeResult>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < jobs.Length; i++)
                map[jobs[i].Key] = results[i];
            return map;
        }, cancellationToken);

        // 每轮都打出各探测置信度，便于对照阈值。
        var scoreParts = new List<string>(probeList.Count);
        for (int i = 0; i < probeList.Count; i++)
        {
            TemplateProbe probe = probeList[i];
            if (!dict.TryGetValue(probe.Key, out TemplateProbeResult? result))
                continue;
            string hit = result.IsMatch ? "命中" : "未命中";
            scoreParts.Add($"{probe.Key}={result.Score:F4}/{probe.Threshold:F2}({hit})");
            if (_config.SaveDiagnostics &&
                probe.Key is "first" or "second" or "third" or "fourth")
            {
                ConfigPoint topLeft = probe.TopLeft;
                ConfigSize size = ScreenAutomation.EnsureFitsTemplate(probe.Size, probe.Matcher);
                ScreenRect rect = geometry.RegionFromTopLeftToScreen(topLeft, size);
                _log($"探测 {probe.Key}：1080p({topLeft.X},{topLeft.Y}) {size.Width}×{size.Height} ×({geometry.ScaleX:F3},{geometry.ScaleY:F3}) → " +
                     $"screen({rect.Left},{rect.Top}) {rect.Width}×{rect.Height}，置信度 {result.Score:F4}（阈值 {probe.Threshold:F2}，{hit}）");
            }
        }
        if (scoreParts.Count > 0)
            _log("本轮置信度：" + string.Join(" | ", scoreParts));

        return dict;
    }

    /// <summary>迷宫未命中时保存各探索 ROI 诊断图，便于核对截图位置。</summary>
    private async Task SaveMazeMissDiagnosticsAsync(
        GameWindow window,
        IReadOnlyDictionary<string, TemplateProbeResult> probes,
        CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        CaptureGeometry geometry = _screen.Geometry(window);
        BitmapSource fullClient = _screen.CaptureClient(window);
        fullClient.Freeze();
        _ = Task.Run(() =>
        {
            try { SaveDiagnostic(fullClient, "maze-miss-full", "迷宫未命中整图"); }
            catch { }
        });

        foreach (TemplateProbe probe in BuildMazeExplorationProbes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigPoint topLeft = probe.TopLeft;
            ConfigSize size = probe.Size;
            ScreenRect rect = geometry.RegionFromTopLeftToScreen(topLeft, size);
            RegionCapture region = _screen.CaptureRegion(window, topLeft, size);
            BitmapSource image = region.Image;
            image.Freeze();
            double score = probes.TryGetValue(probe.Key, out TemplateProbeResult? p) ? p.Score : 0;
            string diagName = $"maze-miss-{probe.Key}";
            BitmapSource diag = image;
            string key = probe.Key;
            _ = Task.Run(() =>
            {
                try { SaveDiagnostic(diag, diagName, $"迷宫未命中-{key}"); }
                catch { }
            });
            _log($"迷宫未命中诊断 {probe.Key}：1080p({topLeft.X},{topLeft.Y}) {size.Width}×{size.Height} → " +
                 $"screen({rect.Left},{rect.Top}) {rect.Width}×{rect.Height}，分数 {score:F2}");
        }

        await Task.CompletedTask;
    }

    private async Task SelectTreasureAsync(GameWindow window, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= _config.TreasureMatchRetryCount; attempt++)
        {
            string? selected = await SelectPriorityTreasureAsync(
                window, _config.TreasureOptionsTopLeft, _config.TreasureOptionsSize,
                _treasureMatchers, cancellationToken, _config.TreasureClickOffset,
                diagnosticName: attempt == 1 ? "treasure-options" : null,
                taskName: TreasureTaskName,
                matchThreshold: _config.TreasureMatchThreshold);
            if (selected is not null)
            {
                _log($"选择宝物：{selected}");
                return;
            }

            if (attempt < _config.TreasureMatchRetryCount)
                _log($"宝物未匹配（{attempt}/{_config.TreasureMatchRetryCount}），立即重试。");
        }

        _log("当前已提供的普通宝物模板均未匹配，默认点击第一项。");
        await ClickFirstTreasureOptionAsync(window, cancellationToken);
        _log("选择宝物：first（默认）");
    }

    /// <summary>三选一大图区域左侧第一张卡中心（1080p），再叠加 TreasureClickOffset。</summary>
    private async Task ClickFirstTreasureOptionAsync(GameWindow window, CancellationToken cancellationToken)
    {
        int cardCenterX = _config.TreasureOptionsTopLeft.X + _config.TreasureOptionsSize.Width / 6;
        int cardCenterY = _config.TreasureOptionsTopLeft.Y + _config.TreasureOptionsSize.Height / 2;
        var target = new ConfigPoint(
            cardCenterX + _config.TreasureClickOffset.X,
            cardCenterY + _config.TreasureClickOffset.Y);
        await _screen.ClickAsync(window, target, "宝物默认第一项", cancellationToken);
    }

    /// <summary>
    /// 路线预览区拆成上/中/下三块小图匹配，按优先级尽早命中即返回。
    /// </summary>
    private async Task<string?> SelectRouteTreasureAsync(GameWindow window, CancellationToken cancellationToken)
    {
        double threshold = _config.TreasureMatchThreshold;
        const int overlap = 20; // 1080p
        ConfigPoint fullTopLeft = _config.RouteTreasureOptionsTopLeft;
        ConfigSize fullSize = _config.RouteTreasureOptionsSize;
        int bandHeight = Math.Max(1, fullSize.Height / 3);

        var bands = new (string Name, ConfigPoint TopLeft, ConfigSize Size)[3];
        for (int i = 0; i < 3; i++)
        {
            int y = fullTopLeft.Y + i * bandHeight - (i == 0 ? 0 : overlap);
            int height = bandHeight + (i == 0 || i == 2 ? overlap : overlap * 2);
            if (i == 2)
                height = fullTopLeft.Y + fullSize.Height - y;
            height = Math.Max(1, height);
            string name = i switch { 0 => "上", 1 => "中", _ => "下" };
            bands[i] = (name, new ConfigPoint(fullTopLeft.X, y), new ConfigSize(fullSize.Width, height));
        }

        _log($"{RouteSelectionTaskName}分带搜索：1080p {fullSize.Width}×{fullSize.Height} → 上中下三块，阈值 {threshold:F2}");

        window = _screen.Refresh(window);
        CaptureGeometry geometry = _screen.Geometry(window);
        var bandRects = new ScreenRect[3];
        var logicalWidths = new int[3];
        var logicalHeights = new int[3];
        var bandGrays = new byte[3][];
        for (int i = 0; i < 3; i++)
        {
            RegionCapture band = _screen.CaptureRegion(window, bands[i].TopLeft, bands[i].Size);
            bandRects[i] = band.ScreenRect;
            logicalWidths[i] = band.LogicalWidth;
            logicalHeights[i] = band.LogicalHeight;
            BitmapSource bandImage = band.Image;
            if (_config.SaveDiagnostics)
            {
                bandImage.Freeze();
                string diagName = $"route-treasure-{bands[i].Name}";
                BitmapSource diag = bandImage;
                _ = Task.Run(() =>
                {
                    try { SaveDiagnostic(diag, diagName, RouteSelectionTaskName); }
                    catch { }
                });
            }
            bandGrays[i] = TemplateMatcher.PrepareGray(bandImage, logicalWidths[i], logicalHeights[i]);
        }

        var orderedKeys = _config.TreasurePriority
            .Concat(_routeTreasureMatchers.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string key in orderedKeys)
        {
            if (!_routeTreasureMatchers.TryGetValue(key, out IReadOnlyList<TemplateMatcher>? keyMatchers))
                continue;

            var jobs = new List<(int Band, TemplateMatcher Matcher)>();
            for (int band = 0; band < 3; band++)
            {
                foreach (TemplateMatcher matcher in keyMatchers)
                {
                    if (matcher.LogicalWidth <= logicalWidths[band] &&
                        matcher.LogicalHeight <= logicalHeights[band])
                        jobs.Add((band, matcher));
                }
            }

            if (jobs.Count == 0)
                continue;

            (int Band, TemplateMatchResult Match, TemplateMatcher Matcher)[] results = await Task.Run(() =>
            {
                var local = new (int Band, TemplateMatchResult Match, TemplateMatcher Matcher)[jobs.Count];
                Parallel.For(0, jobs.Count, i =>
                {
                    (int band, TemplateMatcher matcher) = jobs[i];
                    TemplateMatchResult match = _screen.MatchPrepared(
                        matcher, bandGrays[band], logicalWidths[band], logicalHeights[band]);
                    local[i] = (band, match, matcher);
                });
                return local;
            }, cancellationToken);

            (int BandIndex, TemplateMatchResult Match)? best = null;
            foreach ((int band, TemplateMatchResult match, TemplateMatcher matcher) in results)
            {
                _log($"路线 {bands[band].Name} 宝物 {key} 模板 {matcher.ReferenceWidth}×{matcher.ReferenceHeight} 分数：{match.Score:F4}");
                if (best is null || match.Score > best.Value.Match.Score)
                    best = (band, match);
            }

            if (best is null || best.Value.Match.Score < threshold)
                continue;

            int winBand = best.Value.BandIndex;
            TemplateMatchResult winMatch = best.Value.Match;
            ScreenRect rect = bandRects[winBand];
            Point clickPoint = _screen.MatchCenterToScreen(
                geometry, rect, winMatch, logicalWidths[winBand], logicalHeights[winBand]);
            _log($"优先路线宝物 {key} 匹配成功（{bands[winBand].Name}，{winMatch.Score:F4} ≥ {threshold:F2}），" +
                 $"点击 screen({clickPoint.X:F0},{clickPoint.Y:F0})");
            await _screen.ClickScreenAsync(window, clickPoint, cancellationToken);
            return key;
        }

        return null;
    }

    private async Task<string?> SelectPriorityTreasureAsync(
        GameWindow window,
        ConfigPoint optionsTopLeft,
        ConfigSize optionsSize,
        IReadOnlyDictionary<string, IReadOnlyList<TemplateMatcher>> matchers,
        CancellationToken cancellationToken,
        ConfigPoint? clickOffset = null,
        string? diagnosticName = null,
        string? taskName = null,
        double? matchThreshold = null)
    {
        double threshold = matchThreshold ?? _config.MatchThreshold;
        window = _screen.Refresh(window);
        RegionCapture region = _screen.CaptureRegion(window, optionsTopLeft, optionsSize);
        CaptureGeometry geometry = region.Geometry;
        ScreenRect rect = region.ScreenRect;
        BitmapSource image = region.Image;
        _log($"{taskName ?? "宝物匹配"}搜索区域：1080p {optionsSize.Width}×{optionsSize.Height} ×({geometry.ScaleX:F3},{geometry.ScaleY:F3}) → " +
             $"screen({rect.Left},{rect.Top}) {rect.Width}×{rect.Height}，阈值 {threshold:F2}");

        int logicalWidth = region.LogicalWidth;
        int logicalHeight = region.LogicalHeight;

        // 诊断图异步落盘，不阻塞匹配/点击。
        if (_config.SaveDiagnostics && !string.IsNullOrWhiteSpace(diagnosticName))
        {
            image.Freeze();
            string diagName = diagnosticName;
            string diagTask = taskName ?? "宝物匹配";
            BitmapSource diagImage = image;
            _ = Task.Run(() =>
            {
                try { SaveDiagnostic(diagImage, diagName, diagTask); }
                catch { /* 诊断失败不影响主流程 */ }
            });
        }

        byte[] sourceGray = TemplateMatcher.PrepareGray(image, logicalWidth, logicalHeight);
        // 按优先级排列，并补上字典中其余模板，避免配置漏项导致骷髅等未匹配。
        var orderedKeys = _config.TreasurePriority
            .Concat(matchers.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matchJobs = new List<(string Key, TemplateMatcher Matcher)>();
        foreach (string key in orderedKeys)
        {
            if (!matchers.TryGetValue(key, out IReadOnlyList<TemplateMatcher>? keyMatchers))
                continue;
            foreach (TemplateMatcher matcher in keyMatchers)
                matchJobs.Add((key, matcher));
        }

        (string Key, TemplateMatchResult Match, int RefW, int RefH)[] results = await Task.Run(() =>
        {
            var local = new (string Key, TemplateMatchResult Match, int RefW, int RefH)[matchJobs.Count];
            Parallel.For(0, matchJobs.Count, i =>
            {
                (string key, TemplateMatcher matcher) = matchJobs[i];
                TemplateMatchResult match = _screen.MatchPrepared(matcher, sourceGray, logicalWidth, logicalHeight);
                local[i] = (key, match, matcher.ReferenceWidth, matcher.ReferenceHeight);
            });
            return local;
        }, cancellationToken);

        var bestByKey = new Dictionary<string, TemplateMatchResult>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, TemplateMatchResult match, int refW, int refH) in results)
        {
            _log($"宝物 {key} 模板 {refW}×{refH} 分数：{match.Score:F4}");
            if (!bestByKey.TryGetValue(key, out TemplateMatchResult? existing) || match.Score > existing.Score)
                bestByKey[key] = match;
        }

        var scored = bestByKey
            .Select(pair => new TreasureCandidate(pair.Key, pair.Value))
            .ToList();

        TreasureCandidate? winner = PickPriorityTreasure(scored, _config.TreasurePriority, threshold);
        if (winner is null)
            return null;

        TemplateMatchResult bestMatch = winner.Match;
        Point clickPoint = _screen.MatchCenterToScreen(region, bestMatch);
        if (clickOffset is { } offset)
        {
            Point delta = geometry.ScaleDelta(offset);
            clickPoint = new Point(clickPoint.X + delta.X, clickPoint.Y + delta.Y);
        }

        _log($"优先宝物 {winner.Key} 匹配成功（{bestMatch.Score:F4} ≥ {threshold:F2}），点击 screen({clickPoint.X:F0},{clickPoint.Y:F0})" +
             (clickOffset is null ? "" : $"（相对图标中心偏移 1080p {clickOffset.X},{clickOffset.Y}）"));
        await _screen.ClickScreenAsync(window, clickPoint, cancellationToken);
        return winner.Key;
    }

    /// <summary>
    /// 先按位置做非极大值抑制（同位置只留最高分），再在合格候选中按优先级选取。
    /// 避免说明区弱匹配压过图标条上的真实选项。
    /// </summary>
    public static TreasureCandidate? PickPriorityTreasure(
        IReadOnlyList<TreasureCandidate> scored,
        IReadOnlyList<string> priority,
        double threshold,
        double nmsDistanceLogical = 80)
    {
        var above = scored.Where(c => c.Match.Score >= threshold)
            .OrderByDescending(c => c.Match.Score)
            .ToList();
        if (above.Count == 0)
            return null;

        var distinct = new List<TreasureCandidate>();
        foreach (TreasureCandidate candidate in above)
        {
            bool overlaps = distinct.Any(existing =>
            {
                double dx = (existing.Match.X + existing.Match.Width / 2.0) -
                            (candidate.Match.X + candidate.Match.Width / 2.0);
                double dy = (existing.Match.Y + existing.Match.Height / 2.0) -
                            (candidate.Match.Y + candidate.Match.Height / 2.0);
                return Math.Sqrt(dx * dx + dy * dy) <= nmsDistanceLogical;
            });
            if (!overlaps)
                distinct.Add(candidate);
        }

        foreach (string key in priority)
        {
            TreasureCandidate? hit = distinct.FirstOrDefault(
                c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return hit;
        }

        return distinct[0];
    }

    public sealed record TreasureCandidate(string Key, TemplateMatchResult Match);

    private async Task<bool> MatchAndClickTemplateAsync(
        GameWindow window,
        TemplateMatcher matcher,
        ConfigPoint topLeft,
        ConfigSize size,
        ConfigPoint clickPoint,
        string stepName,
        string diagnosticName,
        CancellationToken cancellationToken,
        int? timeoutMs = null,
        bool quietFailure = false,
        bool saveDiagnostics = true,
        double? matchThreshold = null)
    {
        double threshold = matchThreshold ?? _config.MatchThreshold;
        (bool matched, TemplateMatchResult match, BitmapSource image) = await _screen.MatchRegionAsync(
            window, matcher, topLeft, size, cancellationToken, timeoutMs, threshold);

        if (_config.SaveDiagnostics && saveDiagnostics)
            SaveDiagnostic(image, diagnosticName, stepName);
        if (matched || !quietFailure)
            _log($"{stepName}检测，模板分数：{match.Score:F4}，偏移：({match.X},{match.Y})");
        if (!matched)
        {
            if (!quietFailure)
                _log($"{stepName}未达到阈值 {threshold:F2}，跳过点击。");
            return false;
        }

        _log($"{stepName}识别成功，点击写死点。");
        await _screen.ClickAsync(window, clickPoint, stepName, cancellationToken);
        return true;
    }

    private void SaveDiagnostic(BitmapSource image, string name, string taskName)
    {
        string directory = Path.IsPathRooted(_config.DiagnosticDirectory)
            ? _config.DiagnosticDirectory
            : Path.Combine(AppContext.BaseDirectory, _config.DiagnosticDirectory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{name}-roi-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        _log($"{taskName}已保存识别区域：{path}");
    }
}
