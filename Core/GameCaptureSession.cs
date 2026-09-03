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
        if (existingWindow is null && config.LaunchGameWithCapture)
            await LaunchGameAndWaitAsync(config, screen, cancellationToken);

        GameWindow window = existingWindow ?? screen.FindWindow(config.WindowTitleKeyword);
        if (!await screen.FocusAsync(window.Handle, cancellationToken))
            throw new InvalidOperationException("无法将游戏置于前台。请先手动恢复游戏窗口。");
        window = screen.Refresh(window);
        screen.EnsureSixteenByNine(window);
        Window = window;
        IsRunning = true;
        _log($"截图器已启动：{window.Title}（显示器 {window.DisplayRect.Width}×{window.DisplayRect.Height}）。");
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
        if (string.IsNullOrWhiteSpace(config.GameExecutablePath))
            throw new InvalidOperationException("已启用“同时启动游戏”，但尚未在设置中选择游戏 exe。 ");
        if (!File.Exists(config.GameExecutablePath))
            throw new FileNotFoundException("游戏 exe 不存在。", config.GameExecutablePath);

        _log("正在启动游戏：" + Path.GetFileName(config.GameExecutablePath));
        Process.Start(new ProcessStartInfo
        {
            FileName = config.GameExecutablePath,
            Arguments = config.GameLaunchArguments,
            WorkingDirectory = Path.GetDirectoryName(config.GameExecutablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        });

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
