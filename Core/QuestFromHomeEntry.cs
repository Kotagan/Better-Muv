using System.Diagnostics;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 有主页钮则回主页，再进入任务页；可等待目标模板出现后再点击。
/// </summary>
public sealed class QuestFromHomeEntry
{
    /// <summary>任务页切换较慢，点完任务后多等一会再点目标。</summary>
    private const int AfterQuestDelayMs = 2200;
    private const int AfterTargetDelayMs = 800;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _questMatcher;

    public QuestFromHomeEntry(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _questMatcher = TemplateAssets.Load("quest.png");
    }

    public static ConfigPoint MainQuestBannerClick(AutomationConfig config) => new(
        config.MainQuestBannerTopLeft.X + config.MainQuestBannerSize.Width / 2,
        config.MainQuestBannerTopLeft.Y + config.MainQuestBannerSize.Height / 2);

    /// <summary>任务页「バトルシミュレート」点击中心（后续流程备用）。</summary>
    public static ConfigPoint BattleSimulateClick(AutomationConfig config) => new(
        config.QuestBattleSimulateTopLeft.X + config.QuestBattleSimulateSize.Width / 2,
        config.QuestBattleSimulateTopLeft.Y + config.QuestBattleSimulateSize.Height / 2);

    /// <summary>任务页「戦術演習」点击中心（后续流程备用）。</summary>
    public static ConfigPoint ExercisesClick(AutomationConfig config) => new(
        config.QuestExercisesTopLeft.X + config.QuestExercisesSize.Width / 2,
        config.QuestExercisesTopLeft.Y + config.QuestExercisesSize.Height / 2);

    /// <summary>任务页「課外活動」点击中心（后续流程备用）。</summary>
    public static ConfigPoint ActivityClick(AutomationConfig config) => new(
        config.QuestActivityTopLeft.X + config.QuestActivitySize.Width / 2,
        config.QuestActivityTopLeft.Y + config.QuestActivitySize.Height / 2);

    /// <summary>左上角返回箭头点击中心（后续流程备用）。</summary>
    public static ConfigPoint NavBackClick(AutomationConfig config) => new(
        config.NavBackTopLeft.X + config.NavBackSize.Width / 2,
        config.NavBackTopLeft.Y + config.NavBackSize.Height / 2);

    public async Task<GameWindow> RunAsync(
        GameWindow window,
        ConfigPoint targetClick,
        string targetName,
        CancellationToken cancellationToken,
        TemplateMatcher? targetReadyMatcher = null)
    {
        // 有主页钮则点一次并已在内部等 500ms。
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);

        // BitBlt 截的是屏幕上客户区像素：必须把游戏拉回前台，否则会截到桌面/本工具窗口。
        window = _screen.Refresh(window);
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
            _log("未能将游戏置于前台，クエスト识别可能失败。");
        await Task.Delay(250, cancellationToken);

        _log("等待主界面的“クエスト”按钮出现，最多 10 秒。");
        TemplateProbeResult quest = await WaitForQuestAsync(window, cancellationToken);
        if (!quest.IsMatch)
        {
            // 再聚焦一次并放宽底栏 ROI 重试。
            window = _screen.Refresh(window);
            await _screen.FocusAsync(window.Handle, cancellationToken);
            await Task.Delay(300, cancellationToken);
            quest = await WaitForQuestAsync(window, cancellationToken, widen: true, timeoutMs: 5000);
        }

        if (!quest.IsMatch)
            throw new InvalidOperationException(
                $"主界面加载超时：未识别到“クエスト”（最高 {quest.Score:F4}）。" +
                "请确认游戏全屏在最前、Better-Muv 已最小化。任务已停止。");

        _log($"已识别“クエスト”（{quest.Score:F4}），点击模板中心进入任务页。");
        window = await DoubleClickScreenAsync(window, quest.Center, "任务", cancellationToken);

