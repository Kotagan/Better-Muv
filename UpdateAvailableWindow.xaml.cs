using System.Windows;
using System.Windows.Input;
using BetterMuv.Services;

namespace BetterMuv;

public partial class UpdateAvailableWindow : Window
{
    public enum Choice
    {
        Later,
        IgnoreMajor,
        Updated
    }

    public Choice ResultChoice { get; private set; } = Choice.Later;

    private readonly AppUpdateService _updater;
    private readonly GitHubRelease _release;
    private CancellationTokenSource? _downloadCts;
    private bool _busy;

    public UpdateAvailableWindow(AppVersion current, GitHubRelease release, AppUpdateService? updater = null)
    {
        InitializeComponent();
        _updater = updater ?? new AppUpdateService();
        _release = release;
        VersionSummaryText.Text = $"当前 {current}  →  最新 {release.Version}（{release.TagName}）";
        NotesText.Text = string.IsNullOrWhiteSpace(release.Body)
            ? "（无更新说明）"
            : release.Body.Trim();
        if (release.SetupAsset is null)
        {
            UpdateButton.Content = "打开发布页";
            UpdateButton.ToolTip = "该 Release 没有安装包，将打开 GitHub 页面。";
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && !_busy)
            DragMove();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        ResultChoice = Choice.Later;
        DialogResult = false;
        Close();
    }

    private void IgnoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        AppUpdateService.IgnoreMajorLine(_release.Version);
        ResultChoice = Choice.IgnoreMajor;
        DialogResult = true;
        Close();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_release.SetupAsset is null)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _release.HtmlUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowError("无法打开发布页：" + ex.Message);
            }

            ResultChoice = Choice.Later;
            DialogResult = false;
            Close();
            return;
        }

        _busy = true;
        SetBusyUi(true);
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress.Value = p;
                ProgressText.Text = p >= 1 ? "下载完成，正在启动安装程序…" : $"正在下载… {(int)(p * 100)}%";
            });
            string path = await _updater.DownloadSetupAsync(_release, progress, _downloadCts.Token);
            ResultChoice = Choice.Updated;
            DialogResult = true;
            AppUpdateService.LaunchInstallerAndExit(path);
        }
        catch (OperationCanceledException)
        {
            ShowError("下载已取消。");
            _busy = false;
            SetBusyUi(false);
        }
        catch (Exception ex)
        {
            ShowError("更新失败：" + ex.Message);
            _busy = false;
            SetBusyUi(false);
        }
    }

    private void SetBusyUi(bool busy)
    {
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.IsEnabled = !busy;
        IgnoreButton.IsEnabled = !busy;
        LaterButton.IsEnabled = !busy;
        if (busy)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            DownloadProgress.Value = 0;
            ProgressText.Text = "正在下载…";
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        base.OnClosed(e);
    }
}
