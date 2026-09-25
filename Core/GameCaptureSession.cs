using System.Diagnostics;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 首页截图器的生命周期。它验证游戏窗口与显示器；实际截图仍按需由自动化流程获取，
/// 不会在后台无意义地持续占用 CPU。
/// </summary>
public sealed class GameCaptureSession
{
    private readonly Action<string> _log;

    public GameCaptureSession(Action<string> log) => _log = log;

    public bool IsRunning { get; private set; }
    public GameWindow? Window { get; private set; }

    /// <summary>截图器标记运行中时，探测游戏窗口是否仍在；不在则清空状态以便重新 Start。</summary>
    public bool HasLiveWindow(AutomationConfig config)
    {
        if (!IsRunning)
            return false;
        try
        {
            var screen = new ScreenAutomation(config, _ => { });
            Window = screen.FindWindow(config.WindowTitleKeyword);
            return true;
        }
        catch (InvalidOperationException)
        {
            IsRunning = false;
            Window = null;
            return false;
        }
    }

    public async Task StartAsync(AutomationConfig config, CancellationToken cancellationToken)
    {
        var screen = new ScreenAutomation(config, _log);
        GameWindow? existingWindow = null;
        try
        {
            existingWindow = screen.FindWindow(config.WindowTitleKeyword);
        }
        catch (InvalidOperationException)
        {
            // 游戏尚未启动；若用户已启用自动启动，下面会处理。
        }
        if (existingWindow is null && config.LaunchGameWithCapture &&
            !config.WindowSelectionMode.Equals("selected", StringComparison.OrdinalIgnoreCase))
            await LaunchGameAndWaitAsync(config, screen, cancellationToken);

        GameWindow window = existingWindow ?? screen.FindWindow(config.WindowTitleKeyword);
        if (!await screen.FocusAsync(window.Handle, cancellationToken))
            throw new InvalidOperationException("无法将游戏置于前台。请先手动恢复游戏窗口。");
        window = await screen.EnsurePreferredClientAsync(window, cancellationToken);
        Window = window;
        IsRunning = true;
        ScreenRect viewport = screen.Viewport(window);
        string mode = config.WindowSelectionMode == "selected" ? "选择窗口/客户区" : "默认游戏/显示器";
        _log($"截图器已启动：{window.Title}（{mode} {viewport.Width}×{viewport.Height}）。");
    }

    public void Stop()
    {
        IsRunning = false;
        Window = null;
        _log("截图器已停止。");
    }

    private async Task LaunchGameAndWaitAsync(
        AutomationConfig config, ScreenAutomation screen, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.GameExecutablePath) || !GamePathLocator.IsValid(config.GameExecutablePath))
        {
            string? found = GamePathLocator.TryFind(config.GameExecutablePath);
            if (found is null)
                throw new InvalidOperationException("已启用“同时启动游戏”，但尚未找到游戏 exe。请在设置中自动搜索或手动浏览。");
            config.GameExecutablePath = found;
            ConfigStore.Save(config);
            _log("已自动找到并永久保存游戏：" + found);
        }
        if (!GamePathLocator.IsValid(config.GameExecutablePath))
            throw new FileNotFoundException("游戏 exe 无效（必须是 muv_luv_girlsgardenx_cl.exe）。", config.GameExecutablePath);

        _log("正在启动游戏：" + Path.GetFileName(config.GameExecutablePath));
        Process.Start(new ProcessStartInfo
        {
            FileName = config.GameExecutablePath,
            Arguments = config.GameLaunchArguments,
            WorkingDirectory = Path.GetDirectoryName(config.GameExecutablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        });

        // 启动后先等客户端完成加载，再开始找窗口。
        const int launchSettleSeconds = 30;
        _log($"游戏已拉起，等待 {launchSettleSeconds} 秒后再检测窗口…");
        await Task.Delay(TimeSpan.FromSeconds(launchSettleSeconds), cancellationToken);

        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed.TotalSeconds < config.GameLaunchTimeoutSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = screen.FindWindow(config.WindowTitleKeyword);
                _log("已检测到游戏窗口，继续启动截图器。");
                return;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(500, cancellationToken);
            }
        }
        throw new TimeoutException($"等待游戏窗口超过 {config.GameLaunchTimeoutSeconds} 秒。");
    }
}
