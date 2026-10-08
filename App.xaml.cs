using System.Windows;
using BetterMuv.Services;
using BetterMuv.Services.ChildSession;

namespace BetterMuv;

public partial class App : Application
{
    private RelativeMouseBridge? _childRelativeMouse;
    private ChildSessionService? _childSessionService;

    public static ChildSessionService? SharedChildSessionService { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppInstance.Initialize(e.Args);
        base.OnStartup(e);

        if (AppInstance.IsChildSession)
        {
            _childRelativeMouse = new RelativeMouseBridge();
            _childRelativeMouse.StartClient();
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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _childRelativeMouse?.Dispose();
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
