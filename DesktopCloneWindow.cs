using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterMuv;

/// <summary>
/// 桌面分身的控制台入口。Child Session 的创建由 Windows 远程桌面组件处理；
/// 该窗口集中说明所需权限，并为已经创建的本机会话提供连接入口。
/// </summary>
internal sealed class DesktopCloneWindow : Window
{
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(196, 211, 225)), TextWrapping = TextWrapping.Wrap };

    public DesktopCloneWindow()
    {
        Title = "Better-Muv 桌面分身";
        Width = 760;
        Height = 510;
        MinWidth = 620;
        MinHeight = 420;
        Background = new SolidColorBrush(Color.FromRgb(32, 38, 48));
        Foreground = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(30) };
        root.Children.Add(new TextBlock { Text = "桌面分身", FontSize = 28, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "在独立 Windows 会话中运行游戏和 Better-Muv，避免占用主桌面的鼠标与键盘。",
            Foreground = new SolidColorBrush(Color.FromRgb(174, 185, 197)), Margin = new Thickness(0, 8, 0, 22), TextWrapping = TextWrapping.Wrap
        });
        var notice = new Border { Background = new SolidColorBrush(Color.FromRgb(43, 50, 61)), CornerRadius = new CornerRadius(10), Padding = new Thickness(18) };
        notice.Child = new TextBlock
        {
            Text = "使用条件：需要管理员权限、Windows 本机账户的真实密码（不能只用 PIN），且系统不能启用 RDP Wrapper。登录分身后，请在分身内点击“启动本工具”，再独立启动截图器和迷宫探索。",
            TextWrapping = TextWrapping.Wrap, LineHeight = 22, Foreground = new SolidColorBrush(Color.FromRgb(218, 227, 236))
        };
        root.Children.Add(notice);
        _status.Margin = new Thickness(0, 22, 0, 18);
        root.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var connect = new Button { Content = "打开本机远程桌面", Padding = new Thickness(18, 10, 18, 10), Background = new SolidColorBrush(Color.FromRgb(33, 150, 232)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        connect.Click += (_, _) => OpenRemoteDesktop();
        buttons.Children.Add(connect);
        var close = new Button { Content = "关闭", Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(10, 0, 0, 0) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        Content = root;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        _status.Text = isAdmin
            ? "状态：管理员权限已就绪。请按“打开本机远程桌面”进入或完成分身会话登录。"
            : "状态：需要管理员权限才能创建 Windows Child Session。请以管理员身份重新启动 Better-Muv。";
    }

    private void OpenRemoteDesktop()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "mstsc.exe", Arguments = "/v:localhost", UseShellExecute = true });
            _status.Text = "已打开 Windows 远程桌面。完成本机账户登录后，在分身中启动 Better-Muv。";
        }
        catch (Exception exception)
        {
            _status.Text = "无法启动远程桌面：" + exception.Message;
        }
    }
}
