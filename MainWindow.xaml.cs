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
    private bool _loadingRunWindows;
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
    private const string FluentPlay = "\uE768";
    private const string FluentPause = "\uE769";
    private const string FluentStop = "\uE71A";
    private enum ActiveTask { None, Maze, MainQuest, HardMainQuest, Pipeline }
    private ActiveTask _activeTask = ActiveTask.None;
    private readonly List<string> _pipelineQueue = [];
    private int _pipelineIndex;
    private bool _suppressPipelineToggle;
    private Point _taskDragStart;
    private Border? _taskDragSource;
    private bool _taskDragPending;

    public MainWindow()
    {
        InitializeComponent();
        _captureSession = new GameCaptureSession(AppendLog);
        InitializeShopPanel();
        LoadSettings();
        LoadRunWindows();
        SetPage(Page.Home);
        AppendLog("配置文件：" + _configPath);
        AppendLog("等待启动截图器。");
        ContentRendered += MainWindow_ContentRendered;
    }

    private async void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        if (!string.Equals(Environment.GetEnvironmentVariable("BETTER_MUV_AUTO_MAIN_QUEST"), "1", StringComparison.Ordinal))
            return;
        AppendLog("调试模式：自动启动主线任务。");
        SetPage(Page.Execute);
        try
        {
            if (!_captureSession.IsRunning)
                await _captureSession.StartAsync(ConfigStore.Load(), CancellationToken.None);
            UpdateCaptureUi();
            await StartMainQuestAsync();
        }
        catch (Exception ex)
        {
            AppendLog("调试自动启动失败：" + ex.Message);
        }
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

    private void RunWindowComboBox_DropDownOpened(object sender, EventArgs e) => LoadRunWindows();

    private void LoadRunWindows()
    {
        AutomationConfig config = ConfigStore.Load();
        var capture = new WindowCaptureService();
        List<WindowCandidate> candidates = capture.EnumerateWindows();
        _loadingRunWindows = true;
        try
        {
            RunWindowComboBox.Items.Clear();
            RunWindowComboBox.Items.Add(new ComboBoxItem { Content = "游戏窗口（默认）", Tag = null });
            foreach (WindowCandidate candidate in candidates)
                RunWindowComboBox.Items.Add(new ComboBoxItem { Content = candidate.DisplayName, Tag = candidate });

            int selectedIndex = 0;
            if (config.WindowSelectionMode.Equals("selected", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < RunWindowComboBox.Items.Count; i++)
                {
                    if (RunWindowComboBox.Items[i] is ComboBoxItem { Tag: WindowCandidate candidate } &&
                        candidate.ProcessName.Equals(config.SelectedWindowProcessName, StringComparison.OrdinalIgnoreCase) &&
                        candidate.ClassName.Equals(config.SelectedWindowClassName, StringComparison.Ordinal) &&
                        candidate.Title.Equals(config.SelectedWindowTitle, StringComparison.Ordinal))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
                if (selectedIndex == 0)
                {
                    RunWindowComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = $"已保存（当前未找到）— {config.SelectedWindowProcessName} — {config.SelectedWindowTitle}",
                        Tag = "saved"
                    });
                    selectedIndex = RunWindowComboBox.Items.Count - 1;
                }
            }
            RunWindowComboBox.SelectedIndex = selectedIndex;
        }
        finally { _loadingRunWindows = false; }
    }

    private void RunWindowComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingRunWindows || !IsLoaded || RunWindowComboBox.SelectedItem is not ComboBoxItem item)
            return;
        if (item.Tag is string) return;
        AutomationConfig config = ConfigStore.Load();
        if (item.Tag is WindowCandidate candidate)
        {
            config.WindowSelectionMode = "selected";
            config.SelectedWindowProcessName = candidate.ProcessName;
            config.SelectedWindowClassName = candidate.ClassName;
            config.SelectedWindowTitle = candidate.Title;
            AppendLog("已保存运行窗口：" + candidate.DisplayName);
        }
        else
        {
            config.WindowSelectionMode = "game";
            config.SelectedWindowProcessName = "";
            config.SelectedWindowClassName = "";
            config.SelectedWindowTitle = "";
            AppendLog("运行窗口已恢复为默认游戏窗口。");
        }
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

    private void ShowLogDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLogDrawerOpen)
            CollapseLogDrawer();
        else
            OpenLogDrawer();
    }

    private void CloseLogDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseLogDrawer();
    }

    private void ExportLogButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出运行日志",
            Filter = "文本文件 (*.txt)|*.txt",
            DefaultExt = ".txt",
            FileName = $"Better-Muv-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, LogBox.Text ?? "");
            AppendLog("已导出日志：" + dialog.FileName);
        }
        catch (Exception exception)
        {
            AppendLog("导出日志失败：" + exception.Message);
        }
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
        ApplyGamePath(dialog.FileName, quiet: false);
    }

    private void FindGameButton_Click(object sender, RoutedEventArgs e)
    {
        string? found = GamePathLocator.TryFind(GamePathBox.Text);
        if (found is null)
        {
            AppendLog("未找到游戏：请确认已安装，或手动浏览选择 muv_luv_girlsgardenx_cl.exe。");
            return;
        }

        ApplyGamePath(found, quiet: false);
    }

    private void ApplyGamePath(string path, bool quiet)
    {
        GamePathBox.Text = path;
        AutomationConfig config = ConfigStore.Load();
        config.GameExecutablePath = path;
        ConfigStore.Save(config);
        if (!quiet)
            AppendLog("已设置游戏路径：" + path);
    }

    private void EnsureGamePathResolved()
    {
        AutomationConfig config = ConfigStore.Load();
        if (GamePathLocator.IsValid(config.GameExecutablePath))
        {
            GamePathBox.Text = config.GameExecutablePath;
            return;
        }

        string? found = GamePathLocator.TryFind(config.GameExecutablePath);
        if (found is null)
        {
            GamePathBox.Text = config.GameExecutablePath;
            return;
        }

        ApplyGamePath(found, quiet: true);
        AppendLog("已自动找到游戏：" + found);
    }

    private void SaveGameLaunchSettings()
    {
        AutomationConfig config = ConfigStore.Load();
        config.GameExecutablePath = GamePathBox.Text.Trim();
        ConfigStore.Save(config);
    }

    private async Task<bool> EnsureCaptureForRunAsync(string statusTarget)
    {
        if (_captureSession.IsRunning)
            return true;
        if (statusTarget == "maze")
            MazeStatusText.Text = "正在启动截图器…";
        else if (statusTarget == "mainQuest")
            MainQuestStatusText.Text = "正在启动截图器…";
        else if (statusTarget == "hardMainQuest")
            HardMainQuestStatusText.Text = "正在启动截图器…";
        try
        {
            await _captureSession.StartAsync(ConfigStore.Load(), CancellationToken.None);
            UpdateCaptureUi();
            AppendLog("执行任务前已自动启动截图器。");
            return true;
        }
        catch (Exception exception)
        {
            string message = "截图器启动失败：" + exception.Message;
            if (statusTarget == "maze")
                MazeStatusText.Text = message;
            else if (statusTarget == "mainQuest")
                MainQuestStatusText.Text = message;
            else if (statusTarget == "hardMainQuest")
                HardMainQuestStatusText.Text = message;
            AppendLog("无法执行任务：" + message);
            UpdateRunUi();
            return false;
        }
    }

    private async void RunMazeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTask == ActiveTask.Maze && (_runCancellation is not null || _isPaused))
        {
            PauseResumeButton_Click(sender, e);
            return;
        }
        if (_runCancellation is not null || _isPaused)
            return;
        OpenLogDrawer();
        if (!await EnsureCaptureForRunAsync("maze"))
            return;
        await StartMazeAsync();
    }

    private async void RunMainQuestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTask == ActiveTask.MainQuest && (_runCancellation is not null || _isPaused))
        {
            PauseResumeButton_Click(sender, e);
            return;
        }
        if (_runCancellation is not null || _isPaused)
            return;
        OpenLogDrawer();
        if (!await EnsureCaptureForRunAsync("mainQuest"))
            return;
        await StartMainQuestAsync();
    }

    private async void RunHardMainQuestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTask == ActiveTask.HardMainQuest && (_runCancellation is not null || _isPaused))
        {
            PauseResumeButton_Click(sender, e);
            return;
        }
        if (_runCancellation is not null || _isPaused)
            return;
        OpenLogDrawer();
        if (!await EnsureCaptureForRunAsync("hardMainQuest"))
            return;
        await StartHardMainQuestAsync();
    }

    private async void RunPipelineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTask == ActiveTask.Pipeline && (_runCancellation is not null || _isPaused))
        {
            PauseResumeButton_Click(sender, e);
            return;
        }
        if (_runCancellation is not null || _isPaused)
            return;
        OpenLogDrawer();
        if (!await EnsureCaptureForRunAsync("pipeline"))
            return;
        await StartPipelineAsync();
    }

    private async Task StartMazeAsync()
    {
        if (_runCancellation is not null) return;
        PersistMazeSettings(quiet: true);
        _isPaused = false;
        _pauseRequested = false;
        _activeTask = ActiveTask.Maze;
        _runCancellation = new CancellationTokenSource();
        UpdateRunUi();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(250, _runCancellation.Token);
            await RunMazeCoreAsync(_runCancellation.Token);
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
            FinishRunSession();
        }
    }

    private async Task StartMainQuestAsync()
    {
        if (_runCancellation is not null) return;
        _isPaused = false;
        _pauseRequested = false;
        _activeTask = ActiveTask.MainQuest;
        _runCancellation = new CancellationTokenSource();
        UpdateRunUi();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(250, _runCancellation.Token);
            await RunMainQuestCoreAsync(_runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog(_pauseRequested ? "自动主线已暂停。" : "自动主线已停止。");
        }
        catch (Exception exception)
        {
            AppendLog("自动主线错误：" + exception.Message);
        }
        finally
        {
            FinishRunSession();
        }
    }

    private async Task StartHardMainQuestAsync()
    {
        if (_runCancellation is not null) return;
        _isPaused = false;
        _pauseRequested = false;
        _activeTask = ActiveTask.HardMainQuest;
        _runCancellation = new CancellationTokenSource();
        UpdateRunUi();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(250, _runCancellation.Token);
            await RunHardMainQuestCoreAsync(_runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog(_pauseRequested ? "困难主线已暂停。" : "困难主线已停止。");
        }
        catch (Exception exception)
        {
            AppendLog("困难主线错误：" + exception.Message);
        }
        finally
        {
            FinishRunSession();
        }
    }

    private async Task StartPipelineAsync()
    {
        if (_runCancellation is not null) return;
        PersistMazeSettings(quiet: true);
        bool resume = _isPaused && _activeTask == ActiveTask.Pipeline;
        _isPaused = false;
        _pauseRequested = false;
        if (!resume)
        {
            AutomationConfig config = ConfigStore.Load();
            _pipelineQueue.Clear();
            foreach (string id in AutomationConfig.NormalizePipelineTaskOrder(config.PipelineTaskOrder))
            {
                if (id == "maze" && config.MazeTaskEnabled)
                    _pipelineQueue.Add(id);
                else if (id == "mainQuest" && config.MainQuestTaskEnabled)
                    _pipelineQueue.Add(id);
                else if (id == "hardMainQuest" && config.HardMainQuestTaskEnabled)
                    _pipelineQueue.Add(id);
            }

            _pipelineIndex = 0;
            if (_pipelineQueue.Contains("maze") && config.MazeRunLimit == 0)
                AppendLog("一条龙包含迷宫且次数为 0（无限），后续任务会等迷宫结束后才会开始。");
            if (_pipelineQueue.Count == 0)
            {
                AppendLog("一条龙未启用任何任务，已取消。");
                _activeTask = ActiveTask.None;
                UpdateRunUi();
                return;
            }
        }

        _activeTask = ActiveTask.Pipeline;
        _runCancellation = new CancellationTokenSource();
        UpdateRunUi();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(250, _runCancellation.Token);
            while (_pipelineIndex < _pipelineQueue.Count)
            {
                string id = _pipelineQueue[_pipelineIndex];
                AppendLog($"一条龙：开始 {PipelineTaskDisplayName(id)}（{_pipelineIndex + 1}/{_pipelineQueue.Count}）。");
                if (id == "maze")
                    await RunMazeCoreAsync(_runCancellation.Token);
                else if (id == "hardMainQuest")
                    await RunHardMainQuestCoreAsync(_runCancellation.Token);
                else
                    await RunMainQuestCoreAsync(_runCancellation.Token);
                _pipelineIndex++;
            }

            AppendLog("一条龙：已按顺序跑完启用的任务。");
        }
        catch (OperationCanceledException)
        {
            AppendLog(_pauseRequested ? "一条龙已暂停。" : "一条龙已停止。");
        }
        catch (Exception exception)
        {
            AppendLog("一条龙错误：" + exception.Message);
        }
        finally
        {
            FinishRunSession();
        }
    }

    private Task RunMazeCoreAsync(CancellationToken cancellationToken)
    {
        var automation = new MazeAutomation(ConfigStore.Load(), AppendLog);
        return automation.RunOnceAsync(cancellationToken);
    }

    private Task RunMainQuestCoreAsync(CancellationToken cancellationToken)
    {
        var automation = new MainQuestAutomation(ConfigStore.Load(), AppendLog);
        return automation.RunOnceAsync(cancellationToken);
    }

    private Task RunHardMainQuestCoreAsync(CancellationToken cancellationToken)
    {
        var automation = new HardMainQuestAutomation(ConfigStore.Load(), AppendLog);
        return automation.RunOnceAsync(cancellationToken);
    }

    private void FinishRunSession()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        _runCancellation?.Dispose();
        _runCancellation = null;
        // 仅用户点「暂停」时保留可继续；停止键 / 任务自行结束一律清空。
        bool keepPaused = _pauseRequested;
        _isPaused = keepPaused;
        _pauseRequested = false;
        if (!_isPaused)
        {
            _activeTask = ActiveTask.None;
            _pipelineQueue.Clear();
            _pipelineIndex = 0;
        }

        UpdateRunUi();
    }

    private static string PipelineTaskDisplayName(string id) => id switch
    {
        "maze" => "迷宫探索",
        "hardMainQuest" => "自动困难主线",
        _ => "自动主线任务"
    };

    private void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPaused)
        {
            if (_activeTask == ActiveTask.Pipeline)
                _ = StartPipelineAsync();
            else if (_activeTask == ActiveTask.MainQuest)
                _ = StartMainQuestAsync();
            else if (_activeTask == ActiveTask.HardMainQuest)
                _ = StartHardMainQuestAsync();
            else
                _ = StartMazeAsync();
            return;
        }

        if (_runCancellation is not null)
        {
            _pauseRequested = true;
            RunMazeButton.IsEnabled = false;
            RunMainQuestButton.IsEnabled = false;
            RunHardMainQuestButton.IsEnabled = false;
            RunPipelineButton.IsEnabled = false;
            _runCancellation.Cancel();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _pauseRequested = false;
        _isPaused = false;
        _activeTask = ActiveTask.None;
        _pipelineQueue.Clear();
        _pipelineIndex = 0;
        StopButton.IsEnabled = false;
        StopMainQuestButton.IsEnabled = false;
        StopHardMainQuestButton.IsEnabled = false;
        StopPipelineButton.IsEnabled = false;
        _runCancellation?.Cancel();
        if (_runCancellation is null) UpdateRunUi();
    }

    private void UpdateRunUi()
    {
        bool running = _runCancellation is not null;
        bool showControls = running || _isPaused;
        bool mazeUi = _activeTask == ActiveTask.Maze;
        bool mainQuestUi = _activeTask == ActiveTask.MainQuest;
        bool hardMainQuestUi = _activeTask == ActiveTask.HardMainQuest;
        bool pipelineUi = _activeTask == ActiveTask.Pipeline;
        bool idle = !running && !_isPaused;

        RunMazeButton.IsEnabled = idle || mazeUi;
        RunMainQuestButton.IsEnabled = idle || mainQuestUi;
        RunHardMainQuestButton.IsEnabled = idle || hardMainQuestUi;
        RunPipelineButton.IsEnabled = idle || pipelineUi;
        MazePipelineToggle.IsEnabled = idle;
        MainQuestPipelineToggle.IsEnabled = idle;
        HardMainQuestPipelineToggle.IsEnabled = idle;

        // 独立暂停键隐藏；运行键兼任暂停/继续，旁边保留停止键。
        PauseResumeButton.Visibility = Visibility.Collapsed;
        PauseMainQuestButton.Visibility = Visibility.Collapsed;
        PauseHardMainQuestButton.Visibility = Visibility.Collapsed;
        PausePipelineButton.Visibility = Visibility.Collapsed;

        StopButton.Visibility = showControls && mazeUi ? Visibility.Visible : Visibility.Collapsed;
        StopMainQuestButton.Visibility = showControls && mainQuestUi ? Visibility.Visible : Visibility.Collapsed;
        StopHardMainQuestButton.Visibility = showControls && hardMainQuestUi ? Visibility.Visible : Visibility.Collapsed;
        StopPipelineButton.Visibility = showControls && pipelineUi ? Visibility.Visible : Visibility.Collapsed;
        RunPipelineButton.Visibility = Visibility.Visible;

        // 运行中：暂停 + 停止；暂停中：继续(播放) + 停止。
        string runGlyph = !showControls ? FluentPlay : _isPaused ? FluentPlay : FluentPause;
        RunMazeButton.Content = mazeUi && showControls ? runGlyph : FluentPlay;
        RunMainQuestButton.Content = mainQuestUi && showControls ? runGlyph : FluentPlay;
        RunHardMainQuestButton.Content = hardMainQuestUi && showControls ? runGlyph : FluentPlay;
        RunMazeButton.ToolTip = mazeUi && running ? "暂停" : mazeUi && _isPaused ? "继续" : "运行";
        RunMainQuestButton.ToolTip = mainQuestUi && running ? "暂停" : mainQuestUi && _isPaused ? "继续" : "运行";
        RunHardMainQuestButton.ToolTip = hardMainQuestUi && running ? "暂停" : hardMainQuestUi && _isPaused ? "继续" : "运行";
        RunPipelineButton.Content = pipelineUi && running ? "暂停"
            : pipelineUi && _isPaused ? "继续"
            : "运行";

        StopButton.Content = FluentStop;
        StopMainQuestButton.Content = FluentStop;
        StopHardMainQuestButton.Content = FluentStop;
        StopPipelineButton.Content = FluentStop;
        StopButton.ToolTip = "停止";
        StopMainQuestButton.ToolTip = "停止";
        StopHardMainQuestButton.ToolTip = "停止";
        StopPipelineButton.ToolTip = "停止";
        StopButton.IsEnabled = true;
        StopMainQuestButton.IsEnabled = true;
        StopHardMainQuestButton.IsEnabled = true;
        StopPipelineButton.IsEnabled = true;

        MazeStatusText.Text = mazeUi && running ? "迷宫探索运行中。"
            : mazeUi && _isPaused ? "迷宫探索已暂停。"
            : _captureSession.IsRunning ? "截图器已就绪。" : "等待截图器启动。";
        MainQuestStatusText.Text = mainQuestUi && running ? "自动主线运行中。"
            : mainQuestUi && _isPaused ? "自动主线已暂停。"
            : "";
        HardMainQuestStatusText.Text = hardMainQuestUi && running ? "困难主线运行中。"
            : hardMainQuestUi && _isPaused ? "困难主线已暂停。"
            : "";
    }

    private void LoadSettings()
    {
        AutomationConfig config = ConfigStore.Load();
        LaunchGameCheckBox.IsChecked = config.LaunchGameWithCapture;
        EnsureGamePathResolved();
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
        _suppressPipelineToggle = true;
        MazePipelineToggle.IsChecked = config.MazeTaskEnabled;
        MainQuestPipelineToggle.IsChecked = config.MainQuestTaskEnabled;
        HardMainQuestPipelineToggle.IsChecked = config.HardMainQuestTaskEnabled;
        _suppressPipelineToggle = false;
        ApplyTaskListOrder(config.PipelineTaskOrder);
        UpdateCaptureUi();
        UpdateRunUi();
    }

    private void PipelineTaskToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressPipelineToggle) return;
        AutomationConfig config = ConfigStore.Load();
        config.MazeTaskEnabled = MazePipelineToggle.IsChecked == true;
        config.MainQuestTaskEnabled = MainQuestPipelineToggle.IsChecked == true;
        config.HardMainQuestTaskEnabled = HardMainQuestPipelineToggle.IsChecked == true;
        ConfigStore.Save(config);
    }

    private void TaskCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _taskDragPending = false;
        _taskDragSource = null;
        if (_runCancellation is not null || _isPaused)
            return;
        if (e.OriginalSource is DependencyObject origin &&
            (FindAncestor<Button>(origin) is not null || FindAncestor<CheckBox>(origin) is not null))
            return;
        if (sender is not Border card)
            return;
        _taskDragSource = card;
        _taskDragStart = e.GetPosition(this);
        _taskDragPending = true;
    }

    private void TaskCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _taskDragPending = false;
        _taskDragSource = null;
    }

    private void TaskCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_taskDragPending || _taskDragSource is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (_runCancellation is not null || _isPaused)
            return;
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _taskDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _taskDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _taskDragPending = false;
        string id = _taskDragSource.Tag as string ?? "";
        Border dragged = _taskDragSource;
        dragged.Opacity = 0.55;
        try
        {
            DragDrop.DoDragDrop(dragged, id, DragDropEffects.Move);
        }
        finally
        {
            dragged.Opacity = 1;
            ClearTaskDropHighlight();
            _taskDragSource = null;
        }
    }

    private void TaskList_DragOver(object sender, DragEventArgs e)
    {
        if (!CanReorderTasks(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        UpdateTaskDropHighlight(GetTaskDropIndex(e));
    }

    private void TaskList_DragLeave(object sender, DragEventArgs e)
    {
        Point p = e.GetPosition(TaskListPanel);
        if (p.X < 0 || p.Y < 0 || p.X > TaskListPanel.ActualWidth || p.Y > TaskListPanel.ActualHeight)
            ClearTaskDropHighlight();
    }

    private void TaskList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearTaskDropHighlight();
        if (!CanReorderTasks(e))
            return;
        string id = (e.Data.GetData(typeof(string)) as string) ?? "";
        UIElement? source = FindTaskCard(id);
        if (source is null)
            return;
        int from = TaskListPanel.Children.IndexOf(source);
        int to = GetTaskDropIndex(e);
        if (from < 0)
            return;
        if (from < to)
            to--;
        if (to == from || to < 0 || to >= TaskListPanel.Children.Count)
            return;
        TaskListPanel.Children.Remove(source);
        TaskListPanel.Children.Insert(to, source);
        PersistTaskListOrder();
    }

    private bool CanReorderTasks(DragEventArgs e) =>
        _runCancellation is null && !_isPaused && e.Data.GetDataPresent(typeof(string));

    private int GetTaskDropIndex(DragEventArgs e)
    {
        Point pos = e.GetPosition(TaskListPanel);
        for (int i = 0; i < TaskListPanel.Children.Count; i++)
        {
            if (TaskListPanel.Children[i] is not FrameworkElement child)
                continue;
            Point top = child.TranslatePoint(default, TaskListPanel);
            if (pos.Y < top.Y + child.ActualHeight / 2)
                return i;
        }
        return TaskListPanel.Children.Count;
    }

    private void UpdateTaskDropHighlight(int insertIndex)
    {
        ClearTaskDropHighlight();
        if (insertIndex < TaskListPanel.Children.Count && TaskListPanel.Children[insertIndex] is Border before)
        {
            before.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0xA6, 0xF1));
            before.BorderThickness = new Thickness(0, 2, 0, 0);
            return;
        }
        if (TaskListPanel.Children.Count > 0 && TaskListPanel.Children[^1] is Border last)
        {
            last.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0xA6, 0xF1));
            last.BorderThickness = new Thickness(0, 0, 0, 2);
        }
    }

    private void ClearTaskDropHighlight()
    {
        foreach (UIElement child in TaskListPanel.Children)
        {
            if (child is not Border card)
                continue;
            card.BorderBrush = Brushes.Transparent;
            card.BorderThickness = new Thickness(0);
        }
    }

    private UIElement? FindTaskCard(string id)
    {
        foreach (UIElement child in TaskListPanel.Children)
        {
            if (child is FrameworkElement el &&
                string.Equals(el.Tag as string, id, StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void PersistTaskListOrder()
    {
        AutomationConfig config = ConfigStore.Load();
        config.PipelineTaskOrder = TaskListPanel.Children
            .OfType<FrameworkElement>()
            .Select(card => card.Tag as string)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
        ConfigStore.Save(config);
    }

    private void ApplyTaskListOrder(IEnumerable<string> order)
    {
        Dictionary<string, UIElement> cards = new(StringComparer.OrdinalIgnoreCase)
        {
            ["maze"] = MazeTaskCard,
            ["mainQuest"] = MainQuestTaskCard,
            ["hardMainQuest"] = HardMainQuestTaskCard
        };
        TaskListPanel.Children.Clear();
        foreach (string id in AutomationConfig.NormalizePipelineTaskOrder(order))
        {
            if (cards.TryGetValue(id, out UIElement? card))
                TaskListPanel.Children.Add(card);
        }
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
    private void MovePriorityTop_Click(object sender, RoutedEventArgs e) => MovePriorityToTop();
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

    private void MovePriorityToTop()
    {
        int from = TreasurePriorityList.SelectedIndex;
        if (from <= 0) return;
        object item = TreasurePriorityList.Items[from];
        TreasurePriorityList.Items.RemoveAt(from);
        TreasurePriorityList.Items.Insert(0, item);
        TreasurePriorityList.SelectedIndex = 0;
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
            screen.EnsureUsableViewport(window);
            ScreenRect viewport = screen.Viewport(window);
            BitmapSource image = screen.Capture.Capture(window, viewport);
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
