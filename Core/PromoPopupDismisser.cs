using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 挡住流程的弹窗：お知らせ「閉じる」、奖励确认粉钮 OK、宣传右上角 X、実績解除。
/// </summary>
public sealed class PromoPopupDismisser
{
    /// <summary>过低会把右上角 HUD 误当成关闭钮，形成连点死循环。</summary>
    private const double CloseThreshold = 0.78;
    private const double RewardOkThreshold = 0.58;
    private const double TojiruThreshold = 0.72;
    private const double OshiraseTitleThreshold = 0.72;
    private const int MaxFailedBursts = 3;
    private const int SuppressAfterFailMs = 20000;
    private const int MaxDismissRounds = 4;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _closeMatcher;
    private readonly TemplateMatcher _rewardOkMatcher;
    private readonly TemplateMatcher _rewardOkMatcherAlt;
    private readonly TemplateMatcher _tojiruMatcher;
    private readonly TemplateMatcher _oshiraseTitleMatcher;
    private int _failedBursts;
    private DateTime _suppressUntilUtc = DateTime.MinValue;

    /// <summary>
    /// 奖励确认粉钮 OK 搜索区（1080p）。须盖住「報酬を獲得」弹窗底部 OK（约 y=974），
    /// 旧 ROI 下沿只到 960，会导致永远扫不到。
    /// </summary>
    private static readonly ConfigPoint RewardOkTopLeft = new(550, 700);
    private static readonly ConfigSize RewardOkSize = new(820, 360);

    /// <summary>お知らせ/実績解除底部「閉じる」（1080p）。</summary>
    private static readonly ConfigPoint TojiruTopLeft = new(700, 880);
    private static readonly ConfigSize TojiruSize = new(520, 180);
    /// <summary>お知らせ 顶栏标题。</summary>
    private static readonly ConfigPoint OshiraseTitleTopLeft = new(700, 20);
    private static readonly ConfigSize OshiraseTitleSize = new(520, 120);
    /// <summary>閉じる固定点击（1080p；模板漏检兜底）。</summary>
    private static readonly ConfigPoint TojiruClick = new(957, 974);

    public PromoPopupDismisser(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _closeMatcher = TemplateAssets.Load("popup-close.png");
        _rewardOkMatcher = TemplateAssets.Load("settlement-confirm.png");
        _rewardOkMatcherAlt = TemplateAssets.Load("daily-shop-ok.png");
        _tojiruMatcher = TemplateAssets.Load("achievement-close.png");
        _oshiraseTitleMatcher = TemplateAssets.Load("oshirase-title.png");
    }

    /// <summary>连续关掉多层挡住弹窗（お知らせ 常叠在商店/任务入口前）。</summary>
    public async Task<int> DismissAllAsync(GameWindow window, CancellationToken cancellationToken)
    {
        int closed = 0;
        for (int i = 0; i < MaxDismissRounds; i++)
        {
            if (!await TryAsync(window, cancellationToken))
                break;
            closed++;
            await Task.Delay(350, cancellationToken);
            window = _screen.Refresh(window);
        }

        return closed;
    }

