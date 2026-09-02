using System.Windows;
using System.Windows.Threading;

namespace BetterMuv;

public partial class FirstRunNoticeWindow : Window
{
    private const int LockSeconds = 10;
    private readonly DispatcherTimer _timer;
    private int _remaining = LockSeconds;
    private bool _canClose;

    public FirstRunNoticeWindow()
    {
        InitializeComponent();
        MessageText.Text =
            "该工具为免费工具！！！！！\n" +
            "该工具为免费工具！！！！！\n" +
            "该工具为免费工具！！！！！\n" +
            "目前该工具仅适配200级以下迷宫，且存在诸多bug。可在Github上或群294674780进行反馈";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        Loaded += (_, _) => _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining > 0)
        {
            CloseButton.Content = $"我知道了（{_remaining}）";
            return;
        }

        _timer.Stop();
        _canClose = true;
        CloseButton.IsEnabled = true;
        CloseButton.Content = "我知道了";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_canClose)
            return;
        DialogResult = true;
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_canClose)
            e.Cancel = true;
    }
}
