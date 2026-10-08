using System.Windows;
using BetterMuv.Services;
using BetterMuv.Services.ChildSession;

namespace BetterMuv;

public partial class App : Application
{
    private ChildSessionTaskBridge? _childTaskBridge;
    private ChildSessionService? _childSessionService;

    public static ChildSessionService? SharedChildSessionService { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppInstance.Initialize(e.Args);
        base.OnStartup(e);

        if (AppInstance.IsChildSession)
        {
            _childTaskBridge = new ChildSessionTaskBridge();
            // MainWindow 就绪后会挂上真正的任务处理函数。
            _childTaskBridge.StartServer(_ => ChildSessionTaskBridge.ReplyIdle);
        }
        else
        {
            // 主窗口创建前先用 Debug；MainWindow 加载后会改挂 AppendLog。
            SharedChildSessionService = new ChildSessionService(msg =>
                System.Diagnostics.Debug.WriteLine("[ChildSession] " + msg));
            _childSessionService = SharedChildSessionService;
        }
    }

    /// <summary>主窗口就绪后把分身日志接到 UI/文件日志。</summary>
    public static void AttachChildSessionLogger(Action<string> log)
    {
        if (SharedChildSessionService is null)
            return;
        // 重新构造会丢窗口状态；改为订阅失败事件由 MainWindow 写日志。
        SharedChildSessionService.ConnectionFailed += (_, e) =>
        {
            string oneLine = e.Message.Replace("\r\n", " | ").Replace('\n', ' ');
            log($"桌面分身连接失败：code={e.ErrorCode} ext={e.ExtendedErrorCode?.ToString() ?? "-"} | {oneLine}");
        };
    }

    /// <summary>分身实例：把管道命令接到 MainWindow 任务控制。</summary>
    public static void AttachChildSessionTaskHandler(Func<string, string> handler)
    {
        if (Current is not App app || app._childTaskBridge is null)
            return;
        app._childTaskBridge.StartServer(handler);
    }

    /// <summary>关掉/隐藏桌面分身观看窗后，重新唤起主界面。</summary>
    public static void RestoreRootMainWindow()
    {
        if (!AppInstance.IsRoot)
            return;

        try
        {
            Current?.Dispatcher.Invoke(() =>
            {
                try
                {
                    MainWindow? existing = null;
                    foreach (Window window in Current.Windows)
                    {
                        if (window is MainWindow main)
                        {
                            existing = main;
                            break;
                        }
                    }

                    if (existing is not null)
                    {
                        if (!existing.IsVisible)
                            existing.Show();
                        if (existing.WindowState == WindowState.Minimized)
                            existing.WindowState = WindowState.Normal;
                        existing.Activate();
                        Current.MainWindow = existing;
                        return;
                    }

                    var created = new MainWindow();
                    Current.MainWindow = created;
                    created.Show();
                    created.Activate();
                }
                catch
                {
                    // 唤起主界面失败不阻断分身关闭
                }
            });
        }
        catch
        {
            // ignore
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _childTaskBridge?.Dispose();
            _childSessionService?.Dispose();
            SharedChildSessionService = null;
        }
        catch
        {
            // 退出清理失败不阻止关闭
        }

        base.OnExit(e);
    }
}