    /// <summary>当前是否仍有お知らせ标题（用于阻止误点主页钮）。</summary>
    public async Task<bool> IsOshiraseVisibleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult title = await _screen.ProbeAsync(
            window, _oshiraseTitleMatcher,
            OshiraseTitleTopLeft, OshiraseTitleSize,
            cancellationToken, OshiraseTitleThreshold);
        return title.IsMatch;
    }

    /// <returns>true 仅表示已成功关掉弹窗；未识别/点了仍在/抑制期内均返回 false。</returns>
    public async Task<bool> TryAsync(GameWindow window, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _suppressUntilUtc)
            return false;

        // 宣传层最常见且关闭钮 ROI 最小，优先探测可避免先跑多次大区域匹配。
        TemplateProbeResult probe = await _screen.ProbeAsync(
            window, _closeMatcher,
            _config.PopupCloseTopLeft, _config.PopupCloseSize,
            cancellationToken, CloseThreshold);
        if (probe.IsMatch)
            return await DismissPromoCloseAsync(window, probe, cancellationToken);

        // お知らせ优先：标题或閉じる任一命中即关，避免挡住クエスト。
        if (await TryDismissOshiraseOrTojiruAsync(window, cancellationToken))
            return true;

        if (await TryDismissRewardOkAsync(window, cancellationToken))
            return true;

        _failedBursts = 0;
        return false;
    }

    private async Task<bool> DismissPromoCloseAsync(
        GameWindow window, TemplateProbeResult probe, CancellationToken cancellationToken)
    {
        _log($"检测到宣传弹窗关闭钮（{probe.Score:F3}），点击关闭。");
        await _screen.ClickScreenAsync(window, probe.Center, cancellationToken);
        await Task.Delay(400, cancellationToken);

        TemplateProbeResult still = await _screen.ProbeAsync(
            window, _closeMatcher,
            _config.PopupCloseTopLeft, _config.PopupCloseSize,
            cancellationToken, CloseThreshold);
        if (!still.IsMatch)
        {
            _failedBursts = 0;
            return true;
        }

        _log($"关闭钮仍在（{still.Score:F3}），再点一次固定点。");
        await _screen.ClickAsync(window, _config.PopupCloseClick, "宣传弹窗关闭", cancellationToken);
        await Task.Delay(400, cancellationToken);

        TemplateProbeResult after = await _screen.ProbeAsync(
            window, _closeMatcher,
            _config.PopupCloseTopLeft, _config.PopupCloseSize,
            cancellationToken, CloseThreshold);
        if (!after.IsMatch)
        {
            _failedBursts = 0;
            return true;
        }

        _failedBursts++;
        _log($"宣传弹窗关闭无效（仍 {after.Score:F3}），连续失败 {_failedBursts}/{MaxFailedBursts}。");
        if (_failedBursts >= MaxFailedBursts)
        {
            _suppressUntilUtc = DateTime.UtcNow.AddMilliseconds(SuppressAfterFailMs);
            _failedBursts = 0;
            _log($"宣传关闭连续失败，暂停识别 {SuppressAfterFailMs / 1000}s，继续主流程。");
        }

        return false;
    }

    private async Task<bool> TryDismissOshiraseOrTojiruAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult title = await _screen.ProbeAsync(
            window, _oshiraseTitleMatcher,
            OshiraseTitleTopLeft, OshiraseTitleSize,
            cancellationToken, OshiraseTitleThreshold);
        TemplateProbeResult tojiru = await _screen.ProbeAsync(
            window, _tojiruMatcher,
            TojiruTopLeft, TojiruSize,
            cancellationToken, TojiruThreshold);

        if (!title.IsMatch && !tojiru.IsMatch)
            return false;

        if (tojiru.IsMatch)
        {
            _log($"检测到弹窗「閉じる」（{tojiru.Score:F3}），点击关闭。");
            await _screen.ClickProbeAsync(window, tojiru, "弹窗閉じる", cancellationToken, settleDelayMs: 250);
        }
        else
        {
            _log($"检测到お知らせ标题（{title.Score:F3}），「閉じる」未命中，点固定坐标。");
            await _screen.ClickAsync(window, TojiruClick, "お知らせ閉じる(坐标)", cancellationToken);
        }

        await Task.Delay(700, cancellationToken);
        window = _screen.Refresh(window);

        TemplateProbeResult titleStill = await _screen.ProbeAsync(
            window, _oshiraseTitleMatcher,
            OshiraseTitleTopLeft, OshiraseTitleSize,
            cancellationToken, OshiraseTitleThreshold);
        TemplateProbeResult tojiruStill = await _screen.ProbeAsync(
            window, _tojiruMatcher,
            TojiruTopLeft, TojiruSize,
            cancellationToken, TojiruThreshold);
        if (!titleStill.IsMatch && !tojiruStill.IsMatch)
        {
            _failedBursts = 0;
            return true;
        }

        _log($"お知らせ仍在（标题 {titleStill.Score:F3} / 閉じる {tojiruStill.Score:F3}），再点一次閉じる。");
        if (tojiruStill.IsMatch)
            await _screen.ClickProbeAsync(window, tojiruStill, "弹窗閉じる再点", cancellationToken, settleDelayMs: 250);
        else
            await _screen.ClickAsync(window, TojiruClick, "お知らせ閉じる再点(坐标)", cancellationToken);
        await Task.Delay(600, cancellationToken);

        window = _screen.Refresh(window);
        titleStill = await _screen.ProbeAsync(
            window, _oshiraseTitleMatcher,
            OshiraseTitleTopLeft, OshiraseTitleSize,
            cancellationToken, OshiraseTitleThreshold);
        bool gone = !titleStill.IsMatch;
        if (gone)
            _failedBursts = 0;
        return gone;
    }

    private async Task<bool> TryDismissRewardOkAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult? hit = null;
        foreach (TemplateMatcher matcher in new[] { _rewardOkMatcher, _rewardOkMatcherAlt })
        {
            TemplateProbeResult ok = await _screen.ProbeAsync(
                window, matcher, RewardOkTopLeft, RewardOkSize,
                cancellationToken, RewardOkThreshold);
            if (ok.IsMatch && (hit is null || ok.Score > hit.Score))
                hit = ok;
        }

        if (hit is null)
            return false;

        TemplateProbeResult matched = hit;
        _log($"检测到奖励确认 OK（{matched.Score:F3}），点击关闭。");
        await _screen.ClickProbeAsync(window, matched, "奖励确认OK", cancellationToken, settleDelayMs: 250);
        await Task.Delay(700, cancellationToken);

        TemplateProbeResult stillBest = TemplateProbes.Empty;
        foreach (TemplateMatcher matcher in new[] { _rewardOkMatcher, _rewardOkMatcherAlt })
        {
            TemplateProbeResult still = await _screen.ProbeAsync(
                window, matcher, RewardOkTopLeft, RewardOkSize,
                cancellationToken, RewardOkThreshold);
            if (still.Score > stillBest.Score)
                stillBest = still;
        }

        if (!stillBest.IsMatch)
        {
            _failedBursts = 0;
            return true;
        }

        _log($"奖励确认 OK 仍在（{stillBest.Score:F3}），再点一次中心。");
        await _screen.ClickProbeAsync(window, stillBest, "奖励确认OK再点", cancellationToken, settleDelayMs: 250);
        await Task.Delay(600, cancellationToken);

        TemplateProbeResult afterBest = TemplateProbes.Empty;
        foreach (TemplateMatcher matcher in new[] { _rewardOkMatcher, _rewardOkMatcherAlt })
        {
            TemplateProbeResult after = await _screen.ProbeAsync(
                window, matcher, RewardOkTopLeft, RewardOkSize,
                cancellationToken, RewardOkThreshold);
            if (after.Score > afterBest.Score)
                afterBest = after;
        }

        bool gone = !afterBest.IsMatch;
        if (gone)
            _failedBursts = 0;
        return gone;
    }
}
