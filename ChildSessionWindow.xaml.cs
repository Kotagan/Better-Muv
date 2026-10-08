using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterMuv.Core;
using BetterMuv.Services;
using BetterMuv.Services.ChildSession;
using Microsoft.Win32;
using DrawingSize = System.Drawing.Size;

namespace BetterMuv;

public partial class ChildSessionWindow : Window
{
    private readonly ChildSessionService _service;
    private readonly RdpActiveXHost _rdpHost = new();
    private readonly DispatcherTimer _keyboardFocusTimer;
    private readonly DispatcherTimer _taskStatusTimer;
    private bool _childTaskBusy;
    private bool _taskCommandInFlight;
    private bool _smallWindow;
    private bool _closingInProgress;
    private const double NormalWidth = 1280;
    private const double NormalHeight = 752;
    private const double SmallWidth = 500;
    private const double SmallHeight = 292;

    internal RdpActiveXHost RdpHost => _rdpHost;

    internal bool AllowClose { get; set; }

    public ChildSessionWindow(ChildSessionService service)
    {
        _service = service;
        InitializeComponent();
        FormsHost.Child = _rdpHost;
        Topmost = _service.TopmostEnabled;
        TopmostMenu.IsChecked = _service.TopmostEnabled;
        KeepAspectMenu.IsChecked = _service.KeepAspectRatio;
        SystemShortcutsMenu.IsChecked = _service.SendSystemShortcutsToRemote;
        UpdateMuteButton();
        ApplySavedPosition(small: false);
        _service.StateChanged += (_, _) => Dispatcher.Invoke(RefreshUi);
        _service.ConnectionFailed += (_, e) => Dispatcher.Invoke(() => OnConnectionFailed(e));

        _keyboardFocusTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _keyboardFocusTimer.Tick += (_, _) => _service.SyncKeyboardFocusToMouse();
        _taskStatusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _taskStatusTimer.Tick += async (_, _) => await RefreshTaskButtonAsync();
        Loaded += (_, _) =>
        {
            _keyboardFocusTimer.Start();
            _taskStatusTimer.Start();
        };
        Closed += (_, _) =>
        {
            _keyboardFocusTimer.Stop();
            _taskStatusTimer.Stop();
        };
        RefreshUi();
    }

