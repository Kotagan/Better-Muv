using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>主页采矿小人 → 採掘弹窗（收菜 / 免费加速共用）。</summary>
internal sealed class MiningPopupAccess
{
    private const int AfterHomeDelayMs = 700;
    private const int AfterOpenDelayMs = 1600;
    private const int RecognizeTimeoutMs = 8000;
    /// <summary>只点识别中心；未命中不点固定坐标，避免误点活动条。</summary>
    private const double EntryThreshold = 0.62;
    private const double TitleThreshold = 0.55;
    private const double ClaimOpenThreshold = 0.62;
    private const double CloseThreshold = 0.72;
    private static readonly ConfigPoint CloseTopLeft = new(700, 880);
    private static readonly ConfigSize CloseSize = new(520, 180);

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly string _taskName;
    private readonly TemplateMatcher _entryMatcher;
    private readonly TemplateMatcher _titleMatcher;
    private readonly TemplateMatcher _claimMatcher;
    private readonly TemplateMatcher _closeMatcher;

    public MiningPopupAccess(AutomationConfig config, ScreenAutomation screen, Action<string> log, string taskName)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _taskName = taskName;
        _entryMatcher = TemplateAssets.Load("mining-home-entry.png");
        _titleMatcher = TemplateAssets.Load("mining-title.png");
        _claimMatcher = TemplateAssets.Load("mining-claim.png");
        _closeMatcher = TemplateAssets.Load("achievement-close.png");
    }

    public async Task<(GameWindow Window, bool Opened)> EnsureHomeAndOpenAsync(CancellationToken cancellationToken)
    {
        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"{_taskName}：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return (window, false);
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);
        _log($"{_taskName}：客户区 {window.ClientRect.Width}×{window.ClientRect.Height}");

        // 启动阶段只做一次快速探测，避免明明在主页却为两个不存在的模板
        // 分别等待完整超时；真正点击入口后仍使用完整等待确认。
        _log($"{_taskName}：快速检查当前是否已在採掘弹窗。");
        if (await IsMiningOpenAsync(window, cancellationToken, titleTimeoutMs: 0, claimTimeoutMs: 0))
        {
            _log($"{_taskName}：当前已在採掘弹窗。");
            return (_screen.Refresh(window), true);
        }

        var home = new HomePresence(_config, _screen, _log);
        (window, bool onHome) = await home.EnsureAsync(window, _taskName, cancellationToken);
        if (!onHome)
        {
            _log($"{_taskName}：未在主页，无法点击采矿入口。");
            return (window, false);
        }

        await Task.Delay(AfterHomeDelayMs, cancellationToken);
        window = _screen.Refresh(window);

        return await OpenAsync(window, cancellationToken);
    }

    public async Task<(GameWindow Window, bool Opened)> OpenAsync(GameWindow window, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            // 扩大入口搜索区，小人位置会漂。
            var entryTopLeft = new ConfigPoint(
                Math.Max(0, _config.MiningEntryTopLeft.X - 80),
                Math.Max(0, _config.MiningEntryTopLeft.Y - 80));
            var entrySize = new ConfigSize(
                Math.Max(_config.MiningEntrySize.Width + 160, 360),
                Math.Max(_config.MiningEntrySize.Height + 160, 360));

            _log($"{_taskName}：识别主页采矿入口（第 {attempt} 次）。");
            TemplateProbeResult entry = await _screen.WaitForProbeAsync(
                window,
                _entryMatcher,
                entryTopLeft,
                entrySize,
                cancellationToken,
                timeoutMs: attempt == 1 ? RecognizeTimeoutMs : 4000,
                matchThreshold: EntryThreshold);
            if (entry.IsMatch)
            {
                _log($"{_taskName}：已识别采矿入口（{entry.Score:F4}），点击识别中心。");
                await _screen.ClickProbeAsync(window, entry, "采矿入口", cancellationToken, settleDelayMs: 200);
            }
            else
            {
                _log($"{_taskName}：未识别入口（最高 {entry.Score:F4}），改点固定坐标 " +
                     $"（{_config.MiningEntryClick.X},{_config.MiningEntryClick.Y}）。");
                await _screen.ClickAsync(
                    window, _config.MiningEntryClick, "采矿入口(固定坐标)", cancellationToken);
            }

            await Task.Delay(AfterOpenDelayMs, cancellationToken);
            window = _screen.Refresh(window);

            if (await IsMiningOpenAsync(window, cancellationToken))
            {
                _log($"{_taskName}：已打开採掘弹窗。");
                return (_screen.Refresh(window), true);
            }

            _log($"{_taskName}：仍未确认採掘弹窗。");
            if (attempt < 3)
            {
                _log($"{_taskName}：回主页后重试打开。");
                // 显式尝试识别并点击右上主页按钮；已在主页时不会命中，也不会点击。
                await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
                window = _screen.Refresh(window);
                var home = new HomePresence(_config, _screen, _log);
                (window, bool onHome) = await home.EnsureAsync(window, _taskName, cancellationToken);
                if (!onHome)
                    break;
                await Task.Delay(AfterHomeDelayMs, cancellationToken);
                window = _screen.Refresh(window);
            }
        }

        return (_screen.Refresh(window), false);
    }

    /// <summary>关闭採掘弹窗后即回到主页；只在识别到「閉じる」时点击。</summary>
    public async Task<bool> CloseAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult close = await _screen.ProbeAsync(
            window,
            _closeMatcher,
            CloseTopLeft,
            CloseSize,
            cancellationToken,
            CloseThreshold);
        if (!close.IsMatch)
        {
            _log($"{_taskName}：未识别採掘「閉じる」（最高 {close.Score:F4}），不点击。");
            return false;
        }

        _log($"{_taskName}：点击採掘「閉じる」（{close.Score:F4}），关闭后即为主页。");
        await _screen.ClickProbeAsync(window, close, "採掘 閉じる", cancellationToken, settleDelayMs: 300);
        return true;
    }

    private async Task<bool> IsMiningOpenAsync(
        GameWindow window,
        CancellationToken cancellationToken,
        int titleTimeoutMs = 2500,
        int claimTimeoutMs = 2000)
    {
        TemplateProbeResult title = await _screen.WaitForProbeAsync(
            window,
            _titleMatcher,
            _config.MiningTitleTopLeft,
            _config.MiningTitleSize,
            cancellationToken,
            timeoutMs: titleTimeoutMs,
            matchThreshold: TitleThreshold);
        if (title.IsMatch)
        {
            _log($"{_taskName}：识别採掘标题（{title.Score:F4}）。");
            return true;
        }

        // 标题阈值卡边时，用「受取」粉钮佐证已打开。
        TemplateProbeResult claim = await _screen.WaitForProbeAsync(
            window,
            _claimMatcher,
            _config.MiningClaimTopLeft,
            _config.MiningClaimSize,
            cancellationToken,
            timeoutMs: claimTimeoutMs,
            matchThreshold: ClaimOpenThreshold);
        if (claim.IsMatch)
        {
            _log($"{_taskName}：标题未稳（最高 {title.Score:F4}），但已识别「受取」（{claim.Score:F4}），视为已打开。");
            return true;
        }

        _log($"{_taskName}：未确认打开（标题 {title.Score:F4}，受取 {claim.Score:F4}）。");
        return false;
    }
}