        if (targetReadyMatcher is null)
        {
            await Task.Delay(AfterQuestDelayMs, cancellationToken);
        }
        else
        {
            _log($"等待任务选择页的“{targetName}”文字出现，最多 10 秒。");
            TemplateProbeResult ready = await _screen.WaitForProbeAsync(
                window, targetReadyMatcher,
                _config.SecondSearchTopLeft, _config.SecondSearchSize,
                cancellationToken, timeoutMs: 10000);
            if (!ready.IsMatch)
                throw new InvalidOperationException(
                    $"任务选择页加载超时：未识别到“{targetName}”（最高 {ready.Score:F4}）。任务已停止。");
            _log($"已识别“{targetName}”（{ready.Score:F4}），开始点击。");
            try
            {
                RegionCapture readyRoi = _screen.CaptureRegion(
                    window, _config.SecondSearchTopLeft, _config.SecondSearchSize);
                readyRoi.Image.Freeze();
                MazeAssetHarvest.SaveTemplateCandidate("maze-search", readyRoi.Image, _log);
            }
            catch { }
        }

        window = await DoubleClickAsync(window, targetClick, targetName, cancellationToken);
        await Task.Delay(AfterTargetDelayMs, cancellationToken);
        return _screen.Refresh(window);
    }

    private async Task<TemplateProbeResult> WaitForQuestAsync(
        GameWindow window,
        CancellationToken cancellationToken,
        bool widen = false,
        int timeoutMs = 10000)
    {
        ConfigPoint topLeft = widen
            ? new ConfigPoint(
                Math.Min(_config.SearchTopLeft.X, _config.QuestNavTopLeft.X),
                Math.Min(_config.SearchTopLeft.Y, _config.QuestNavTopLeft.Y))
            : _config.SearchTopLeft;
        ConfigSize size = widen
            ? new ConfigSize(
                Math.Max(_config.FirstSearchSize.Width, _config.QuestNavSize.Width),
                Math.Max(_config.FirstSearchSize.Height, _config.QuestNavSize.Height))
            : _config.FirstSearchSize;

        // 模板约 103×72 逻辑像素；ROI 过小会导致永远 miss。
        size = ScreenAutomation.EnsureFitsTemplate(size, _questMatcher);

        var timer = Stopwatch.StartNew();
        TemplateProbeResult best = TemplateProbes.Empty;
        int ticks = 0;
        do
        {
            if (ticks > 0 && ticks % 8 == 0)
            {
                window = _screen.Refresh(window);
                await _screen.FocusAsync(window.Handle, cancellationToken);
            }

            TemplateProbeResult probe = await _screen.ProbeAsync(
                window, _questMatcher, topLeft, size, cancellationToken);
            if (probe.Score > best.Score)
                best = probe;
            if (probe.IsMatch)
                return probe;

            if (timer.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
            ticks++;
        }
        while (timer.ElapsedMilliseconds < timeoutMs);

        return best;
    }

    private async Task<GameWindow> DoubleClickAsync(
        GameWindow window, ConfigPoint point, string reason, CancellationToken cancellationToken)
    {
        int gapMs = Math.Max(_config.DoubleClickIntervalMs, 50);
        window = await _screen.ClickAsync(window, point, $"{reason} 1/2", cancellationToken);
        await Task.Delay(gapMs, cancellationToken);
        return await _screen.ClickAsync(window, point, $"{reason} 2/2", cancellationToken);
    }

    private async Task<GameWindow> DoubleClickScreenAsync(
        GameWindow window, System.Windows.Point point, string reason, CancellationToken cancellationToken)
    {
        int gapMs = Math.Max(_config.DoubleClickIntervalMs, 50);
        _log($"{reason} 1/2：点击识别中心 ({point.X:F0},{point.Y:F0})");
        await _screen.ClickScreenAsync(window, point, cancellationToken);
        await Task.Delay(gapMs, cancellationToken);
        _log($"{reason} 2/2：点击识别中心 ({point.X:F0},{point.Y:F0})");
        await _screen.ClickScreenAsync(window, point, cancellationToken);
        return _screen.Refresh(window);
    }
}
