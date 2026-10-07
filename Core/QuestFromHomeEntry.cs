using System.Diagnostics;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 有主页钮则回主页，再进入任务页；可等待目标模板出现后再点击。
/// </summary>
public sealed class QuestFromHomeEntry
{
    /// <summary>任务页切换较慢，点完任务后多等一会再点目标。</summary>
    private const int AfterQuestDelayMs = 2800;
    private const int AfterTargetDelayMs = 1100;
    private const int QuestEnterAttempts = 3;
    private const int QuestEnterWaitMs = 6000;
    /// <summary>
    /// 窗口已找到时只做短探测（命中即返回）。
    /// 长时间等待留给「尚未找到游戏窗口」的启动流程，不在这里空等。
    /// </summary>
    private const int QuestPresenceTimeoutMs = 2000;
    private const int QuestPresenceRetryMs = 2000;
    /// <summary>紧裁文字模板在 4K/1080p 均应稳定超过 0.80。</summary>
    private const double QuestPresenceThreshold = 0.80;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _questMatcher;
    private readonly PromoPopupDismisser _promo;

    public QuestFromHomeEntry(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _questMatcher = TemplateAssets.Load("quest.png");
        _promo = new PromoPopupDismisser(config, screen, log);
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
        TemplateMatcher? targetReadyMatcher = null,
        ConfigPoint? readyTopLeft = null,
        ConfigSize? readySize = null,
        bool singleClickTarget = false)
    {
        // 有主页钮则点一次并已在内部等 500ms。
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);

        // BitBlt 截的是屏幕上客户区像素：必须把游戏拉回前台，否则会截到桌面/本工具窗口。
        window = _screen.Refresh(window);
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
            _log("未能将游戏置于前台，クエスト识别可能失败。");
        await Task.Delay(250, cancellationToken);

        _log("识别主界面“クエスト”（窗口已就绪，短探测）。");
        TemplateProbeResult quest = await WaitForQuestAsync(
            window, cancellationToken, timeoutMs: QuestPresenceTimeoutMs);
        if (!quest.IsMatch)
        {
            // 再聚焦一次并放宽底栏 ROI 重试。
            window = _screen.Refresh(window);
            await _screen.FocusAsync(window.Handle, cancellationToken);
            await Task.Delay(200, cancellationToken);
            quest = await WaitForQuestAsync(
                window, cancellationToken, widen: true, timeoutMs: QuestPresenceRetryMs);
        }

        if (!quest.IsMatch)
            throw new InvalidOperationException(
                $"主界面加载超时：未识别到“クエスト”（最高 {quest.Score:F4}）。" +
                "请确认游戏全屏在最前、Better-Muv 已最小化。任务已停止。");

        // 默认 SecondSearch=メイズ；主线须显式传入 MainQuestBanner ROI。
        ConfigPoint readyTl = readyTopLeft ?? _config.SecondSearchTopLeft;
        ConfigSize readySz = readySize ?? _config.SecondSearchSize;

        if (targetReadyMatcher is null)
        {
            _log($"已识别“クエスト”（{quest.Score:F4}），点击进入任务页。");
            window = await ClickQuestNavAsync(window, quest, cancellationToken);
            await Task.Delay(AfterQuestDelayMs, cancellationToken);
        }
        else
        {
            TemplateProbeResult ready = TemplateProbes.Empty;
            for (int attempt = 1; attempt <= QuestEnterAttempts; attempt++)
            {
                window = _screen.Refresh(window);
                if (!await _screen.FocusAsync(window.Handle, cancellationToken))
                    _log("未能将游戏置于前台，クエスト点击可能无效。");

                // 每轮重新认一次底栏，避免界面已变仍点旧坐标。
                TemplateProbeResult questNow = await WaitForQuestAsync(
                    window, cancellationToken, widen: attempt > 1, timeoutMs: 4000);
                if (!questNow.IsMatch)
                    questNow = quest;

                _log($"已识别“クエスト”（{questNow.Score:F4}），单击进入任务页（{attempt}/{QuestEnterAttempts}）。");
                window = await ClickQuestNavAsync(window, questNow, cancellationToken);

                _log($"等待任务选择页的“{targetName}”文字出现，最多 {QuestEnterWaitMs / 1000} 秒。");
                ready = await _screen.WaitForProbeAsync(
                    window, targetReadyMatcher, readyTl, readySz,
                    cancellationToken, timeoutMs: QuestEnterWaitMs);
                if (ready.IsMatch)
                    break;

                _log($"任务页未出现“{targetName}”（最高 {ready.Score:F4}），可能点击未生效，重试。");
                quest = questNow;
            }

            if (!ready.IsMatch)
                throw new InvalidOperationException(
                    $"任务选择页加载超时：未识别到“{targetName}”（最高 {ready.Score:F4}）。任务已停止。");
            _log($"已识别“{targetName}”（{ready.Score:F4}），开始点击。");
        }

        if (singleClickTarget)
        {
            window = await _screen.ClickAsync(window, targetClick, targetName, cancellationToken, parkCursor: false);
            await Task.Delay(250, cancellationToken);
            _screen.ParkCursorAway(window);
        }
        else
            window = await DoubleClickAsync(window, targetClick, targetName, cancellationToken);
        await Task.Delay(AfterTargetDelayMs, cancellationToken);
        return _screen.Refresh(window);
    }

    /// <summary>底栏クエスト用单击：双击过快且中途挪开光标时，4K 全屏常点不进。</summary>
    private async Task<GameWindow> ClickQuestNavAsync(
        GameWindow window, TemplateProbeResult quest, CancellationToken cancellationToken)
    {
        _log($"任务：点击识别中心 ({quest.Center.X:F0},{quest.Center.Y:F0})");
        // 进页过程不要立刻把光标挪开，否则部分全屏客户端会吞掉点击。
        await _screen.ClickScreenAsync(window, quest.Center, cancellationToken, parkCursor: false);
        await Task.Delay(700, cancellationToken);
        _screen.ParkCursorAway(window);
        return _screen.Refresh(window);
    }

    private async Task<TemplateProbeResult> WaitForQuestAsync(
        GameWindow window,
        CancellationToken cancellationToken,
        bool widen = false,
        int timeoutMs = QuestPresenceTimeoutMs)
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

        // 模板约 87×29 逻辑像素；ROI 额外留边以容纳分辨率缩放和少量校准偏移。
        size = ScreenAutomation.EnsureFitsTemplate(size, _questMatcher);

        var timer = Stopwatch.StartNew();
        TemplateProbeResult best = TemplateProbes.Empty;
        int ticks = 0;
        do
        {
            if (ticks > 0 && ticks % 4 == 0)
            {
                window = _screen.Refresh(window);
                await _promo.DismissAllAsync(window, cancellationToken);
                window = _screen.Refresh(window);
                await _screen.FocusAsync(window.Handle, cancellationToken);
            }

            TemplateProbeResult probe = await _screen.ProbeAsync(
                window, _questMatcher, topLeft, size, cancellationToken, QuestPresenceThreshold);
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
        int gapMs = Math.Max(_config.DoubleClickIntervalMs, 200);
        window = await _screen.ClickAsync(window, point, $"{reason} 1/2", cancellationToken, parkCursor: false);
        await Task.Delay(gapMs, cancellationToken);
        window = await _screen.ClickAsync(window, point, $"{reason} 2/2", cancellationToken, parkCursor: false);
        await Task.Delay(250, cancellationToken);
        _screen.ParkCursorAway(window);
        return window;
    }
}
