using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 战斗结算/战败页：优先点「ホームへ」回主页（DEFEAT 页旧模板匹配不上，需专用模板）。
/// </summary>
public sealed class BattleResultHome
{
    private const double ResultHomeThreshold = 0.72;
    private const double DefeatBannerThreshold = 0.70;
    private const double RematchThreshold = 0.70;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _toHomeLegacy = TemplateAssets.Load("main-quest-to-home.png");
    private readonly TemplateMatcher _toHomeResult = TemplateAssets.Load("battle-result-to-home.png");
    private readonly TemplateMatcher _rematchLegacy = TemplateAssets.Load("main-quest-rematch.png");
    private readonly TemplateMatcher _rematchResult = TemplateAssets.Load("battle-result-rematch.png");
    private readonly TemplateMatcher _defeatBanner = TemplateAssets.Load("battle-defeat-banner.png");

    public BattleResultHome(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
    }

    /// <summary>
    /// 若在结算/战败页则点「ホームへ」。返回 true 表示已点击。
    /// </summary>
    public async Task<bool> TryClickHomeAsync(
        GameWindow window, string reason, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        TemplateProbeResult home = await ProbeBestToHomeAsync(window, cancellationToken);
        if (home.IsMatch)
        {
            _log($"{reason}：识别「ホームへ」（{home.Score:F3}），点击回主页。");
            await _screen.ClickProbeAsync(window, home, $"{reason}ホームへ", cancellationToken, 800);
            return true;
        }

        bool defeatLike = await LooksLikeDefeatOrRematchAsync(window, cancellationToken);
        if (!defeatLike)
            return false;

        _log($"{reason}：已像战败/再戦页但「ホームへ」未命中，点击配置坐标兜底。");
        await _screen.ClickAsync(
            window, _config.MainQuestToHomeClick, $"{reason}ホームへ(坐标)", cancellationToken);
        await Task.Delay(800, cancellationToken);
        return true;
    }

    public async Task<TemplateProbeResult> ProbeBestToHomeAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        // 战败页按钮更靠下；兼容用户配置里偏上的旧 ROI。
        var tl = new ConfigPoint(
            Math.Min(_config.MainQuestToHomeTopLeft.X, 20),
            Math.Min(_config.MainQuestToHomeTopLeft.Y, 960));
        var size = new ConfigSize(
            Math.Max(_config.MainQuestToHomeSize.Width, 480),
            Math.Max(_config.MainQuestToHomeSize.Height, 120));
        TemplateProbeResult a = await _screen.ProbeAsync(
            window, _toHomeResult, tl, size, cancellationToken, ResultHomeThreshold);
        TemplateProbeResult b = await _screen.ProbeAsync(
            window, _toHomeLegacy, tl, size, cancellationToken, ResultHomeThreshold);
        return a.Score >= b.Score ? a : b;
    }

    public async Task<bool> LooksLikeDefeatOrRematchAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult defeat = await _screen.ProbeAsync(
            window, _defeatBanner,
            new ConfigPoint(400, 0), new ConfigSize(1100, 280),
            cancellationToken, DefeatBannerThreshold);
        if (defeat.IsMatch)
            return true;

        // 战败「再戦」偏右下，搜索区略宽于旧胜利再戦 ROI。
        var rematchTl = new ConfigPoint(
            Math.Min(_config.MainQuestRematchTopLeft.X, 1480),
            Math.Min(_config.MainQuestRematchTopLeft.Y, 960));
        var rematchSize = new ConfigSize(
            Math.Max(_config.MainQuestRematchSize.Width, 440),
            Math.Max(_config.MainQuestRematchSize.Height, 120));
        TemplateProbeResult rematchNew = await _screen.ProbeAsync(
            window, _rematchResult, rematchTl, rematchSize, cancellationToken, RematchThreshold);
        if (rematchNew.IsMatch)
            return true;

        TemplateProbeResult rematchOld = await _screen.ProbeAsync(
            window, _rematchLegacy, rematchTl, rematchSize, cancellationToken, RematchThreshold);
        return rematchOld.IsMatch;
    }
}
