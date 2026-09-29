using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>主页采矿小人 → 採掘弹窗（收菜 / 免费加速共用）。</summary>
internal sealed class MiningPopupAccess
{
    private const int AfterHomeDelayMs = 700;
    private const int AfterOpenDelayMs = 1600;
    private const int RecognizeTimeoutMs = 8000;
    private const double EntryThreshold = 0.52;
    private const double TitleThreshold = 0.55;
    private const double ClaimOpenThreshold = 0.62;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly string _taskName;
    private readonly PromoPopupDismisser _promo;
    private readonly TemplateMatcher _entryMatcher;
    private readonly TemplateMatcher _titleMatcher;
    private readonly TemplateMatcher _claimMatcher;

    public MiningPopupAccess(AutomationConfig config, ScreenAutomation screen, Action<string> log, string taskName)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _taskName = taskName;
        _promo = new PromoPopupDismisser(config, screen, log);
        _entryMatcher = TemplateAssets.Load("mining-home-entry.png");
        _titleMatcher = TemplateAssets.Load("mining-title.png");
        _claimMatcher = TemplateAssets.Load("mining-claim.png");
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

        await _promo.DismissAllAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        // 若已在採掘弹窗，直接继续。
        if (await IsMiningOpenAsync(window, cancellationToken))
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
        await _promo.DismissAllAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        return await OpenAsync(window, cancellationToken);
    }

    public async Task<(GameWindow Window, bool Opened)> OpenAsync(GameWindow window, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _promo.DismissAllAsync(window, cancellationToken);
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
                _log($"{_taskName}：已识别采矿入口（{entry.Score:F4}），点击。");
                await _screen.ClickProbeAsync(window, entry, "采矿入口", cancellationToken, settleDelayMs: 200);
            }
            else
            {
                _log($"{_taskName}：未识别入口（最高 {entry.Score:F4}），不点击。");
            }

            await Task.Delay(AfterOpenDelayMs, cancellationToken);
            window = _screen.Refresh(window);
            await _promo.DismissAllAsync(window, cancellationToken);
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

    private async Task<bool> IsMiningOpenAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult title = await _screen.WaitForProbeAsync(
            window,
            _titleMatcher,
            _config.MiningTitleTopLeft,
            _config.MiningTitleSize,
            cancellationToken,
            timeoutMs: 2500,
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
            timeoutMs: 2000,
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
