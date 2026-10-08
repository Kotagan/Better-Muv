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
            SharedChildSessionService = new ChildSessionService();
            _childSessionService = SharedChildSessionService;
        }
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
