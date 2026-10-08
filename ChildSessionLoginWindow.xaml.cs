using System.Windows;
using System.Windows.Input;
using BetterMuv.Services;
using BetterMuv.Services.ChildSession;

namespace BetterMuv;

public partial class ChildSessionLoginWindow : Window
{
    public ChildSessionLoginCredentials? Credentials { get; private set; }

    private bool _setPasswordMode;

    public ChildSessionLoginWindow()
    {
        InitializeComponent();
        UserNameBox.Text = Environment.UserName;
        if (ChildSessionCredentialHints.IsBlankPasswordRemoteBlocked())
        {
            HintText.Text = "本机禁止空密码远程登录。若你平时只用 PIN、从未设过账户密码，"
                + "请点上方蓝色文字设置一个新密码。";
        }

        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void ToggleSetPassword_Click(object sender, RoutedEventArgs e)
    {
        _setPasswordMode = !_setPasswordMode;
        SetPasswordPanel.Visibility = _setPasswordMode ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.IsEnabled = !_setPasswordMode;
        PasswordBox.Opacity = _setPasswordMode ? 0.45 : 1;
        ConnectButton.Content = _setPasswordMode ? "设置密码并连接" : "连接";
        ToggleSetPasswordButton.Content = _setPasswordMode
            ? "我知道密码，返回填写"
            : "没有密码 / 只用 PIN？点此设置新密码";

        if (_setPasswordMode)
            NewPasswordBox.Focus();
        else
            PasswordBox.Focus();
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_setPasswordMode)
            TryAccept();
    }

    private void ConfirmPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _setPasswordMode)
            TryAccept();
    }

    private void TryAccept()
    {
        string userName = Environment.UserName;
        string password;

        if (_setPasswordMode)
        {
            password = NewPasswordBox.Password;
            string confirm = ConfirmPasswordBox.Password;
            if (string.IsNullOrEmpty(password) || password.Length < 4)
            {
                MessageBox.Show(this, "请设置至少 4 位的新密码。", "设置账户密码",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.Equals(password, confirm, StringComparison.Ordinal))
            {
                MessageBox.Show(this, "两次输入的新密码不一致。", "设置账户密码",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBoxResult confirmSet = MessageBox.Show(
                this,
                $"将把当前 Windows 账户「{userName}」的登录密码改为你刚输入的新密码。\n\n"
                + "之后开机仍可用 PIN（若已设置）；进入桌面分身请用这个新密码。\n\n"
                + "是否继续？",
                "确认修改 Windows 密码",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmSet != MessageBoxResult.Yes)
                return;

            if (!ElevationHelper.IsElevated())
            {
                MessageBoxResult elevate = MessageBox.Show(
                    this,
                    "设置 Windows 账户密码需要管理员权限。\n是否以管理员权限重启 Better-Muv？",
                    "需要管理员权限",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (elevate == MessageBoxResult.Yes)
                    ElevationHelper.TryRestartElevated(AppInstance.OpenChildSessionArgument);
                return;
            }

            try
            {
                WindowsLocalAccountPassword.SetPassword(userName, password);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.GetBaseException().Message, "设置账户密码失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        else
        {
            password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show(
                    this,
                    "请输入账户密码。\n若不知道或从未设过，请点「没有密码 / 只用 PIN？」设置新密码。",
                    "桌面分身登录",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
        }

        // 本机账户用计算机名作域；"." 在部分 Child Session 场景会卡住/报内部错误。
        string domain = Environment.UserDomainName;
        if (string.IsNullOrWhiteSpace(domain))
            domain = Environment.MachineName;

        Credentials = new ChildSessionLoginCredentials(userName, domain, password);
        DialogResult = true;
        Close();
    }
}