    private async void OnConnectionFailed(ChildSessionConnectionFailedEventArgs e)
    {
        bool credentialLikely = IsLikelyCredentialFailure(e);
        // 本机未挂 RDP Wrapper 时，516 多数是密码/PIN/空密码问题。
        bool treat516AsCredential = e.ErrorCode is 516 or 0x204
            && !ChildSessionNativeMethods.IsRdpWrapperEnabled();
        if (credentialLikely || treat516AsCredential)
            ChildSessionCredentialStore.Clear();

        if (treat516AsCredential || credentialLikely)
        {
            MessageBoxResult retry = MessageBox.Show(
                this,
                e.Message + "\n\n"
                + "已清除本机保存的分身密码。\n"
                + "是否重新输入账户密码并再试一次？\n"
                + "（必须用 Windows「密码」，不能用 PIN）",
                "桌面分身连接失败",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (retry == MessageBoxResult.Yes)
            {
                if (ElevationHelper.IsElevated())
                {
                    try
                    {
                        await Task.Run(() => RdpWrapperSwitch.TryRestartTermService(_ => { }));
                    }
                    catch
                    {
                        // 重启 TermService 失败不阻断重输密码重试。
                    }
                }

                await StartSessionAsync(forceCredentialPrompt: true);
            }

            return;
        }

        MessageBox.Show(this, e.Message, "桌面分身连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool IsLikelyCredentialFailure(ChildSessionConnectionFailedEventArgs e)
    {
        if (e.ErrorCode is 0 or 264)
            return true;
        string msg = e.Message;
        return msg.Contains("凭据", StringComparison.Ordinal)
            || msg.Contains("密码", StringComparison.Ordinal)
            || msg.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("logon failure", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshUi()
    {
        StatusText.Text = _service.StatusText;
        StatusDot.Fill = _service.ConnectedState switch
        {
            1 => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
            2 => new SolidColorBrush(Color.FromRgb(255, 193, 7)),
            _ => new SolidColorBrush(Color.FromRgb(107, 119, 133))
        };
        GuideOverlay.Visibility = _service.ConnectedState == 0 && !_closingInProgress
            ? Visibility.Visible
            : Visibility.Collapsed;
        Topmost = _service.TopmostEnabled;
        UpdateMuteButton();
        UpdateTaskButtonContent();
        _ = RefreshTaskButtonAsync();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        bool forcePrompt = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        await StartSessionAsync(forceCredentialPrompt: forcePrompt);
    }

    private async void TaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service.ConnectedState != 1)
        {
            MessageBox.Show(this, "请先连接桌面分身，并等待分身内 Better-Muv 启动完成。",
                "启动任务", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_taskCommandInFlight)
            return;

        _taskCommandInFlight = true;
        TaskButton.IsEnabled = false;
        try
        {
            bool? busy = _childTaskBusy;
            if (busy != true)
            {
                // 再查一次，避免状态过期
                busy = await ChildSessionTaskBridge.TryQueryBusyAsync() ?? false;
            }

            string cmd = busy == true
                ? ChildSessionTaskBridge.CmdStop
                : ChildSessionTaskBridge.CmdStart;
            string reply = await ChildSessionTaskBridge.SendAsync(cmd);
            if (reply.StartsWith("ERR", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "无法控制分身内 Better-Muv：\n" + reply
                    + "\n\n请确认分身内已自动启动 Better-Muv。",
                    "任务控制",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _childTaskBusy = string.Equals(cmd, ChildSessionTaskBridge.CmdStart, StringComparison.Ordinal)
                || string.Equals(reply, ChildSessionTaskBridge.ReplyBusy, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(cmd, ChildSessionTaskBridge.CmdStop, StringComparison.Ordinal))
                _childTaskBusy = false;
            UpdateTaskButtonContent();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "连不上分身内 Better-Muv（可能还在启动）：\n" + ex.GetBaseException().Message,
                "任务控制",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _taskCommandInFlight = false;
            TaskButton.IsEnabled = true;
            await RefreshTaskButtonAsync();
        }
    }

    private async Task RefreshTaskButtonAsync()
    {
        if (_taskCommandInFlight || _service.ConnectedState != 1)
        {
            UpdateTaskButtonContent();
            return;
        }

        bool? busy = await ChildSessionTaskBridge.TryQueryBusyAsync();
        if (busy is not null)
            _childTaskBusy = busy.Value;
        UpdateTaskButtonContent();
    }

    private void UpdateTaskButtonContent()
    {
        bool connected = _service.ConnectedState == 1;
        TaskButton.IsEnabled = connected && !_taskCommandInFlight;
        TaskButton.Content = _childTaskBusy ? "停止任务" : "启动任务";
        TaskButton.ToolTip = !connected
            ? "分身未连接"
            : _childTaskBusy
                ? "停止分身内 Better-Muv 当前任务"
                : "在分身内 Better-Muv 启动一条龙";
    }

    /// <summary>打开分身窗口后自动调用：处理 Wrapper / 凭据并连接。</summary>
    internal async Task StartSessionAsync(bool forceCredentialPrompt = false)
    {
        if (_service.ConnectedState == 1)
        {
            RefreshUi();
            return;
        }

        try
        {
            // 首次启用 Child Session 需要管理员；未提权时先问，避免只弹 Win32 错误 5。
            if (!ElevationHelper.IsElevated()
                && !ChildSessionNativeMethods.IsChildSessionsEnabled())
            {
                MessageBoxResult elevateChoice = MessageBox.Show(
                    this,
                    "启用桌面分身（RDP Child Session）需要管理员权限。\n\n"
                    + "是否以管理员权限重启 Better-Muv？\n"
                    + "（重启后会自动打开并启动桌面分身）",
                    "需要管理员权限",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (elevateChoice != MessageBoxResult.Yes)
                    return;
                if (!ElevationHelper.TryRestartElevated(AppInstance.OpenChildSessionArgument))
                {
                    MessageBox.Show(this, "已取消管理员授权，未重启。", "桌面分身",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            bool disableWrapper = false;
            if (_service.IsRdpWrapperEnabled())
            {
                if (!ElevationHelper.IsElevated())
                {
                    MessageBoxResult elevateChoice = MessageBox.Show(
                        this,
                        "检测到本机正在使用 RDP Wrapper（rdpwrap.dll）。\n\n"
                        + "它与桌面分身冲突（常见错误 516），临时切回系统原生 RDP 需要管理员权限。\n\n"
                        + "是否以管理员权限重启 Better-Muv？\n"
                        + "（重启后会自动打开并启动桌面分身）",
                        "需要管理员权限",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (elevateChoice != MessageBoxResult.Yes)
                        return;
                    if (!ElevationHelper.TryRestartElevated(AppInstance.OpenChildSessionArgument))
                    {
                        MessageBox.Show(this, "已取消管理员授权，未重启。", "桌面分身",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                MessageBoxResult choice = MessageBox.Show(
                    this,
                    "检测到本机正在使用 RDP Wrapper（rdpwrap.dll）。\n\n"
                    + "它与桌面分身（Child Session）冲突，通常会直接报错误 516。\n\n"
                    + "选「是」：临时切回系统原生 termsrv.dll 并启动分身"
                    + "（关闭分身 / 退出程序时自动还原 Wrapper）。\n"
                    + "选「否」：取消启动。\n\n"
                    + "说明：临时切换期间，依赖 RDP Wrapper 的「本地多用户远程」会不可用。",
                    "RDP Wrapper 与桌面分身冲突",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes)
                    return;
                disableWrapper = true;
            }

            // 已记住密码则直接用；按住 Ctrl 点启动可强制重新输入。
            ChildSessionLoginCredentials? credentials = forceCredentialPrompt
                ? null
                : ChildSessionCredentialStore.TryLoadForCurrentUser();
            if (credentials is null)
            {
                var loginWindow = new ChildSessionLoginWindow { Owner = this };
                if (loginWindow.ShowDialog() != true || loginWindow.Credentials is null)
                    return;
                credentials = loginWindow.Credentials;
            }

            TaskButton.IsEnabled = false;
            await _service.StartAsync(
                temporarilyDisableRdpWrapper: disableWrapper,
                loginCredentials: credentials);
        }
        catch (Exception ex)
        {
            if (TryOfferElevateRestart(ex))
                return;
            MessageBox.Show(this, ex.GetBaseException().Message, "启动桌面分身失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshUi();
        }
    }

    private bool TryOfferElevateRestart(Exception ex)
    {
        if (ElevationHelper.IsElevated())
            return false;

        Exception root = ex.GetBaseException();
        bool accessDenied = root is System.ComponentModel.Win32Exception win32
            && win32.NativeErrorCode == 5;
        bool needsAdmin = accessDenied
            || root is UnauthorizedAccessException
            || root is System.Security.SecurityException
            || root.Message.Contains("管理员", StringComparison.Ordinal);
        if (!needsAdmin)
            return false;

        MessageBoxResult choice = MessageBox.Show(
            this,
            "当前操作需要管理员权限。\n\n"
            + root.Message + "\n\n"
            + "是否以管理员权限重启 Better-Muv？",
            "需要管理员权限",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes)
            return false;

        if (!ElevationHelper.TryRestartElevated(AppInstance.OpenChildSessionArgument))
        {
            MessageBox.Show(this, "已取消管理员授权，未重启。", "桌面分身",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return true;
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        _service.HideWindow();
        App.RestoreRootMainWindow();
    }

    private void SwitchWindowButton_Click(object sender, RoutedEventArgs e)
    {
        try { _service.ShowChildSessionTaskView(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "桌面分身", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _service.SetAudioMuted(!_service.AudioMuted);
        UpdateMuteButton();
    }

    private void ControlCenterButton_Click(object sender, RoutedEventArgs e)
    {
        ControlMenu.PlacementTarget = sender as FrameworkElement;
        ControlMenu.IsOpen = true;
    }

    private void UnlockBrowser_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirm = MessageBox.Show(
            this,
            "将结束主桌面上的 Chrome / Edge 进程，以便分身里的登录页能打开浏览器。\n\n"
            + "未保存的网页会丢失。是否继续？",
            "结束主桌面浏览器",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            int killed = ChildSessionBrowserUnlock.TerminateBrowsersInCurrentSession();
            MessageBox.Show(
                this,
                killed == 0
                    ? "主桌面未发现 Chrome / Edge 进程。若仍拉不起，请检查托盘里是否还有后台运行，或改用独立用户数据目录启动 Chrome。"
                    : $"已结束主桌面约 {killed} 个浏览器进程。请回到分身里再次点击登录。",
                "结束主桌面浏览器",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, "结束浏览器失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void LaunchApp_Click(object sender, RoutedEventArgs e)
    {
        try { await _service.LaunchBetterMuvAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SmallWindow_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentPosition();
        _smallWindow = !_smallWindow;
        if (_smallWindow)
        {
            Width = SmallWidth;
            Height = SmallHeight;
            SmallWindowMenu.Header = "还原窗口";
            ApplySavedPosition(small: true);
        }
        else
        {
            Width = NormalWidth;
            Height = NormalHeight;
            SmallWindowMenu.Header = "小窗模式";
            ApplySavedPosition(small: false);
        }
    }

    private void Adaptive_Click(object sender, RoutedEventArgs e) => _service.SetSmartSizing(true);
    private void OneToOne_Click(object sender, RoutedEventArgs e) => _service.SetSmartSizing(false);

    private void KeepAspect_Click(object sender, RoutedEventArgs e)
    {
        _service.SetKeepAspectRatio(KeepAspectMenu.IsChecked == true);
        if (_service.KeepAspectRatio && !_smallWindow)
            Height = Width * 9.0 / 16.0 + 40 + 36;
    }

    private void SystemShortcuts_Click(object sender, RoutedEventArgs e) =>
        _ = _service.SetSendSystemShortcutsToRemote(SystemShortcutsMenu.IsChecked == true);

    private void ShowDesktop_Click(object sender, RoutedEventArgs e)
    {
        try { _service.ShowChildSessionDesktop(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "桌面分身", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowTaskView_Click(object sender, RoutedEventArgs e) => SwitchWindowButton_Click(sender, e);

    private async void LaunchExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "可执行文件|*.exe" };
        if (dialog.ShowDialog(this) != true)
            return;
        try { await _service.LaunchExecutableAsync(dialog.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _service.SetTopmost(TopmostMenu.IsChecked == true);
        Topmost = _service.TopmostEnabled;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (AllowClose || Dispatcher.HasShutdownStarted)
            return;

        e.Cancel = true;
        if (_closingInProgress)
            return;

        if (_service.ChildSessionId is null && _service.ConnectedState == 0)
        {
            SaveCurrentPosition();
            App.RestoreRootMainWindow();
            AllowClose = true;
            Close();
            return;
        }

        // 是=保留会话仅隐藏；否=注销后再关闭窗口；取消=不关。
        MessageBoxResult choice = MessageBox.Show(
            this,
            "关闭桌面分身窗口：\n\n"
            + "「是」：只断开画面并隐藏窗口，分身会话和游戏继续跑（登录保留）\n"
            + "「否」：先注销分身，完成后再关闭窗口（下次需重新登录游戏）\n"
            + "「取消」：不关闭",
            "关闭 Better-Muv 桌面分身",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel)
            return;

        await FinishCloseWindowAsync(logoff: choice == MessageBoxResult.No);
    }

    private async void LogoffSession_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirm = MessageBox.Show(
            this,
            "将注销桌面分身会话，其中游戏等都会关闭，下次进入需重新登录。\n\n注销完成后会关闭本窗口。是否继续？",
            "注销分身会话",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        await FinishCloseWindowAsync(logoff: true);
    }

    /// <summary>
    /// 注销路径：等会话注销完成后再真正 Close；仅断开则 Hide 以便下次复用。
    /// </summary>
    private async Task FinishCloseWindowAsync(bool logoff)
    {
        if (_closingInProgress)
            return;

        _closingInProgress = true;
        ClosingOverlay.Visibility = Visibility.Visible;
        GuideOverlay.Visibility = Visibility.Collapsed;
        SaveCurrentPosition();
        try
        {
            if (logoff)
            {
                await _service.LogoffAndHideAsync();
                App.RestoreRootMainWindow();
                AllowClose = true;
                Close();
            }
            else
            {
                await _service.DisconnectAndHideAsync();
                App.RestoreRootMainWindow();
            }
        }
        finally
        {
            _closingInProgress = false;
            if (!AllowClose)
                ClosingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateMuteButton()
    {
        MuteButton.Content = _service.AudioMuted ? "取消静音" : "静音";
    }

    private void ApplySavedPosition(bool small)
    {
        WindowPositionConfig? pos = small ? _service.SmallWindowPosition : _service.NormalWindowPosition;
        if (pos is null)
            return;
        Left = pos.Left;
        Top = pos.Top;
    }

    private void SaveCurrentPosition()
    {
        var pos = new WindowPositionConfig((int)Left, (int)Top);
        if (_smallWindow)
            _service.SmallWindowPosition = pos;
        else
            _service.NormalWindowPosition = pos;
        try
        {
            AutomationConfig config = ConfigStore.Load();
            config.ChildSession ??= new ChildSessionConfig();
            config.ChildSession.NormalWindowPosition = _service.NormalWindowPosition;
            config.ChildSession.SmallWindowPosition = _service.SmallWindowPosition;
            ConfigStore.Save(config);
        }
        catch
        {
            // 位置保存失败不影响关闭
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_service.KeepAspectRatio && !_smallWindow && sizeInfo.WidthChanged)
            Height = Width * 9.0 / 16.0 + 76;
    }
}
