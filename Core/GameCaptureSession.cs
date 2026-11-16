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

        bool launchedNow = existingWindow is null;
        GameWindow window = existingWindow ?? screen.FindWindow(config.WindowTitleKeyword);
        bool focused = false;
        for (int i = 0; i < 6; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = screen.Refresh(window);
            if (await screen.FocusAsync(window.Handle, cancellationToken))
            {
                focused = true;
                break;
            }
            await Task.Delay(700, cancellationToken);
        }
        if (!focused)
            throw new InvalidOperationException("无法将游戏置于前台。请先手动恢复游戏窗口。");
        window = await screen.EnsurePreferredClientAsync(window, cancellationToken);
        Window = window;
        IsRunning = true;
        ScreenRect viewport = screen.Viewport(window);
        string mode = config.WindowSelectionMode == "selected" ? "选择窗口/客户区" : "默认游戏/显示器";
        _log($"截图器已启动：{window.Title}（{mode} {viewport.Width}×{viewport.Height}）。");

        // 刚拉起时常停在 TOUCH TO START / 过渡屏，需连点几次；已在跑的游戏只在识别到标题页时才点。
        var titleEnter = new TitleScreenEnter(config, screen, _log);
        bool entered = launchedNow
            ? await titleEnter.EnsureHomeAfterLaunchAsync(window, cancellationToken)
            : await titleEnter.TryPassAsync(window, cancellationToken);
        Window = screen.Refresh(window);
        if (launchedNow && !entered)
            _log("截图器：启动后尚未确认主界面，后续任务将再尝试进入。");
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
        if (string.IsNullOrWhiteSpace(config.GameExecutablePath) ||
            !GamePathLocator.IsValid(config.GameExecutablePath, config.GameLaunchArguments))
        {
            string? found = GamePathLocator.TryRecoverLaunchPath(
                config.GameExecutablePath, config.GameLaunchArguments);
            if (found is null)
                throw new InvalidOperationException(
                    "已启用“同时启动游戏”，但路径无效。" +
                    (GamePathLocator.IsSchtasksLauncher(config.GameExecutablePath)
                        ? "DMM 快速启动请选择桌面 .lnk，或为 schtasks.exe 配置 /run /tn 参数。"
                        : "请在设置中自动搜索或手动浏览 .exe / .lnk。"));
            config.GameExecutablePath = found;
            if (GamePathLocator.IsShortcut(found))
            {
                string? lnkArgs = GamePathLocator.TryResolveShortcutArguments(found);
                if (GamePathLocator.HasSchtasksRunArguments(lnkArgs))
                    config.GameLaunchArguments = lnkArgs!;
            }
            ConfigStore.Save(config);
            _log("已自动恢复游戏启动路径：" + found);
        }
        if (!GamePathLocator.IsValid(config.GameExecutablePath, config.GameLaunchArguments))
            throw new FileNotFoundException("游戏路径无效（请选择存在的 .exe 或 .lnk）。", config.GameExecutablePath);

        string launchPath = config.GameExecutablePath;
        if (GamePathLocator.IsShortcut(launchPath))
            _log($"正在按快捷方式启动（DMM/计划任务均可）：{Path.GetFileName(launchPath)}");
        else if (GamePathLocator.IsSchtasksLauncher(launchPath))
            _log($"正在通过计划任务启动：schtasks {config.GameLaunchArguments}");
        else
            _log($"正在启动游戏：{Path.GetFileName(launchPath)}");
        Process.Start(GamePathLocator.CreateLaunchStartInfo(launchPath, config.GameLaunchArguments));

        // 拉起后立刻轮询找窗口；只有「还没找到窗口」时才继续等，不再写死空等几十秒。
        _log($"已请求启动，等待出现窗口（最多 {config.GameLaunchTimeoutSeconds} 秒）…");
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed.TotalSeconds < config.GameLaunchTimeoutSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                GameWindow found = screen.FindWindow(config.WindowTitleKeyword);
                _log($"已检测到游戏窗口（{timeout.Elapsed.TotalSeconds:0.0}s）：{found.Title}");
                // DMM 刚弹出时常常抢不到前台，稍等再继续。
                await Task.Delay(2500, cancellationToken);
                for (int i = 0; i < 8; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    found = screen.Refresh(found);
                    if (await screen.FocusAsync(found.Handle, cancellationToken))
                    {
                        _log($"游戏已置于前台（额外等待 {(i + 1) * 0.8:0.0}s）。");
                        return;
                    }
                    await Task.Delay(800, cancellationToken);
                }

                _log("窗口已出现但前台聚焦仍不稳定，继续启动截图器。");
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
