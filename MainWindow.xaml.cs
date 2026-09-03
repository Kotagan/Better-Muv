using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterMuv.Core;
using BetterMuv.Services;
using Microsoft.Win32;

namespace BetterMuv;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, string> TreasureNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["diamond"] = "钻石", ["skull"] = "骷髅", ["sparkle"] = "闪光",
            ["shield"] = "盾", ["sword"] = "剑", ["heart"] = "心"
        };

    private readonly GameCaptureSession _captureSession;
    private readonly string _configPath = ConfigStore.EnsureUserConfigPath();
    private readonly object _logFileLock = new();
    private CancellationTokenSource? _runCancellation;
    private bool _pauseRequested;
    private bool _isPaused;
    private bool _isLogDrawerOpen;
    private double? _widthBeforeLogDrawer;
    private const double LogDrawerWidth = 360;

    public MainWindow()
    {
        InitializeComponent();
        _captureSession = new GameCaptureSession(AppendLog);
        InitializeShopPanel();
        LoadSettings();
        SetPage(Page.Home);
        AppendLog("配置文件：" + _configPath);
        AppendLog("等待启动截图器。");
    }

    private enum Page { Home, Execute, Settings, MazeSettings }
    private enum MazeSettingsSection { Basic, Treasure, Shop }
    private enum SettingsSection { General, Hotkey }

    private void SetPage(Page page)
    {
        HomePanel.Visibility = page == Page.Home ? Visibility.Visible : Visibility.Collapsed;
        ExecutionPanel.Visibility = page == Page.Execute ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
        MazeSettingsPanel.Visibility = page == Page.MazeSettings ? Visibility.Visible : Visibility.Collapsed;
        MazeSettingsBackButton.Visibility = page == Page.MazeSettings ? Visibility.Visible : Visibility.Collapsed;
        if (page != Page.Execute && _isLogDrawerOpen)
            CollapseLogDrawer();
        else
            LogDrawer.Visibility = page == Page.Execute && _isLogDrawerOpen ? Visibility.Visible : Visibility.Collapsed;
        HomeNavButton.Background = page == Page.Home ? new SolidColorBrush(Color.FromRgb(43, 50, 61)) : Brushes.Transparent;
        ExecuteNavButton.Background = page is Page.Execute or Page.MazeSettings ? new SolidColorBrush(Color.FromRgb(43, 50, 61)) : Brushes.Transparent;
        SettingsNavButton.Background = page == Page.Settings ? new SolidColorBrush(Color.FromRgb(43, 50, 61)) : Brushes.Transparent;
    }

    private void ShowHomeButton_Click(object sender, RoutedEventArgs e) => SetPage(Page.Home);
    private void ShowExecuteButton_Click(object sender, RoutedEventArgs e) => SetPage(Page.Execute);
    private void ShowSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        SetPage(Page.Settings);
        ShowSettingsSection(SettingsSection.General);
    }

    private void ShowSettingsGeneralButton_Click(object sender, RoutedEventArgs e) => ShowSettingsSection(SettingsSection.General);
    private void ShowSettingsHotkeyButton_Click(object sender, RoutedEventArgs e) => ShowSettingsSection(SettingsSection.Hotkey);

    private void ShowSettingsSection(SettingsSection section)
    {
        SettingsGeneralSection.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        SettingsHotkeySection.Visibility = section == SettingsSection.Hotkey ? Visibility.Visible : Visibility.Collapsed;
        var active = new SolidColorBrush(Color.FromRgb(59, 66, 78));
        SettingsGeneralNavButton.Background = section == SettingsSection.General ? active : Brushes.Transparent;
        SettingsHotkeyNavButton.Background = section == SettingsSection.Hotkey ? active : Brushes.Transparent;
    }

    private async void CaptureStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_captureSession.IsRunning)
        {
            _captureSession.Stop();
            UpdateCaptureUi();
            return;
        }

        CaptureStartButton.IsEnabled = false;
        try
        {
            await _captureSession.StartAsync(ConfigStore.Load(), CancellationToken.None);
            UpdateCaptureUi();
        }
        catch (Exception exception)
        {
            CaptureStatusText.Text = "启动失败：" + exception.Message;
            AppendLog("截图器启动失败：" + exception.Message);
        }
        finally
        {
            CaptureStartButton.IsEnabled = true;
        }
    }

    private void UpdateCaptureUi()
    {
        bool running = _captureSession.IsRunning;
        CaptureStartButton.Content = running ? "■  停止" : "▷  启动";
        CaptureStartButton.Background = new SolidColorBrush(Color.FromRgb(59, 66, 78));
        CaptureStatusText.Text = running ? "已启动，迷宫探索可执行。" : "未启动。启动后才能执行迷宫探索。";
        MazeStatusText.Text = running ? "截图器已就绪。" : "等待截图器启动。";
    }

    private void LaunchGameCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AutomationConfig config = ConfigStore.Load();
        config.LaunchGameWithCapture = LaunchGameCheckBox.IsChecked == true;
        ConfigStore.Save(config);
    }

    private void GameSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SetPage(Page.Settings);
        ShowSettingsSection(SettingsSection.General);
        GameSettingsCard.BringIntoView();
    }

    private void MazeSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        SetPage(Page.MazeSettings);
        ShowMazeSettingsSection(MazeSettingsSection.Basic);
    }

    private void ShowMazeBasicSettingsButton_Click(object sender, RoutedEventArgs e) => ShowMazeSettingsSection(MazeSettingsSection.Basic);
    private void ShowMazeTreasureSettingsButton_Click(object sender, RoutedEventArgs e) => ShowMazeSettingsSection(MazeSettingsSection.Treasure);
    private void ShowMazeShopSettingsButton_Click(object sender, RoutedEventArgs e) => ShowMazeSettingsSection(MazeSettingsSection.Shop);

    private void ShowMazeSettingsSection(MazeSettingsSection section)
    {
        MazeBasicSettingsSection.Visibility = section == MazeSettingsSection.Basic ? Visibility.Visible : Visibility.Collapsed;
        MazeTreasureSettingsSection.Visibility = section == MazeSettingsSection.Treasure ? Visibility.Visible : Visibility.Collapsed;
        MazeShopSettingsSection.Visibility = section == MazeSettingsSection.Shop ? Visibility.Visible : Visibility.Collapsed;
        var active = new SolidColorBrush(Color.FromRgb(59, 66, 78));
        MazeBasicNavButton.Background = section == MazeSettingsSection.Basic ? active : Brushes.Transparent;
        MazeTreasureNavButton.Background = section == MazeSettingsSection.Treasure ? active : Brushes.Transparent;
        MazeShopNavButton.Background = section == MazeSettingsSection.Shop ? active : Brushes.Transparent;
    }

    private void ReturnToExecutionButton_Click(object sender, RoutedEventArgs e) => SetPage(Page.Execute);

    private void ShowLogDrawerButton_Click(object sender, RoutedEventArgs e) => OpenLogDrawer();

    private void CloseLogDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseLogDrawer();
    }

    private void OpenLogDrawer()
    {
        if (!_isLogDrawerOpen)
        {
            _widthBeforeLogDrawer = Width;
            if (WindowState == WindowState.Normal)
                Width += LogDrawerWidth;
        }
        _isLogDrawerOpen = true;
        LogDrawerColumn.Width = new GridLength(LogDrawerWidth);
        if (ExecutionPanel.Visibility == Visibility.Visible)
            LogDrawer.Visibility = Visibility.Visible;
    }

    private void CollapseLogDrawer()
    {
        _isLogDrawerOpen = false;
        LogDrawer.Visibility = Visibility.Collapsed;
        LogDrawerColumn.Width = new GridLength(0);
        if (_widthBeforeLogDrawer is double width && WindowState == WindowState.Normal)
            Width = Math.Max(MinWidth, width);
        _widthBeforeLogDrawer = null;
    }

    private void BrowseGameButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 MuvLuv Girls Garden 程序", Filter = "程序 (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        GamePathBox.Text = dialog.FileName;
        SaveGameLaunchSettings();
    }

    private void SaveGameLaunchSettings()
    {
        AutomationConfig config = ConfigStore.Load();
        config.GameExecutablePath = GamePathBox.Text.Trim();
        ConfigStore.Save(config);
    }

    private async void RunMazeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runCancellation is not null || _isPaused)
            return;

        OpenLogDrawer();

        if (!_captureSession.IsRunning)
        {
            MazeStatusText.Text = "正在启动截图器…";
            try
            {
                await _captureSession.StartAsync(ConfigStore.Load(), CancellationToken.None);
                UpdateCaptureUi();
                AppendLog("执行迷宫前已自动启动截图器。");
            }
            catch (Exception exception)
            {
                MazeStatusText.Text = "截图器启动失败。";
                AppendLog("无法执行迷宫：截图器启动失败：" + exception.Message);
                UpdateRunUi();
                return;
            }
        }
        await StartMazeAsync();
    }

    private async Task StartMazeAsync()
    {
        if (_runCancellation is not null) return;
        PersistMazeSettings(quiet: true);
        _isPaused = false;
        _pauseRequested = false;
        _runCancellation = new CancellationTokenSource();
        UpdateRunUi();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(250, _runCancellation.Token);
            var automation = new MazeAutomation(ConfigStore.Load(), AppendLog);
            await automation.RunOnceAsync(_runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog(_pauseRequested ? "迷宫已暂停。" : "迷宫已停止。");
        }
        catch (Exception exception)
        {
            AppendLog("迷宫错误：" + exception.Message);
        }
        finally
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            _runCancellation.Dispose();
            _runCancellation = null;
            _isPaused = _pauseRequested;
            _pauseRequested = false;
            UpdateRunUi();
        }
    }

    private void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPaused)
        {
            _ = StartMazeAsync();
            return;
        }
        if (_runCancellation is not null)
        {
            _pauseRequested = true;
            PauseResumeButton.IsEnabled = false;
            PauseResumeButton.Content = "正在暂停…";
            _runCancellation.Cancel();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _pauseRequested = false;
        _isPaused = false;
        StopButton.IsEnabled = false;
        _runCancellation?.Cancel();
        if (_runCancellation is null) UpdateRunUi();
    }

    private void UpdateRunUi()
    {
        bool running = _runCancellation is not null;
        RunMazeButton.Visibility = Visibility.Visible;
        RunMazeButton.IsEnabled = true;
        PauseResumeButton.Visibility = running || _isPaused ? Visibility.Visible : Visibility.Collapsed;
        PauseResumeButton.IsEnabled = true;
        PauseResumeButton.Content = _isPaused ? "▶  继续" : "Ⅱ  暂停";
        StopButton.Visibility = running || _isPaused ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = true;
        MazeStatusText.Text = running ? "迷宫探索运行中。" : _isPaused ? "迷宫探索已暂停。" : _captureSession.IsRunning ? "截图器已就绪。" : "等待截图器启动。";
    }

    private void LoadSettings()
    {
        AutomationConfig config = ConfigStore.Load();
        LaunchGameCheckBox.IsChecked = config.LaunchGameWithCapture;
        GamePathBox.Text = config.GameExecutablePath;
        MazeRunLimitBox.Text = config.MazeRunLimit.ToString();
        DifficultyKeepRadio.IsChecked = MazeDifficultyRunner.NormalizeMode(config.MazeDifficultyMode) != "custom";
        DifficultyCustomRadio.IsChecked = !DifficultyKeepRadio.IsChecked;
        DifficultyTargetBox.Text = config.MazeDifficultyTarget.ToString();
        DifficultyTargetBox.IsEnabled = DifficultyCustomRadio.IsChecked == true;
        TreasurePriorityList.Items.Clear();
        foreach (string key in config.TreasurePriority)
            TreasurePriorityList.Items.Add(TreasureNames.TryGetValue(key, out string? display) ? display : key);
        if (TreasurePriorityList.Items.Count > 0) TreasurePriorityList.SelectedIndex = 0;
        PauseHotkeyBox.Text = config.PauseHotkey;
        StopHotkeyBox.Text = config.StopHotkey;
        DiagnosticModeCheckBox.IsChecked = config.SaveDiagnostics;
        UpdateCaptureUi();
        UpdateRunUi();
    }

    private void MazeRunLimitBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => PersistMazeSettings();
    private void DifficultyMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DifficultyTargetBox.IsEnabled = DifficultyCustomRadio.IsChecked == true;
        PersistMazeSettings();
    }
    private void DifficultyTargetBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => PersistMazeSettings();

    private void PersistMazeSettings(bool quiet = false)
    {
        AutomationConfig config = ConfigStore.Load();
        if (!int.TryParse(MazeRunLimitBox.Text.Trim(), out int limit) || limit < 0) limit = config.MazeRunLimit;
        if (!int.TryParse(DifficultyTargetBox.Text.Trim(), out int target) || target is < 1 or > 999) target = config.MazeDifficultyTarget;
        config.MazeRunLimit = limit;
        config.MazeDifficultyMode = DifficultyCustomRadio.IsChecked == true ? "custom" : "keep";
        config.MazeDifficultyTarget = target;
        config.TreasurePriority = TreasurePriorityList.Items.Cast<string>()
            .Select(display => TreasureNames.FirstOrDefault(item => item.Value == display).Key ?? display).ToList();
        ConfigStore.Save(config);
        if (!quiet) AppendLog("迷宫设置已保存。");
    }

    private void MovePriorityUp_Click(object sender, RoutedEventArgs e) => MovePriority(-1);
    private void MovePriorityDown_Click(object sender, RoutedEventArgs e) => MovePriority(1);
    private void MovePriority(int offset)
    {
        int from = TreasurePriorityList.SelectedIndex, to = from + offset;
        if (from < 0 || to < 0 || to >= TreasurePriorityList.Items.Count) return;
        object item = TreasurePriorityList.Items[from];
        TreasurePriorityList.Items.RemoveAt(from);
        TreasurePriorityList.Items.Insert(to, item);
        TreasurePriorityList.SelectedIndex = to;
        PersistMazeSettings();
    }

    private void DiagnosticModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AutomationConfig config = ConfigStore.Load();
        config.SaveDiagnostics = DiagnosticModeCheckBox.IsChecked == true;
        ConfigStore.Save(config);
    }

    private void HotkeyBoxes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        string pause = PauseHotkeyBox.Text.Trim().ToUpperInvariant();
        string stop = StopHotkeyBox.Text.Trim().ToUpperInvariant();
        if (!AutomationConfig.IsSupportedFunctionKey(pause) || !AutomationConfig.IsSupportedFunctionKey(stop) || pause == stop)
        {
            AppendLog("快捷键须为不同的 F1–F12。已恢复原设置。");
            LoadSettings();
            return;
        }
        AutomationConfig config = ConfigStore.Load();
        config.PauseHotkey = pause;
        config.StopHotkey = stop;
        ConfigStore.Save(config);
        RegisterConfiguredHotkeys();
    }

    private void SelectLocatorTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择要定位的模板图片", Filter = "图像文件|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(this) == true) LocatorTemplatePathBox.Text = dialog.FileName;
    }

    private async void LocateTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(LocatorTemplatePathBox.Text)) { LocatorResultText.Text = "请先选择有效的模板图片。"; return; }
        try
        {
            using var stream = File.OpenRead(LocatorTemplatePathBox.Text);
            BitmapSource template = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            template.Freeze();
            AutomationConfig config = ConfigStore.Load();
            var screen = new ScreenAutomation(config, AppendLog);
            GameWindow window = screen.FindWindow(config.WindowTitleKeyword);
            screen.EnsureSixteenByNine(window);
            BitmapSource image = screen.Capture.Capture(window, window.DisplayRect);
            CaptureGeometry geometry = screen.Geometry(window);
            int width = Math.Max(1, (int)Math.Round(template.PixelWidth / geometry.ScaleX));
            int height = Math.Max(1, (int)Math.Round(template.PixelHeight / geometry.ScaleY));
            ImageLocationResult match = await Task.Run(() => new ImageLocator().Locate(image, template, width, height));
            const int pad = 12;
            int left = Math.Max(0, match.X - pad), top = Math.Max(0, match.Y - pad);
            int right = Math.Min(1920, match.X + match.Width + pad), bottom = Math.Min(1080, match.Y + match.Height + pad);
            LocatorResultText.Text = $"分数 {match.Score:F4}；推荐 TopLeft: ({left}, {top})，Size: ({right - left}, {bottom - top})。";
        }
        catch (Exception exception) { LocatorResultText.Text = "定位失败：" + exception.Message; }
    }

    private void OpenCloneButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new DesktopCloneWindow { Owner = this };
        window.Show();
    }

    private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border frame)
            return;
        frame.Clip = new RectangleGeometry(
            new Rect(0, 0, frame.ActualWidth, frame.ActualHeight),
            frame.CornerRadius.TopLeft,
            frame.CornerRadius.TopLeft);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        PersistMazeSettings(quiet: true);
        PersistShopPurchases(quiet: true);
        _runCancellation?.Cancel();
        if (_captureSession.IsRunning) _captureSession.Stop();
        ReleaseHotkeys();
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try { lock (_logFileLock) File.AppendAllText(ConfigStore.EnsureLogFilePath(), line + Environment.NewLine); } catch { }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLogToUi(line));
            return;
        }
        AppendLogToUi(line);
    }

    private void AppendLogToUi(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
