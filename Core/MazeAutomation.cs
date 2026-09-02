using System.Windows.Media.Imaging;
using System.Windows;
using System.Diagnostics;
using BetterMuv.Services;

namespace BetterMuv.Core;

public sealed class MazeAutomation
{
    private const string FirstTaskName = "识别主页";
    private const string SecondTaskName = "识别任务界面";
    private const string ThirdTaskName = "识别迷宫开始界面";
    private const string FourthTaskName = "识别探索准备界面";
    private const string FifthTaskName = "识别下一步";
    private const string PartnerSelectionTaskName = "识别选择伙伴/商店界面";
    private const string BattleSkipTaskName = "识别战斗界面";
    private const string EventChoiceTaskName = "识别事件选择界面";
    private const string SettlementTaskName = "识别迷宫结算界面";
    private const string TreasureTaskName = "识别宝物选择状态";
    private const string RouteSelectionTaskName = "识别路线选择界面";

    private readonly AutomationConfig _config;
    private readonly WindowCaptureService _capture;
    private readonly MouseInputService _mouse;
    private readonly TemplateMatcher _firstMatcher;
    private readonly TemplateMatcher _secondMatcher;
    private readonly TemplateMatcher _thirdMatcher;
    private readonly TemplateMatcher _fourthMatcher;
    private readonly TemplateMatcher _fifthMatcher;
    private readonly TemplateMatcher _partnerSelectionMatcher;
    private readonly TemplateMatcher _battleSkipMatcher;
    private readonly TemplateMatcher _eventChoiceMatcher;
    private readonly SettlementShopRunner _settlementShop;
    private readonly TemplateMatcher _treasureStateMatcher;
    private readonly TemplateMatcher _routeSelectionMatcher;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TemplateMatcher>> _treasureMatchers;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TemplateMatcher>> _routeTreasureMatchers;
    private readonly Action<string> _log;

