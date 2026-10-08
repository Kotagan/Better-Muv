using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using BetterMuv.Core;
using BetterMuv.Services.ChildSession;
using Microsoft.Win32;
using DrawingSize = System.Drawing.Size;

namespace BetterMuv;

public partial class ChildSessionWindow : Window
{
    private readonly ChildSessionService _service;
    private readonly RdpActiveXHost _rdpHost = new();
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
        UpdateGameMouseButton();
        UpdateMuteButton();
        ApplySavedPosition(small: false);
        _service.StateChanged += (_, _) => Dispatcher.Invoke(RefreshUi);
        _service.ConnectionFailed += (_, e) => Dispatcher.Invoke(() =>
            MessageBox.Show(this, e.Message, "桌面分身连接失败", MessageBoxButton.OK, MessageBoxImage.Warning));
        RefreshUi();
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
        UpdateGameMouseButton();
        UpdateMuteButton();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_service.IsRdpWrapperEnabled())
            {
                MessageBoxResult choice = MessageBox.Show(
                    this,
                    "检测到系统已安装并启用 RDP Wrapper。这可能导致 Child Session 异常。是否仍要继续？",
                    "RDP Wrapper 兼容性提醒",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes)
                    return;
            }

            StartButton.IsEnabled = false;
            await _service.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, "启动桌面分身失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => _service.HideWindow();

    private void SwitchWindowButton_Click(object sender, RoutedEventArgs e)
    {
        try { _service.ShowChildSessionTaskView(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "桌面分身", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void GameMouseButton_Click(object sender, RoutedEventArgs e)
    {
        _service.SetGameMouseModeEnabled(!_service.IsGameMouseModeEnabled);
        UpdateGameMouseButton();
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
            Hide();
            return;
        }

        MessageBoxResult choice = MessageBox.Show(
            this,
            "关闭会断开 RDP 并注销桌面分身，其中所有正在运行的软件都会被关闭，未保存的数据会丢失。是否继续？",
            "关闭 Better-Muv 桌面分身",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes)
            return;

        _closingInProgress = true;
        ClosingOverlay.Visibility = Visibility.Visible;
        GuideOverlay.Visibility = Visibility.Collapsed;
        SaveCurrentPosition();
        try
        {
            await _service.LogoffAndHideAsync();
        }
        finally
        {
            _closingInProgress = false;
            ClosingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateGameMouseButton()
    {
        GameMouseButton.Content = _service.IsGameMouseModeEnabled ? "普通鼠标" : "游戏鼠标";
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