    public MazeAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _capture = new WindowCaptureService();
        _mouse = new MouseInputService();
        string templateDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates");
        _firstMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "quest.png"));
        _secondMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "maze-search.png"));
        _thirdMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "exploration-ready.png"));
        _fourthMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "exploration-action.png"));
        _fifthMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "maze-next.png"));
        _partnerSelectionMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "partner-leave.png"));
        _battleSkipMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "battle-skip.png"));
        _eventChoiceMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "event-choice.png"));
        _settlementShop = new SettlementShopRunner(
            config, templateDirectory, log, ProbeTemplateAsync, ClickReferenceAsync);
        _treasureStateMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "treasure-state.png"));
        _routeSelectionMatcher = new TemplateMatcher(Path.Combine(templateDirectory, "route-selection.png"));
        // 宝物选择用大图标；路线预览图标更小，用 route-* / treasure-sword（小剑）。
        _treasureMatchers = new Dictionary<string, IReadOnlyList<TemplateMatcher>>(StringComparer.OrdinalIgnoreCase)
        {
            ["diamond"] = [new TemplateMatcher(Path.Combine(templateDirectory, "treasure-diamond.png"))],
            ["sparkle"] = [new TemplateMatcher(Path.Combine(templateDirectory, "treasure-sparkle.png"))],
            ["shield"] = [new TemplateMatcher(Path.Combine(templateDirectory, "treasure-shield.png"))],
            ["sword"] = [new TemplateMatcher(Path.Combine(templateDirectory, "treasure-greatsword.png"))],
            ["heart"] =
            [
                new TemplateMatcher(Path.Combine(templateDirectory, "treasure-heart.png")),
                new TemplateMatcher(Path.Combine(templateDirectory, "treasure-heart-half.png"))
            ]
        };
        _routeTreasureMatchers = new Dictionary<string, IReadOnlyList<TemplateMatcher>>(StringComparer.OrdinalIgnoreCase)
        {
            ["diamond"] = [new TemplateMatcher(Path.Combine(templateDirectory, "route-diamond.png"))],
            ["sparkle"] = [new TemplateMatcher(Path.Combine(templateDirectory, "route-sparkle.png"))],
            ["shield"] = [new TemplateMatcher(Path.Combine(templateDirectory, "route-shield.png"))],
            ["sword"] = [new TemplateMatcher(Path.Combine(templateDirectory, "treasure-sword.png"))],
            ["heart"] = [new TemplateMatcher(Path.Combine(templateDirectory, "route-heart.png"))]
        };
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameWindow window = _capture.FindWindow(_config.WindowTitleKeyword);
        _log($"已找到窗口：{window.Title}");

        var geometry = new CaptureGeometry(window.DisplayRect);
        EnsureDisplayAspectRatio(geometry);
        _log($"客户区：{window.ClientRect.Width}×{window.ClientRect.Height}");
        _log($"基准显示器：{window.DisplayRect.Width}×{window.DisplayRect.Height}，16:9 校验通过");

        int completedRuns = 0;
        string limitText = _config.MazeRunLimit == 0 ? "无限" : _config.MazeRunLimit.ToString();
        _log($"迷宫轮次配置：{limitText}。");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _log(completedRuns == 0
                ? "开始识别当前界面并进入本轮迷宫。"
                : $"开始第 {completedRuns + 1} 轮：识别迷宫开始界面并继续。");

            await IdentifyAndEnterMazeAsync(window, cancellationToken);
            completedRuns++;
            _log($"第 {completedRuns} 轮迷宫已结算完成。");

            if (_config.MazeRunLimit > 0 && completedRuns >= _config.MazeRunLimit)
            {
                _log($"已达到配置次数 {_config.MazeRunLimit}，停止。");
                return;
            }

            _log(_config.MazeRunLimit == 0
                ? "配置为无限轮次，等待回到迷宫开始界面后继续。"
                : $"未达上限（{completedRuns}/{_config.MazeRunLimit}），等待回到迷宫开始界面后继续。");
        }
    }

    private async Task IdentifyAndEnterMazeAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("开始循环识别当前任务；确认状态前不会执行任何动作。");
        int identificationRounds = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            identificationRounds++;

            TemplateProbeResult treasureProbe = await ProbeTemplateAsync(
                window, _treasureStateMatcher, _config.TreasureStateTopLeft,
                _config.TreasureStatePadding, cancellationToken);
            if (treasureProbe.IsMatch)
            {
                _log($"{TreasureTaskName}识别成功，进入迷宫循环。");
                await RunMazeLoopAsync(window, 0, cancellationToken);
                return;
            }
            TemplateProbeResult routeProbe = await ProbeTemplateAsync(
                window, _routeSelectionMatcher, _config.RouteSelectionTopLeft,
                _config.RouteSelectionPadding, cancellationToken);
            if (routeProbe.IsMatch)
            {
                _log($"{RouteSelectionTaskName}识别成功，进入迷宫循环。");
                await RunMazeLoopAsync(window, 0, cancellationToken);
                return;
            }
            if (await _settlementShop.IsSettlementVisibleAsync(window, cancellationToken))
            {
                _log($"{SettlementTaskName}识别成功，进入迷宫循环。");
                await RunMazeLoopAsync(window, 0, cancellationToken);
                return;
            }
            if (await MatchAndClickTemplateAsync(
                    window, _partnerSelectionMatcher, _config.PartnerSelectionTopLeft,
                    _config.PartnerSelectionPadding, PartnerSelectionTaskName, "partner-leave",
                    cancellationToken, timeoutMs: 0, quietFailure: true, saveDiagnostics: false))
            {
                await RunMazeLoopAsync(window, 0, cancellationToken);
                return;
            }
            if (await MatchAndClickTemplateAsync(
                    window, _battleSkipMatcher, _config.BattleSkipTopLeft,
                    _config.BattleSkipPadding, BattleSkipTaskName, "battle-skip",
                    cancellationToken, timeoutMs: 0, quietFailure: true, saveDiagnostics: false))
            {
                await RunMazeLoopAsync(window, 0, cancellationToken);
                return;
            }
            if (await MatchAndClickTemplateAsync(
                    window, _fifthMatcher, _config.FifthSearchTopLeft, _config.FifthSearchPadding,
                    FifthTaskName, "maze-next", cancellationToken,
                    timeoutMs: 0, quietFailure: true, saveDiagnostics: false))
            {
                await RunMazeLoopAsync(window, 1, cancellationToken);
                return;
            }
            if (await MatchAndClickTemplateAsync(
                    window, _fourthMatcher, _config.FourthSearchTopLeft, _config.FourthSearchPadding,
                    FourthTaskName, "exploration-action", cancellationToken,
                    timeoutMs: 0, quietFailure: true, saveDiagnostics: false))
            {
                await RunMazeLoopAsync(window, 0, cancellationToken);
                return;
            }
            if (await MatchAndClickTemplateAsync(
                    window, _thirdMatcher, _config.ThirdSearchTopLeft, _config.ThirdSearchPadding,
                    ThirdTaskName, "exploration-ready", cancellationToken,
                    timeoutMs: 0, quietFailure: true, saveDiagnostics: false))
            {
                await RunFromFourthTaskAsync(window, cancellationToken);
                return;
            }
            if (await MatchAndClickTemplateAsync(
                    window, _secondMatcher, _config.SecondSearchTopLeft, _config.SecondSearchPadding,
                    SecondTaskName, "maze-search", cancellationToken,
                    timeoutMs: 0, quietFailure: true, saveDiagnostics: false))
            {
                await RunFromThirdTaskAsync(window, cancellationToken);
                return;
            }
            if (await MatchAndClickFirstTaskOnceAsync(window, cancellationToken))
            {
                await RunFromSecondTaskAsync(window, cancellationToken);
                return;
            }

            if (identificationRounds % 20 == 0)
                _log("尚未识别到已知界面，继续扫描。");
            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
    }

    private async Task<bool> MatchAndClickFirstTaskOnceAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        window = _capture.Refresh(window);
        var geometry = new CaptureGeometry(window.DisplayRect);
        EnsureDisplayAspectRatio(geometry);
        int padding = _config.FirstSearchPadding;
        var topLeft = new ConfigPoint(
            _config.SearchTopLeft.X - padding,
            _config.SearchTopLeft.Y - padding);
        var size = new ConfigSize(
            _firstMatcher.ReferenceWidth + padding * 2,
            _firstMatcher.ReferenceHeight + padding * 2);
        ScreenRect rect = geometry.ReferenceRegionFromTopLeftToScreen(
            topLeft, size, _config.ReferenceWidth, _config.ReferenceHeight);
        BitmapSource image = _capture.Capture(rect);
        int logicalWidth = Math.Max(1,
            (int)Math.Round(size.Width * CaptureGeometry.LogicalWidth / (double)_config.ReferenceWidth));
        int logicalHeight = Math.Max(1,
            (int)Math.Round(size.Height * CaptureGeometry.LogicalHeight / (double)_config.ReferenceHeight));
        TemplateMatchResult match = await Task.Run(
            () => _firstMatcher.Match(image, logicalWidth, logicalHeight), cancellationToken);
        if (match.Score < _config.MatchThreshold)
            return false;

        _log($"{FirstTaskName}识别成功，模板分数：{match.Score:F4}。");
        window = await ClickReferenceAsync(
            window, _config.FirstClick, $"{FirstTaskName}点击 1/2", cancellationToken);
        await Task.Delay(_config.DoubleClickIntervalMs, cancellationToken);
        await ClickReferenceAsync(
            window, _config.FirstClick, $"{FirstTaskName}点击 2/2", cancellationToken);
        return true;
    }

    private async Task RunFromSecondTaskAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log($"开始任务：{SecondTaskName}。");
        bool secondMatched = await MatchAndClickTemplateAsync(
            window, _secondMatcher, _config.SecondSearchTopLeft, _config.SecondSearchPadding,
            SecondTaskName, "maze-search", cancellationToken);
        if (!secondMatched)
            _log($"{SecondTaskName}识别失败，继续后续任务。");
        await RunFromThirdTaskAsync(window, cancellationToken);
    }

    private async Task RunFromThirdTaskAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log($"开始任务：{ThirdTaskName}。");
        bool thirdMatched = await MatchAndClickTemplateAsync(
            window, _thirdMatcher, _config.ThirdSearchTopLeft, _config.ThirdSearchPadding,
            ThirdTaskName, "exploration-ready", cancellationToken);
        if (!thirdMatched)
            _log($"{ThirdTaskName}识别失败，等待后继续后续任务。");
        await RunFromFourthTaskAsync(window, cancellationToken);
    }

    private async Task RunFromFourthTaskAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log($"开始任务：{FourthTaskName}。");
        if (!await MatchAndClickTemplateAsync(
                window, _fourthMatcher, _config.FourthSearchTopLeft, _config.FourthSearchPadding,
                FourthTaskName, "exploration-action", cancellationToken))
            _log($"{FourthTaskName}识别失败，继续检测迷宫探索流程。");
        await RunMazeLoopAsync(window, 0, cancellationToken);
    }

    private async Task RunMazeLoopAsync(
        GameWindow window, int mazeLoopCount, CancellationToken cancellationToken)
    {
        _log("进入迷宫循环：随时检测战斗SKIP/立ち去る/结算/下一步，以及宝物与路线选择。");
        bool treasureHandled = false;
        bool routeHandled = false;
        bool nextHandled = false;
        bool battleSkipHandled = false;
        bool eventChoiceHandled = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TemplateProbeResult battleSkipProbe = await ProbeTemplateAsync(
                window, _battleSkipMatcher, _config.BattleSkipTopLeft,
                _config.BattleSkipPadding, cancellationToken);
            if (battleSkipProbe.IsMatch)
            {
                if (!battleSkipHandled)
                {
                    _log($"{BattleSkipTaskName}识别成功，点击 SKIP。");
                    await _mouse.ClickAsync(window.Handle, battleSkipProbe.Center, cancellationToken);
                    battleSkipHandled = true;
                    await WaitBattleSkipDismissedAsync(window, cancellationToken);
                    battleSkipHandled = false;
                }
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            battleSkipHandled = false;
            TemplateProbeResult eventChoiceProbe = await ProbeTemplateAsync(
                window, _eventChoiceMatcher, _config.EventChoiceTopLeft,
                _config.EventChoicePadding, cancellationToken);
            if (eventChoiceProbe.IsMatch)
            {
                if (!eventChoiceHandled)
                {
                    _log($"{EventChoiceTaskName}识别成功，默认选择第二选项。");
                    window = await ClickReferenceAsync(
                        window, _config.EventChoiceSecondOption, "事件第二选项", cancellationToken);
                    eventChoiceHandled = true;
                    await Task.Delay(500, cancellationToken);
                }
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            eventChoiceHandled = false;
            TemplateProbeResult partnerProbe = await ProbeTemplateAsync(
                window, _partnerSelectionMatcher, _config.PartnerSelectionTopLeft,
                _config.PartnerSelectionPadding, cancellationToken);
            if (partnerProbe.IsMatch)
            {
                // 伙伴/商店可能连续出现多次；每次命中都要点，不能只点一次后空转。
                _log($"{PartnerSelectionTaskName}识别成功，点击中心。");
                await _mouse.ClickAsync(window.Handle, partnerProbe.Center, cancellationToken);
                await WaitPartnerOrShopDismissedAsync(window, cancellationToken);
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            if (await _settlementShop.IsSettlementVisibleAsync(window, cancellationToken))
            {
                await _settlementShop.RunAsync(window, cancellationToken);
                _log("本轮结算完成，结束迷宫循环。");
                return;
            }

            TemplateProbeResult treasureProbe = await ProbeTemplateAsync(
                window, _treasureStateMatcher, _config.TreasureStateTopLeft,
                _config.TreasureStatePadding, cancellationToken);
            if (treasureProbe.IsMatch)
            {
                if (!treasureHandled)
                {
                    _log($"{TreasureTaskName}识别成功，开始按优先级选择。");
                    await SelectTreasureAsync(window, cancellationToken);
                    treasureHandled = true;
                    _log("宝物选择完成，继续检测后续界面。");
                }
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            treasureHandled = false;
            TemplateProbeResult routeProbe = await ProbeTemplateAsync(
                window, _routeSelectionMatcher, _config.RouteSelectionTopLeft,
                _config.RouteSelectionPadding, cancellationToken);
            if (routeProbe.IsMatch)
            {
                if (!routeHandled)
                {
                    _log($"{RouteSelectionTaskName}识别成功，开始按优先级选择路线宝物。");
                    string? selected = await SelectPriorityTreasureAsync(
                        window, _config.RouteTreasureOptionsTopLeft, _config.RouteTreasureOptionsSize,
                        _routeTreasureMatchers, cancellationToken,
                        diagnosticName: "route-treasure-options", taskName: RouteSelectionTaskName,
                        matchThreshold: _config.TreasureMatchThreshold);
                    if (selected is null)
                        _log("路线区域未找到已提供的宝物模板，直接选择路线。");
                    else
                        _log($"已选中路线宝物：{selected}，随后选择路线。");
                    await _mouse.ClickAsync(window.Handle, routeProbe.Center, cancellationToken);
                    routeHandled = true;
                    _log("路线选择完成，继续检测后续界面。");
                }
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            routeHandled = false;
            TemplateProbeResult nextProbe = await ProbeTemplateAsync(
                window, _fifthMatcher, _config.FifthSearchTopLeft,
                _config.FifthSearchPadding, cancellationToken);
            if (nextProbe.IsMatch)
            {
                if (!nextHandled)
                {
                    _log($"{FifthTaskName}识别成功，点击模板中心。");
                    await _mouse.ClickAsync(window.Handle, nextProbe.Center, cancellationToken);
                    mazeLoopCount++;
                    _log($"{FifthTaskName}已执行 {mazeLoopCount} 次。");
                    nextHandled = true;
                    await WaitNextDismissedAsync(window, cancellationToken);
                    nextHandled = false;
                    _log("下一步完成，继续检测后续界面。");
                }
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
                continue;
            }

            nextHandled = false;
            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
    }

    private async Task WaitNextDismissedAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < _config.DetectionTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateProbeResult battleSkipProbe = await ProbeTemplateAsync(
                window, _battleSkipMatcher, _config.BattleSkipTopLeft,
                _config.BattleSkipPadding, cancellationToken);
            if (battleSkipProbe.IsMatch)
                return;

            TemplateProbeResult eventChoiceProbe = await ProbeTemplateAsync(
                window, _eventChoiceMatcher, _config.EventChoiceTopLeft,
                _config.EventChoicePadding, cancellationToken);
            if (eventChoiceProbe.IsMatch)
                return;

            if (await _settlementShop.IsSettlementVisibleAsync(window, cancellationToken))
                return;

            TemplateProbeResult partnerProbe = await ProbeTemplateAsync(
                window, _partnerSelectionMatcher, _config.PartnerSelectionTopLeft,
                _config.PartnerSelectionPadding, cancellationToken);
            if (partnerProbe.IsMatch)
                return;

            TemplateProbeResult treasureProbe = await ProbeTemplateAsync(
                window, _treasureStateMatcher, _config.TreasureStateTopLeft,
                _config.TreasureStatePadding, cancellationToken);
            if (treasureProbe.IsMatch)
                return;

            TemplateProbeResult routeProbe = await ProbeTemplateAsync(
                window, _routeSelectionMatcher, _config.RouteSelectionTopLeft,
                _config.RouteSelectionPadding, cancellationToken);
            if (routeProbe.IsMatch)
                return;

            TemplateProbeResult nextProbe = await ProbeTemplateAsync(
                window, _fifthMatcher, _config.FifthSearchTopLeft,
                _config.FifthSearchPadding, cancellationToken);
            if (!nextProbe.IsMatch)
                return;

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
    }

    private async Task WaitBattleSkipDismissedAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < _config.DetectionTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateProbeResult battleSkipProbe = await ProbeTemplateAsync(
                window, _battleSkipMatcher, _config.BattleSkipTopLeft,
                _config.BattleSkipPadding, cancellationToken);
            if (!battleSkipProbe.IsMatch)
                return;

            await _mouse.ClickAsync(window.Handle, battleSkipProbe.Center, cancellationToken);
            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }
    }

    private async Task WaitPartnerOrShopDismissedAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        // 离开后界面常短暂残留；勿空等 DetectionTimeoutMs（默认 10s）。
        // 超时后由迷宫循环再次识别并点击，以处理连续出现的第二个离开界面。
        const int dismissTimeoutMs = 1500;
        var timer = Stopwatch.StartNew();
        bool reclicked = false;
        while (timer.ElapsedMilliseconds < dismissTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateProbeResult partnerProbe = await ProbeTemplateAsync(
                window, _partnerSelectionMatcher, _config.PartnerSelectionTopLeft,
                _config.PartnerSelectionPadding, cancellationToken);
            if (!partnerProbe.IsMatch)
                return;

            TemplateProbeResult battleSkipProbe = await ProbeTemplateAsync(
                window, _battleSkipMatcher, _config.BattleSkipTopLeft,
                _config.BattleSkipPadding, cancellationToken);
            if (battleSkipProbe.IsMatch)
                return;

            TemplateProbeResult eventChoiceProbe = await ProbeTemplateAsync(
                window, _eventChoiceMatcher, _config.EventChoiceTopLeft,
                _config.EventChoicePadding, cancellationToken);
            if (eventChoiceProbe.IsMatch)
                return;

            if (await _settlementShop.IsSettlementVisibleAsync(window, cancellationToken))
                return;

            TemplateProbeResult treasureProbe = await ProbeTemplateAsync(
                window, _treasureStateMatcher, _config.TreasureStateTopLeft,
                _config.TreasureStatePadding, cancellationToken);
            if (treasureProbe.IsMatch)
                return;

            TemplateProbeResult routeProbe = await ProbeTemplateAsync(
                window, _routeSelectionMatcher, _config.RouteSelectionTopLeft,
                _config.RouteSelectionPadding, cancellationToken);
            if (routeProbe.IsMatch)
                return;

            TemplateProbeResult nextProbe = await ProbeTemplateAsync(
                window, _fifthMatcher, _config.FifthSearchTopLeft,
                _config.FifthSearchPadding, cancellationToken);
            if (nextProbe.IsMatch)
                return;

            if (!reclicked && timer.ElapsedMilliseconds >= 400)
            {
                _log("立ち去る仍在，补点一次。");
                await _mouse.ClickAsync(window.Handle, partnerProbe.Center, cancellationToken);
                reclicked = true;
            }

            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }

        _log("立ち去る等待超时，返回循环以便处理后续同类界面。");
    }

    private async Task SelectTreasureAsync(GameWindow window, CancellationToken cancellationToken)
    {
        // 状态条先亮起时选项图标可能仍在过渡动画中。
        await Task.Delay(400, cancellationToken);
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
            {
                _log($"宝物未匹配（{attempt}/{_config.TreasureMatchRetryCount}），稍后重试。");
                await Task.Delay(_config.TreasureMatchRetryDelayMs, cancellationToken);
            }
        }

        _log("当前已提供的普通宝物模板均未匹配。");
    }

    private async Task<string?> SelectPriorityTreasureAsync(
        GameWindow window,
        ConfigPoint optionsTopLeft,
        ConfigSize optionsSize,
        IReadOnlyDictionary<string, IReadOnlyList<TemplateMatcher>> matchers,
        CancellationToken cancellationToken,
        ConfigPoint? clickOffset4K = null,
        string? diagnosticName = null,
        string? taskName = null,
        double? matchThreshold = null)
    {
        double threshold = matchThreshold ?? _config.MatchThreshold;
        window = _capture.Refresh(window);
        var geometry = new CaptureGeometry(window.DisplayRect);
        ScreenRect rect = geometry.ReferenceRegionFromTopLeftToScreen(
            optionsTopLeft, optionsSize,
            _config.ReferenceWidth, _config.ReferenceHeight);
        BitmapSource image = _capture.Capture(rect);
        _log($"{taskName ?? "宝物匹配"}搜索区域：4K {optionsSize.Width}×{optionsSize.Height}，" +
             $"screen({rect.Left},{rect.Top}) {rect.Width}×{rect.Height}，阈值 {threshold:F2}");
        if (_config.SaveDiagnostics && !string.IsNullOrWhiteSpace(diagnosticName))
            SaveDiagnostic(image, diagnosticName, taskName ?? "宝物匹配");

        int logicalWidth = Math.Max(1,
            (int)Math.Round(optionsSize.Width *
                            CaptureGeometry.LogicalWidth / (double)_config.ReferenceWidth));
        int logicalHeight = Math.Max(1,
            (int)Math.Round(optionsSize.Height *
                            CaptureGeometry.LogicalHeight / (double)_config.ReferenceHeight));

        var scored = new List<TreasureCandidate>();
        foreach (string key in _config.TreasurePriority)
        {
            if (!matchers.TryGetValue(key, out IReadOnlyList<TemplateMatcher>? keyMatchers))
                continue;

            TemplateMatchResult? best = null;
            foreach (TemplateMatcher matcher in keyMatchers)
            {
                TemplateMatchResult match = await Task.Run(
                    () => matcher.Match(image, logicalWidth, logicalHeight), cancellationToken);
                _log($"宝物 {key} 模板 {matcher.ReferenceWidth}×{matcher.ReferenceHeight} 分数：{match.Score:F4}");
                if (best is null || match.Score > best.Score)
                    best = match;
            }

            if (best is not null)
                scored.Add(new TreasureCandidate(key, best));
        }

        TreasureCandidate? winner = PickPriorityTreasure(scored, _config.TreasurePriority, threshold);
        if (winner is null)
            return null;

        TemplateMatchResult bestMatch = winner.Match;
        double centerX = rect.Left + (bestMatch.X + bestMatch.Width / 2.0) * rect.Width / logicalWidth;
        double centerY = rect.Top + (bestMatch.Y + bestMatch.Height / 2.0) * rect.Height / logicalHeight;
        if (clickOffset4K is { } offset)
        {
            centerX += offset.X * geometry.ClientRect.Width / (double)_config.ReferenceWidth;
            centerY += offset.Y * geometry.ClientRect.Height / (double)_config.ReferenceHeight;
        }

        var clickPoint = new Point(centerX, centerY);
        _log($"优先宝物 {winner.Key} 匹配成功（{bestMatch.Score:F4} ≥ {threshold:F2}），点击 screen({clickPoint.X:F0},{clickPoint.Y:F0})" +
             (clickOffset4K is null ? "" : $"（相对图标中心偏移 4K {clickOffset4K.X},{clickOffset4K.Y}）"));
        await _mouse.ClickAsync(window.Handle, clickPoint, cancellationToken);
        return winner.Key;
    }

    /// <summary>
    /// 先按位置做非极大值抑制（同位置只留最高分），再在合格候选中按优先级选取。
    /// 避免说明区弱钻石假阳性压过图标条上的真盾/剑。
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

    internal sealed record TreasureCandidate(string Key, TemplateMatchResult Match);

    private async Task<TemplateProbeResult> ProbeTemplateAsync(
        GameWindow window,
        TemplateMatcher matcher,
        ConfigPoint expectedTopLeft,
        int padding,
        CancellationToken cancellationToken)
    {
        window = _capture.Refresh(window);
        var geometry = new CaptureGeometry(window.DisplayRect);
        var topLeft = new ConfigPoint(expectedTopLeft.X - padding, expectedTopLeft.Y - padding);
        var size = new ConfigSize(
            matcher.ReferenceWidth + padding * 2,
            matcher.ReferenceHeight + padding * 2);
        ScreenRect rect = geometry.ReferenceRegionFromTopLeftToScreen(
            topLeft, size, _config.ReferenceWidth, _config.ReferenceHeight);
        BitmapSource image = _capture.Capture(rect);
        int logicalWidth = Math.Max(1,
            (int)Math.Round(size.Width * CaptureGeometry.LogicalWidth / (double)_config.ReferenceWidth));
        int logicalHeight = Math.Max(1,
            (int)Math.Round(size.Height * CaptureGeometry.LogicalHeight / (double)_config.ReferenceHeight));
        TemplateMatchResult match = await Task.Run(
            () => matcher.Match(image, logicalWidth, logicalHeight), cancellationToken);
        var center = new Point(
            rect.Left + (match.X + match.Width / 2.0) * rect.Width / logicalWidth,
            rect.Top + (match.Y + match.Height / 2.0) * rect.Height / logicalHeight);
        return new TemplateProbeResult(match.Score >= _config.MatchThreshold, match.Score, center);
    }

    private async Task<GameWindow> ClickReferenceAsync(
        GameWindow window, ConfigPoint referencePoint, string reason, CancellationToken cancellationToken)
    {
        window = _capture.Refresh(window);
        var geometry = new CaptureGeometry(window.DisplayRect);
        EnsureDisplayAspectRatio(geometry);
        var point = geometry.ReferenceToScreen(referencePoint, _config.ReferenceWidth, _config.ReferenceHeight);
        _log($"{reason}：reference({referencePoint.X},{referencePoint.Y}) → screen({point.X:F0},{point.Y:F0})");
        await _mouse.ClickAsync(window.Handle, point, cancellationToken);
        return window;
    }

    private async Task<bool> MatchAndClickTemplateAsync(
        GameWindow window,
        TemplateMatcher matcher,
        ConfigPoint expectedTopLeft,
        int padding,
        string stepName,
        string diagnosticName,
        CancellationToken cancellationToken,
        int? timeoutMs = null,
        bool quietFailure = false,
        bool saveDiagnostics = true)
    {
        window = _capture.Refresh(window);
        var geometry = new CaptureGeometry(window.DisplayRect);
        EnsureDisplayAspectRatio(geometry);
        var captureTopLeft = new ConfigPoint(
            expectedTopLeft.X - padding,
            expectedTopLeft.Y - padding);
        var size = new ConfigSize(
            matcher.ReferenceWidth + padding * 2,
            matcher.ReferenceHeight + padding * 2);
        ScreenRect rect = geometry.ReferenceRegionFromTopLeftToScreen(
            captureTopLeft, size, _config.ReferenceWidth, _config.ReferenceHeight);

        int logicalSearchWidth = Math.Max(1,
            (int)Math.Round(size.Width * CaptureGeometry.LogicalWidth / (double)_config.ReferenceWidth));
        int logicalSearchHeight = Math.Max(1,
            (int)Math.Round(size.Height * CaptureGeometry.LogicalHeight / (double)_config.ReferenceHeight));
        BitmapSource image = null!;
        TemplateMatchResult match = null!;
        int attempts = 0;
        var timer = Stopwatch.StartNew();
        int effectiveTimeoutMs = timeoutMs ?? _config.DetectionTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image = _capture.Capture(rect);
            attempts++;
            match = await Task.Run(
                () => matcher.Match(image, logicalSearchWidth, logicalSearchHeight),
                cancellationToken);
            if (match.Score >= _config.MatchThreshold ||
                timer.ElapsedMilliseconds >= effectiveTimeoutMs)
                break;
            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
        }

        if (_config.SaveDiagnostics && saveDiagnostics)
            SaveDiagnostic(image, diagnosticName, stepName);
        if (match.Score >= _config.MatchThreshold || !quietFailure)
            _log($"{stepName}检测 {attempts} 次，模板分数：{match.Score:F4}，" +
                 $"偏移：({match.X - padding / 2},{match.Y - padding / 2})");
        if (match.Score < _config.MatchThreshold)
        {
            if (!quietFailure)
                _log($"{stepName}未达到阈值 {_config.MatchThreshold:F2}，跳过点击。");
            return false;
        }

        var center = new Point(
            rect.Left + (match.X + match.Width / 2.0) * rect.Width / logicalSearchWidth,
            rect.Top + (match.Y + match.Height / 2.0) * rect.Height / logicalSearchHeight);
        _log($"{stepName}识别成功，点击模板中心 screen({center.X:F0},{center.Y:F0})");
        await _mouse.ClickAsync(window.Handle, center, cancellationToken);
        return true;
    }

    private static void EnsureDisplayAspectRatio(CaptureGeometry geometry)
    {
        if (!geometry.IsSixteenByNine)
            throw new InvalidOperationException(
                $"游戏所在显示器必须为 16:9，当前为 {geometry.ClientRect.Width}×{geometry.ClientRect.Height}。");
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
