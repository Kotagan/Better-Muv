using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
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
            ["shield"] = "盾", ["sword"] = "剑", ["heart"] = "心", ["shoe"] = "鞋子"
        };

    private readonly GameCaptureSession _captureSession;
    private readonly DiagnosticTaskSession _diagnosticSession = new();
    private bool _resumeDiagnosticTask;
    private readonly string _configPath = ConfigStore.EnsureUserConfigPath();
    private readonly object _logFileLock = new();
    private string _sessionLogPath = ConfigStore.EnsureLogFilePath();
    private CancellationTokenSource? _runCancellation;
    private bool _pauseRequested;
    private bool _isPaused;
    private bool _isLogDrawerOpen;
    private double? _widthBeforeLogDrawer;
    private const double LogDrawerWidth = 360;
    private const string FluentPlay = "\uE768";
    private const string FluentPause = "\uE769";
    private const string FluentStop = "\uE71A";
    private enum ActiveTask { None, Maze, MainQuest, HardMainQuest, DailyShop, DailyFreeGift, DailyExercises, Redeem, Pipeline }
    private ActiveTask _activeTask = ActiveTask.None;
    private readonly List<string> _pipelineQueue = [];
    private int _pipelineIndex;
    private bool _suppressPipelineToggle;
    private bool _suppressMazeRunLimit;
    private Point _taskDragStart;
    private Border? _taskDragSource;
    private bool _taskDragPending;

    public MainWindow()
    {
        InitializeComponent();
        _captureSession = new GameCaptureSession(AppendLog);
        InitializeShopPanel();
        InitializeMazeRunLimitCombo();
        InitializeRedemptionPanel();
        LoadSettings();
        LoadRunWindows();
        SetPage(Page.Home);
        try
        {
            AutomationConfig config = ConfigStore.Load();
            LocalDataRetention.CleanupOlderThanDays(
                LocalDataRetention.RetainDays,
                ConfigStore.LogsDirectory,
                ConfigStore.ResolveDiagnosticRoot(config.DiagnosticDirectory));
        }
        catch { /* 启动清理静默失败 */ }

        string version = GetAppVersion();
        string title = $"Better-Muv {version} · 更好的 MuvLuv Girls Garden";
        Title = title;
        TitleBarText.Text = title;
        AppendLog("版本：" + version);
        AppendLog("配置文件：" + _configPath);
        AppendLog("等待启动截图器。");
        ContentRendered += MainWindow_ContentRendered;
    }

    private static string GetAppVersion()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string? informational =
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // start.bat 注入 {csproj Version}+local-yyyyMMdd-HHmmss，保留 stamp 方便确认是刚编的。
            int plus = informational.IndexOf('+');
            if (plus >= 0)
            {
                string core = informational[..plus];
                string meta = informational[(plus + 1)..];
                if (meta.StartsWith("local-", StringComparison.OrdinalIgnoreCase))
                    return $"{core} · {meta}";
                return core;
            }

            return informational;
        }

        Version? v = asm.GetName().Version;
        return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
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
    private enum SettingsSection { General, Hotkey, Redeem }

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
    private void ShowSettingsRedeemButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRedemptionUi();
        ShowSettingsSection(SettingsSection.Redeem);
        _ = SyncRedemptionCodesAsync(quiet: true, promptIfNew: false);
    }

    private void ShowSettingsSection(SettingsSection section)
    {
        SettingsGeneralSection.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        SettingsHotkeySection.Visibility = section == SettingsSection.Hotkey ? Visibility.Visible : Visibility.Collapsed;
        SettingsRedeemSection.Visibility = section == SettingsSection.Redeem ? Visibility.Visible : Visibility.Collapsed;
        var active = new SolidColorBrush(Color.FromRgb(59, 66, 78));
        SettingsGeneralNavButton.Background = section == SettingsSection.General ? active : Brushes.Transparent;
        SettingsHotkeyNavButton.Background = section == SettingsSection.Hotkey ? active : Brushes.Transparent;
        SettingsRedeemNavButton.Background = section == SettingsSection.Redeem ? active : Brushes.Transparent;
    }

    private async void CaptureStartButton_Click(object sender, RoutedEventArgs e)
    {
        // 首页「启动一条龙」= 按任务列表开关与顺序串行执行（不再只开截图器）。
        if (_activeTask == ActiveTask.Pipeline && (_runCancellation is not null || _isPaused))
        {
            StopButton_Click(sender, e);
            return;
        }

        if (_runCancellation is not null || _isPaused)
            return;

        SetPage(Page.Execute);
        OpenLogDrawer();
        PersistPipelineSelectionFromUi();
        AutomationConfig pipelineCfg = ConfigStore.Load();
        if (pipelineCfg.MazeTaskEnabled && !PromptSettlementShopIfNeeded())
            return;
        if (!await EnsureCaptureForRunAsync("pipeline"))
            return;
        await StartPipelineAsync();
    }

    private void UpdateCaptureUi()
    {
        bool pipelineBusy = _activeTask == ActiveTask.Pipeline && (_runCancellation is not null || _isPaused);
        bool running = _captureSession.IsRunning;
        CaptureStartButton.Content = pipelineBusy ? "■  停止" : "▷  启动";
        CaptureStartButton.Background = new SolidColorBrush(Color.FromRgb(59, 66, 78));
        MazeStatusText.Text = pipelineBusy ? "一条龙运行中。"
            : running ? "截图器已就绪。" : "等待截图器启动。";
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

    private async void ExportLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportLogButton.IsEnabled)
            return;

        ExportLogButton.IsEnabled = false;
        string previousContent = ExportLogButton.Content as string ?? "导出";
        ExportLogButton.Content = "导出中…";
        try
        {
            Directory.CreateDirectory(ConfigStore.ExportsDirectory);
            string destination = Path.Combine(
                ConfigStore.ExportsDirectory,
                $"Better-Muv-log-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            string directory = _diagnosticSession.RunDirectoryPath
                ?? _diagnosticSession.DirectoryPath
                ?? "";
            string log = LogBox.Text ?? "";
            var screenshot = WindowCaptureService.LatestScreenshot;
            int count = await Task.Run(() => LogBundleExporter.Export(destination, log, directory, screenshot));
            AppendLog($"已导出日志和截图（{count} 张）：{destination}");
            OpenExportLocation(destination);
        }
        catch (Exception exception)
        {
            AppendLog("导出日志失败：" + exception.Message);
        }
        finally
        {
            ExportLogButton.Content = previousContent;
            ExportLogButton.IsEnabled = true;
        }
    }

    private static void OpenExportLocation(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                });
                return;
            }

            string? folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            /* 打开资源管理器失败不影响导出本身 */
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
        var dialog = new OpenFileDialog
        {
            Title = "选择游戏程序或快捷方式",
            Filter = "游戏程序|muv_luv_girlsgardenx_cl.exe|快捷方式 (*.lnk)|*.lnk|程序 (*.exe)|*.exe",
            // 不自动解引用，避免快捷方式目标被静默换成 schtasks 等非游戏路径。
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(this) != true) return;
        string selected = dialog.FileName;
        string? normalized = GamePathLocator.TryNormalize(selected, out string reason);
        if (normalized is null)
        {
            AppendLog("未保存游戏路径：" + reason);
            if (!string.Equals(selected, reason, StringComparison.Ordinal))
                AppendLog("你选择的是：" + selected);
            return;
        }

        if (!string.Equals(selected, normalized, StringComparison.OrdinalIgnoreCase))
            AppendLog($"快捷方式已解析：{selected} → {normalized}");
        ApplyGamePath(normalized, quiet: false);
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
        string? normalized = GamePathLocator.TryNormalize(path, out string reason);
        if (normalized is null)
        {
            if (!quiet)
                AppendLog("未保存游戏路径：" + reason);
            return;
        }

        GamePathBox.Text = normalized;
        AutomationConfig config = ConfigStore.Load();
        config.GameExecutablePath = normalized;
        ConfigStore.Save(config);
        if (!quiet)
            AppendLog($"已永久保存游戏路径：{normalized}\n配置文件：{ConfigStore.UserConfigPath}");
    }

    private void EnsureGamePathResolved()
    {
        AutomationConfig config = ConfigStore.Load();
        if (GamePathLocator.IsValid(config.GameExecutablePath))
        {
            GamePathBox.Text = config.GameExecutablePath;
            return;
        }

        // 配置里是无效路径（如误选的 schtasks.exe）时尝试自动找回并写回。
        string? found = GamePathLocator.TryFind(null);
        if (found is null)
        {
            GamePathBox.Text = config.GameExecutablePath;
            return;
        }

        ApplyGamePath(found, quiet: true);
        AppendLog("已自动纠正并永久保存游戏路径：" + found);
    }

    private void SaveGameLaunchSettings()
    {
        string path = GamePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;
        ApplyGamePath(path, quiet: true);
    }

    private async Task<bool> EnsureCaptureForRunAsync(string statusTarget)
    {
        AutomationConfig config = ConfigStore.Load();
        if (_captureSession.IsRunning && !_captureSession.HasLiveWindow(config))
        {
            AppendLog(config.LaunchGameWithCapture
                ? "游戏窗口已消失，将重新启动游戏并截图器。"
                : "游戏窗口已消失，请先打开游戏；正在重试连接截图器。");
            _captureSession.Stop();
        }

        if (_captureSession.IsRunning)
            return true;
        if (statusTarget == "maze")
            MazeStatusText.Text = "正在启动截图器…";
        else if (statusTarget == "mainQuest")
            MainQuestStatusText.Text = "正在启动截图器…";
        else if (statusTarget == "hardMainQuest")
            HardMainQuestStatusText.Text = "正在启动截图器…";
        else if (statusTarget == "dailyShop")
            DailyShopStatusText.Text = "正在启动截图器…";
        else if (statusTarget == "dailyFreeGift")
            DailyFreeGiftStatusText.Text = "正在启动截图器…";
        else if (statusTarget == "dailyExercises")
            DailyExercisesStatusText.Text = "正在启动截图器…";
        try
        {
            await _captureSession.StartAsync(config, CancellationToken.None);
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
            else if (statusTarget == "dailyShop")
                DailyShopStatusText.Text = message;
            else if (statusTarget == "dailyFreeGift")
                DailyFreeGiftStatusText.Text = message;
            else if (statusTarget == "dailyExercises")
                DailyExercisesStatusText.Text = message;
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
        if (!PromptSettlementShopIfNeeded())
            return;
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

    private async void RunDailyShopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTask == ActiveTask.DailyShop && (_runCancellation is not null || _isPaused))
        {
            PauseResumeButton_Click(sender, e);
            return;
        }
        if (_runCancellation is not null || _isPaused)
            return;
        OpenLogDrawer();
        if (!await EnsureCaptureForRunAsync("dailyShop"))
            return;
        await StartDailyShopAsync();
    }

    private async void RunDailyFreeGiftButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTask == ActiveTask.DailyFreeGift && (_runCancellation is not null || _isPaused))
        {
            PauseResumeButton_Click(sender, e);
            return;
        }
        if (_runCancellation is not null || _isPaused)
            return;
        OpenLogDrawer();
        if (!await EnsureCaptureForRunAsync("dailyFreeGift"))
            return;
        await StartDailyFreeGiftAsync();
    }

    private async void RunDailyExercisesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTask == ActiveTask.DailyExercises && (_runCancellation is not null || _isPaused))
        {
            PauseResumeButton_Click(sender, e);
            return;
        }
        if (_runCancellation is not null || _isPaused)
            return;
        OpenLogDrawer();
        if (!await EnsureCaptureForRunAsync("dailyExercises"))
            return;
        await StartDailyExercisesAsync();
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
        // 一条龙含迷宫时同样提示未配置商店。
        PersistPipelineSelectionFromUi();
        AutomationConfig pipelineCfg = ConfigStore.Load();
        if (pipelineCfg.MazeTaskEnabled && !PromptSettlementShopIfNeeded())
            return;
        if (!await EnsureCaptureForRunAsync("pipeline"))
            return;
        await StartPipelineAsync();
    }

    /// <summary>
    /// 初次跑迷宫且结算商店无任何购买配置时弹窗；选「是」打开商店设置并中止本次启动。
    /// </summary>
    private bool PromptSettlementShopIfNeeded()
    {
        AutomationConfig config = ConfigStore.Load();
        if (config.SettlementShopHintAccepted || config.SettlementPurchases.HasAnyPurchase())
            return true;

        MessageBoxResult choice = MessageBox.Show(
            this,
            "当前结算商店尚未配置购买项。\n迷宫结算时将跳过购买，直接点「完了」。\n\n是否现在去配置？",
            "结算商店提示",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (choice == MessageBoxResult.Yes)
        {
            LoadSettings();
            SetPage(Page.MazeSettings);
            ShowMazeSettingsSection(MazeSettingsSection.Shop);
            AppendLog("已打开结算商店设置；配置购买数量后再启动迷宫。");
            return false;
        }

        config.SettlementShopHintAccepted = true;
        ConfigStore.Save(config);
        AppendLog("已跳过商店配置提示；之后可在迷宫设置 → 结算商店中配置购买。");
        return true;
    }

    private async Task StartMazeAsync()
    {
        if (_runCancellation is not null) return;
        bool resume = _isPaused;
        BeginRunLogSession(resume);
        PersistMazeSettings(quiet: true);
        _resumeDiagnosticTask = resume;
        BeginDiagnosticRun(resume);
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
            CaptureOnAutoStop("maze");
        }
        finally
        {
            FinishRunSession();
        }
    }

    private async Task StartMainQuestAsync()
    {
        if (_runCancellation is not null) return;
        bool resume = _isPaused;
        BeginRunLogSession(resume);
        _resumeDiagnosticTask = resume;
        BeginDiagnosticRun(resume);
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
            CaptureOnAutoStop("mainQuest");
        }
        finally
        {
            FinishRunSession();
        }
    }

    private async Task StartHardMainQuestAsync()
    {
        if (_runCancellation is not null) return;
        bool resume = _isPaused;
        BeginRunLogSession(resume);
        _resumeDiagnosticTask = resume;
        BeginDiagnosticRun(resume);
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
            CaptureOnAutoStop("hardMainQuest");
        }
        finally
        {
            FinishRunSession();
        }
    }

    private async Task StartDailyShopAsync()
    {
        if (_runCancellation is not null) return;
        bool resume = _isPaused;
        BeginRunLogSession(resume);
        _resumeDiagnosticTask = resume;
        BeginDiagnosticRun(resume);
        _isPaused = false;
        _pauseRequested = false;
        _activeTask = ActiveTask.DailyShop;
        _runCancellation = new CancellationTokenSource();
        UpdateRunUi();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(250, _runCancellation.Token);
            await RunDailyShopCoreAsync(_runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog(_pauseRequested ? "每日商店已暂停。" : "每日商店已停止。");
        }
        catch (Exception exception)
        {
            AppendLog("每日商店错误：" + exception.Message);
            CaptureOnAutoStop("dailyShop");
        }
        finally
        {
            FinishRunSession();
        }
    }

    private async Task StartDailyFreeGiftAsync()
    {
        if (_runCancellation is not null) return;
        bool resume = _isPaused;
        BeginRunLogSession(resume);
        _resumeDiagnosticTask = resume;
        BeginDiagnosticRun(resume);
        _isPaused = false;
        _pauseRequested = false;
        _activeTask = ActiveTask.DailyFreeGift;
        _runCancellation = new CancellationTokenSource();
        UpdateRunUi();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(250, _runCancellation.Token);
            await RunDailyFreeGiftCoreAsync(_runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog(_pauseRequested ? "每日免费礼包已暂停。" : "每日免费礼包已停止。");
        }
        catch (Exception exception)
        {
            AppendLog("每日免费礼包错误：" + exception.Message);
            CaptureOnAutoStop("dailyFreeGift");
        }
        finally
        {
            FinishRunSession();
        }
    }

    private async Task StartDailyExercisesAsync()
    {
        if (_runCancellation is not null) return;
        bool resume = _isPaused;
        BeginRunLogSession(resume);
        _resumeDiagnosticTask = resume;
        BeginDiagnosticRun(resume);
        _isPaused = false;
        _pauseRequested = false;
        _activeTask = ActiveTask.DailyExercises;
        _runCancellation = new CancellationTokenSource();
        UpdateRunUi();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(250, _runCancellation.Token);
            await RunDailyExercisesCoreAsync(_runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog(_pauseRequested ? "每日演习已暂停。" : "每日演习已停止。");
        }
        catch (Exception exception)
        {
            AppendLog("每日演习错误：" + exception.Message);
            CaptureOnAutoStop("dailyExercises");
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
        BeginRunLogSession(resume);
        _resumeDiagnosticTask = resume;
        BeginDiagnosticRun(resume);
        _isPaused = false;
        _pauseRequested = false;
        if (!resume)
        {
            // 以当前任务列表 UI（顺序 + 开关）为准并写回配置，避免“看起来开了但没跑”。
            PersistPipelineSelectionFromUi();
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
                else if (id == "dailyShop" && config.DailyShopTaskEnabled)
                    _pipelineQueue.Add(id);
                else if (id == "dailyFreeGift" && config.DailyFreeGiftTaskEnabled)
                    _pipelineQueue.Add(id);
                else if (id == "dailyExercises" && config.DailyExercisesTaskEnabled)
                    _pipelineQueue.Add(id);
            }

            _pipelineIndex = 0;
            if (_pipelineQueue.Count == 0)
            {
                AppendLog("一条龙未启用任何任务，已取消。请打开右侧开关后再运行。");
                _activeTask = ActiveTask.None;
                UpdateRunUi();
                return;
            }

            ClearPipelineResultHints();
            AppendLog("一条龙队列：" + string.Join(" → ", _pipelineQueue.Select(PipelineTaskDisplayName)) +
                      $"（共 {_pipelineQueue.Count} 项）。");
            if (_pipelineQueue.Contains("maze") && config.MazeRunLimit == 0)
                AppendLog("提示：迷宫次数为「无限」，后续任务会等迷宫手动停止后才会开始。");
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
                SetPipelineResultHint(id, PipelineHintState.Running);
                AppendLog($"一条龙：开始 {PipelineTaskDisplayName(id)}（{_pipelineIndex + 1}/{_pipelineQueue.Count}）。");
                try
                {
                    if (id == "maze")
                        await RunMazeCoreAsync(_runCancellation.Token);
                    else if (id == "hardMainQuest")
                        await RunHardMainQuestCoreAsync(_runCancellation.Token);
                    else if (id == "dailyShop")
                        await RunDailyShopCoreAsync(_runCancellation.Token);
                    else if (id == "dailyFreeGift")
                        await RunDailyFreeGiftCoreAsync(_runCancellation.Token);
                    else if (id == "dailyExercises")
                        await RunDailyExercisesCoreAsync(_runCancellation.Token);
                    else
                        await RunMainQuestCoreAsync(_runCancellation.Token);
                    SetPipelineResultHint(id, PipelineHintState.Success);
                }
                catch (OperationCanceledException)
                {
                    SetPipelineResultHint(
                        id,
                        _pauseRequested ? PipelineHintState.Running : PipelineHintState.Cancelled,
                        _pauseRequested ? "已暂停，可继续" : null);
                    if (_pauseRequested)
                    {
                        TextBlock? tip = PipelineResultTipFor(id);
                        if (tip is not null)
                            tip.Text = "已暂停";
                    }
                    throw;
                }
                catch (Exception taskEx)
                {
                    SetPipelineResultHint(id, PipelineHintState.Failure, taskEx.Message);
                    AppendLog($"一条龙：{PipelineTaskDisplayName(id)} 出错，继续下一项 — {taskEx.Message}");
                    CaptureOnAutoStop(id);
                }

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
            CaptureOnAutoStop("pipeline");
        }
        finally
        {
            FinishRunSession();
        }
    }

    private enum PipelineHintState { Running, Success, Failure, Cancelled }

    private void ClearPipelineResultHints()
    {
        foreach (TextBlock tip in PipelineResultTips())
        {
            tip.Text = "";
            tip.Visibility = Visibility.Collapsed;
            tip.ToolTip = null;
        }
    }

    private IEnumerable<TextBlock> PipelineResultTips()
    {
        yield return MazePipelineResultText;
        yield return MainQuestPipelineResultText;
        yield return HardMainQuestPipelineResultText;
        yield return DailyShopPipelineResultText;
        yield return DailyFreeGiftPipelineResultText;
        yield return DailyExercisesPipelineResultText;
    }

    private TextBlock? PipelineResultTipFor(string id) => id switch
    {
        "maze" => MazePipelineResultText,
        "mainQuest" => MainQuestPipelineResultText,
        "hardMainQuest" => HardMainQuestPipelineResultText,
        "dailyShop" => DailyShopPipelineResultText,
        "dailyFreeGift" => DailyFreeGiftPipelineResultText,
        "dailyExercises" => DailyExercisesPipelineResultText,
        _ => null
    };

    private void SetPipelineResultHint(string id, PipelineHintState state, string? detail = null)
    {
        TextBlock? tip = PipelineResultTipFor(id);
        if (tip is null) return;

        tip.Visibility = Visibility.Visible;
        tip.ToolTip = string.IsNullOrWhiteSpace(detail) ? null : detail;
        switch (state)
        {
            case PipelineHintState.Running:
                tip.Text = "执行中…";
                tip.Foreground = new SolidColorBrush(Color.FromRgb(0x3E, 0xA6, 0xF1));
                break;
            case PipelineHintState.Success:
                tip.Text = "成功";
                tip.Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0xC9, 0x7A));
                break;
            case PipelineHintState.Failure:
                tip.Text = "失败";
                tip.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x6B, 0x6B));
                break;
            case PipelineHintState.Cancelled:
                tip.Text = "已中断";
                tip.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x45));
                break;
        }
    }

    /// <summary>把任务页当前顺序与「纳入一条龙」开关写回配置。</summary>
    private void PersistPipelineSelectionFromUi()
    {
        AutomationConfig config = ConfigStore.Load();
        config.MazeTaskEnabled = MazePipelineToggle.IsChecked == true;
        config.MainQuestTaskEnabled = MainQuestPipelineToggle.IsChecked == true;
        config.HardMainQuestTaskEnabled = HardMainQuestPipelineToggle.IsChecked == true;
        config.DailyShopTaskEnabled = DailyShopPipelineToggle.IsChecked == true;
        config.DailyFreeGiftTaskEnabled = DailyFreeGiftPipelineToggle.IsChecked == true;
        config.DailyExercisesTaskEnabled = DailyExercisesPipelineToggle.IsChecked == true;
        config.PipelineTaskOrder = TaskListPanel.Children
            .OfType<FrameworkElement>()
            .Select(card => card.Tag as string)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
        ConfigStore.Save(config);
    }

    private Task RunMazeCoreAsync(CancellationToken cancellationToken)
    {
        var automation = new MazeAutomation(PrepareDiagnosticTask("maze"), AppendLog);
        return automation.RunOnceAsync(cancellationToken);
    }

    private Task RunMainQuestCoreAsync(CancellationToken cancellationToken)
    {
        var automation = new MainQuestAutomation(PrepareDiagnosticTask("mainQuest"), AppendLog);
        return automation.RunOnceAsync(cancellationToken);
    }

    private Task RunHardMainQuestCoreAsync(CancellationToken cancellationToken)
    {
        var automation = new HardMainQuestAutomation(PrepareDiagnosticTask("hardMainQuest"), AppendLog);
        return automation.RunOnceAsync(cancellationToken);
    }

    private Task RunDailyShopCoreAsync(CancellationToken cancellationToken)
    {
        var automation = new DailyShopAutomation(PrepareDiagnosticTask("dailyShop"), AppendLog);
        return automation.RunOnceAsync(cancellationToken);
    }

    private Task RunDailyFreeGiftCoreAsync(CancellationToken cancellationToken)
    {
        var automation = new DailyFreeGiftAutomation(PrepareDiagnosticTask("dailyFreeGift"), AppendLog);
        return automation.RunOnceAsync(cancellationToken);
    }

    private Task RunDailyExercisesCoreAsync(CancellationToken cancellationToken)
    {
        var automation = new DailyExercisesAutomation(PrepareDiagnosticTask("dailyExercises"), AppendLog);
        return automation.RunOnceAsync(cancellationToken);
    }

    private void BeginDiagnosticRun(bool resume)
    {
        AutomationConfig config = ConfigStore.Load();
        string root = ConfigStore.ResolveDiagnosticRoot(config.DiagnosticDirectory);
        if (!resume)
            WindowCaptureService.ResetScreenshot();
        string runDirectory = _diagnosticSession.BeginRun(root, resume);
        WindowCaptureService.SetArchiveDirectory(Path.Combine(runDirectory, "screenshots"));
        if (!resume)
            AppendLog($"本轮诊断批次：{Path.GetFileName(runDirectory)}");
    }

    private AutomationConfig PrepareDiagnosticTask(string taskKey)
    {
        AutomationConfig config = ConfigStore.Load();
        string root = ConfigStore.ResolveDiagnosticRoot(config.DiagnosticDirectory);
        bool resume = _resumeDiagnosticTask && _diagnosticSession.TaskKey == taskKey;
        _resumeDiagnosticTask = false;
        // 单任务入口若未先 BeginRun，这里兜底；一条龙已在 StartPipeline 建好批次。
        if (_diagnosticSession.RunDirectoryPath is null)
            BeginDiagnosticRun(resume);
        string taskDirectory = _diagnosticSession.Begin(root, taskKey, resume);
        // 仅运行时指向任务目录；ConfigStore.Save 会把根路径写回配置，避免嵌套。
        config.DiagnosticDirectory = taskDirectory;
        AppendLog($"{PipelineTaskDisplayName(taskKey)}诊断目录：{Path.GetFileName(taskDirectory)}");
        return config;
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

        ReloadShopPurchases();
        UpdateRunUi();
        UpdateCaptureUi();
    }

    /// <summary>
    /// 错误自动停止：只截游戏界面到诊断目录，不打包；手动「导出」时一并打进 ZIP。
    /// 须在恢复本窗口之前调用（此时游戏仍在前台）。
    /// </summary>
    private void CaptureOnAutoStop(string reason)
    {
        string? directory = _diagnosticSession.DirectoryPath
            ?? _diagnosticSession.RunDirectoryPath;
        AutoStopCapture.SaveErrorUi(directory, TryCaptureGameClientForAutoStop(), reason, AppendLog);
    }

    private BitmapSource? TryCaptureGameClientForAutoStop()
    {
        try
        {
            AutomationConfig config = ConfigStore.Load();
            var capture = new WindowCaptureService();
            GameWindow window = capture.FindWindow(config);
            return capture.CaptureClient(window);
        }
        catch
        {
            return WindowCaptureService.LatestScreenshot?.Image;
        }
    }

    private static string PipelineTaskDisplayName(string id) => id switch
    {
        "maze" => "迷宫探索",
        "hardMainQuest" => "自动困难主线",
        "dailyShop" => "每日商店",
        "dailyFreeGift" => "每日免费礼包",
        "dailyExercises" => "每日演习",
        "redeem" => "兑换码",
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
            else if (_activeTask == ActiveTask.DailyShop)
                _ = StartDailyShopAsync();
            else if (_activeTask == ActiveTask.DailyFreeGift)
                _ = StartDailyFreeGiftAsync();
            else if (_activeTask == ActiveTask.DailyExercises)
                _ = StartDailyExercisesAsync();
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
            RunDailyShopButton.IsEnabled = false;
            RunDailyFreeGiftButton.IsEnabled = false;
            RunDailyExercisesButton.IsEnabled = false;
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
        StopDailyShopButton.IsEnabled = false;
        StopDailyFreeGiftButton.IsEnabled = false;
        StopDailyExercisesButton.IsEnabled = false;
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
        bool dailyShopUi = _activeTask == ActiveTask.DailyShop;
        bool dailyFreeGiftUi = _activeTask == ActiveTask.DailyFreeGift;
        bool dailyExercisesUi = _activeTask == ActiveTask.DailyExercises;
        bool redeemUi = _activeTask == ActiveTask.Redeem;
        bool pipelineUi = _activeTask == ActiveTask.Pipeline;
        bool idle = !running && !_isPaused;

        RunMazeButton.IsEnabled = idle || mazeUi;
        RunMainQuestButton.IsEnabled = idle || mainQuestUi;
        RunHardMainQuestButton.IsEnabled = idle || hardMainQuestUi;
        RunDailyShopButton.IsEnabled = idle || dailyShopUi;
        RunDailyFreeGiftButton.IsEnabled = idle || dailyFreeGiftUi;
        RunDailyExercisesButton.IsEnabled = idle || dailyExercisesUi;
        RunPipelineButton.IsEnabled = idle || pipelineUi;
        MazePipelineToggle.IsEnabled = idle;
        MainQuestPipelineToggle.IsEnabled = idle;
        HardMainQuestPipelineToggle.IsEnabled = idle;
        DailyShopPipelineToggle.IsEnabled = idle;
        DailyFreeGiftPipelineToggle.IsEnabled = idle;
        DailyExercisesPipelineToggle.IsEnabled = idle;

        // 独立暂停键隐藏；运行键兼任暂停/继续，旁边保留停止键。
        PauseResumeButton.Visibility = Visibility.Collapsed;
        PauseMainQuestButton.Visibility = Visibility.Collapsed;
        PauseHardMainQuestButton.Visibility = Visibility.Collapsed;
        PauseDailyShopButton.Visibility = Visibility.Collapsed;
        PauseDailyFreeGiftButton.Visibility = Visibility.Collapsed;
        PauseDailyExercisesButton.Visibility = Visibility.Collapsed;
        PausePipelineButton.Visibility = Visibility.Collapsed;

        StopButton.Visibility = showControls && mazeUi ? Visibility.Visible : Visibility.Collapsed;
        StopMainQuestButton.Visibility = showControls && mainQuestUi ? Visibility.Visible : Visibility.Collapsed;
        StopHardMainQuestButton.Visibility = showControls && hardMainQuestUi ? Visibility.Visible : Visibility.Collapsed;
        StopDailyShopButton.Visibility = showControls && dailyShopUi ? Visibility.Visible : Visibility.Collapsed;
        StopDailyFreeGiftButton.Visibility = showControls && dailyFreeGiftUi ? Visibility.Visible : Visibility.Collapsed;
        StopDailyExercisesButton.Visibility = showControls && dailyExercisesUi ? Visibility.Visible : Visibility.Collapsed;
        StopPipelineButton.Visibility = showControls && (pipelineUi || redeemUi) ? Visibility.Visible : Visibility.Collapsed;
        RunPipelineButton.Visibility = Visibility.Visible;

        // 运行中：暂停 + 停止；暂停中：继续(播放) + 停止。
        string runGlyph = !showControls ? FluentPlay : _isPaused ? FluentPlay : FluentPause;
        RunMazeButton.Content = mazeUi && showControls ? runGlyph : FluentPlay;
        RunMainQuestButton.Content = mainQuestUi && showControls ? runGlyph : FluentPlay;
        RunHardMainQuestButton.Content = hardMainQuestUi && showControls ? runGlyph : FluentPlay;
        RunDailyShopButton.Content = dailyShopUi && showControls ? runGlyph : FluentPlay;
        RunDailyFreeGiftButton.Content = dailyFreeGiftUi && showControls ? runGlyph : FluentPlay;
        RunDailyExercisesButton.Content = dailyExercisesUi && showControls ? runGlyph : FluentPlay;
        RunMazeButton.ToolTip = mazeUi && running ? "暂停" : mazeUi && _isPaused ? "继续" : "运行";
        RunMainQuestButton.ToolTip = mainQuestUi && running ? "暂停" : mainQuestUi && _isPaused ? "继续" : "运行";
        RunHardMainQuestButton.ToolTip = hardMainQuestUi && running ? "暂停" : hardMainQuestUi && _isPaused ? "继续" : "运行";
        RunDailyShopButton.ToolTip = dailyShopUi && running ? "暂停" : dailyShopUi && _isPaused ? "继续" : "运行";
        RunDailyFreeGiftButton.ToolTip = dailyFreeGiftUi && running ? "暂停" : dailyFreeGiftUi && _isPaused ? "继续" : "运行";
        RunDailyExercisesButton.ToolTip = dailyExercisesUi && running ? "暂停" : dailyExercisesUi && _isPaused ? "继续" : "运行";
        RunPipelineButton.Content = pipelineUi && running ? "暂停"
            : pipelineUi && _isPaused ? "继续"
            : "运行";

        StopButton.Content = FluentStop;
        StopMainQuestButton.Content = FluentStop;
        StopHardMainQuestButton.Content = FluentStop;
        StopDailyShopButton.Content = FluentStop;
        StopDailyFreeGiftButton.Content = FluentStop;
        StopDailyExercisesButton.Content = FluentStop;
        StopPipelineButton.Content = FluentStop;
        StopButton.ToolTip = "停止";
        StopMainQuestButton.ToolTip = "停止";
        StopHardMainQuestButton.ToolTip = "停止";
        StopDailyShopButton.ToolTip = "停止";
        StopDailyFreeGiftButton.ToolTip = "停止";
        StopDailyExercisesButton.ToolTip = "停止";
        StopPipelineButton.ToolTip = "停止";
        StopButton.IsEnabled = true;
        StopMainQuestButton.IsEnabled = true;
        StopHardMainQuestButton.IsEnabled = true;
        StopDailyShopButton.IsEnabled = true;
        StopDailyFreeGiftButton.IsEnabled = true;
        StopDailyExercisesButton.IsEnabled = true;
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
        DailyShopStatusText.Text = dailyShopUi && running ? "每日商店运行中。"
            : dailyShopUi && _isPaused ? "每日商店已暂停。"
            : "";
        DailyFreeGiftStatusText.Text = dailyFreeGiftUi && running ? "每日免费礼包运行中。"
            : dailyFreeGiftUi && _isPaused ? "每日免费礼包已暂停。"
            : "";
        DailyExercisesStatusText.Text = dailyExercisesUi && running ? "每日演习运行中。"
            : dailyExercisesUi && _isPaused ? "每日演习已暂停。"
            : "";
    }

    private void LoadSettings()
    {
        AutomationConfig config = ConfigStore.Load();
        LaunchGameCheckBox.IsChecked = config.LaunchGameWithCapture;
        EnsureGamePathResolved();
        SetMazeRunLimitCombo(config.MazeRunLimit);
        string difficultyMode = MazeDifficultyRunner.NormalizeMode(config.MazeDifficultyMode);
        DifficultyKeepRadio.IsChecked = difficultyMode == "keep";
        DifficultyCustomRadio.IsChecked = difficultyMode == "custom";
        DifficultyTowerRadio.IsChecked = difficultyMode == "tower";
        DifficultyTargetBox.Text = config.MazeDifficultyTarget.ToString();
        DifficultyTargetBox.IsEnabled = DifficultyCustomRadio.IsChecked == true;
        TreasurePriorityList.Items.Clear();
        foreach (string key in config.TreasurePriority)
            TreasurePriorityList.Items.Add(TreasureNames.TryGetValue(key, out string? display) ? display : key);
        if (TreasurePriorityList.Items.Count > 0) TreasurePriorityList.SelectedIndex = 0;
        PauseHotkeyBox.Text = config.PauseHotkey;
        StopHotkeyBox.Text = config.StopHotkey;
        DiagnosticModeCheckBox.IsChecked = config.SaveDiagnostics;
        _suppressRedeemPromptToggle = true;
        RedeemPromptCheckBox.IsChecked = config.RedeemPromptOnNewCodes;
        _suppressRedeemPromptToggle = false;
        _suppressPipelineToggle = true;
        MazePipelineToggle.IsChecked = config.MazeTaskEnabled;
        MainQuestPipelineToggle.IsChecked = config.MainQuestTaskEnabled;
        HardMainQuestPipelineToggle.IsChecked = config.HardMainQuestTaskEnabled;
        DailyShopPipelineToggle.IsChecked = config.DailyShopTaskEnabled;
        DailyFreeGiftPipelineToggle.IsChecked = config.DailyFreeGiftTaskEnabled;
        DailyExercisesPipelineToggle.IsChecked = config.DailyExercisesTaskEnabled;
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
        config.DailyShopTaskEnabled = DailyShopPipelineToggle.IsChecked == true;
        config.DailyFreeGiftTaskEnabled = DailyFreeGiftPipelineToggle.IsChecked == true;
        config.DailyExercisesTaskEnabled = DailyExercisesPipelineToggle.IsChecked == true;
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
            ["hardMainQuest"] = HardMainQuestTaskCard,
            ["dailyShop"] = DailyShopTaskCard,
            ["dailyFreeGift"] = DailyFreeGiftTaskCard,
            ["dailyExercises"] = DailyExercisesTaskCard
        };
        TaskListPanel.Children.Clear();
        foreach (string id in AutomationConfig.NormalizePipelineTaskOrder(order))
        {
            if (cards.TryGetValue(id, out UIElement? card))
                TaskListPanel.Children.Add(card);
        }
    }

    private void InitializeMazeRunLimitCombo()
    {
        MazeRunLimitCombo.Items.Clear();
        MazeRunLimitCombo.Items.Add("无限");
        foreach (int n in new[] { 1, 2, 3, 5, 10, 20, 30, 50, 100 })
            MazeRunLimitCombo.Items.Add(n.ToString());
    }

    private void MazeRunLimitCombo_Loaded(object sender, RoutedEventArgs e)
    {
        WireMazeRunLimitEditableBox();
    }

    private void WireMazeRunLimitEditableBox()
    {
        if (MazeRunLimitCombo.Template?.FindName("PART_EditableTextBox", MazeRunLimitCombo) is not TextBox box)
            return;
        box.PreviewTextInput -= MazeRunLimitEditable_PreviewTextInput;
        box.PreviewTextInput += MazeRunLimitEditable_PreviewTextInput;
        box.PreviewKeyDown -= MazeRunLimitEditable_PreviewKeyDown;
        box.PreviewKeyDown += MazeRunLimitEditable_PreviewKeyDown;
        DataObject.RemovePastingHandler(box, MazeRunLimitEditable_Pasting);
        DataObject.AddPastingHandler(box, MazeRunLimitEditable_Pasting);
        InputMethod.SetIsInputMethodEnabled(box, false);
        box.MaxLength = 4;
    }

    private void MazeRunLimitEditable_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        // 仅允许数字；从「无限」开始敲数字时先清空再填入。
        if (string.IsNullOrEmpty(e.Text) || !e.Text.All(char.IsDigit))
        {
            e.Handled = true;
            return;
        }

        if (box.Text is "无限" or "∞")
        {
            box.Text = e.Text;
            box.CaretIndex = box.Text.Length;
            e.Handled = true;
        }
    }

    private void MazeRunLimitEditable_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space)
            e.Handled = true;
    }

    private void MazeRunLimitEditable_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        string text = (e.DataObject.GetData(DataFormats.Text) as string ?? "").Trim();
        if (text.Length == 0 || !text.All(char.IsDigit))
            e.CancelCommand();
    }

    private static string FormatMazeRunLimit(int limit) => limit <= 0 ? "无限" : limit.ToString();

    private static int ParseMazeRunLimitText(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        string t = text.Trim();
        if (t is "无限" or "∞" or "0")
            return 0;
        return int.TryParse(t, out int n) && n >= 0 ? n : fallback;
    }

    private void SetMazeRunLimitCombo(int limit)
    {
        _suppressMazeRunLimit = true;
        try
        {
            string display = FormatMazeRunLimit(limit);
            MazeRunLimitCombo.SelectedIndex = MazeRunLimitCombo.Items.IndexOf(display);
            MazeRunLimitCombo.Text = display;
            if (MazeRunLimitCombo.Template?.FindName("PART_EditableTextBox", MazeRunLimitCombo) is TextBox box)
                box.Text = display;
        }
        finally
        {
            _suppressMazeRunLimit = false;
        }
    }

    private void MazeRunLimitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressMazeRunLimit) return;
        if (MazeRunLimitCombo.SelectedItem is string selected)
        {
            MazeRunLimitCombo.Text = selected;
            if (MazeRunLimitCombo.Template?.FindName("PART_EditableTextBox", MazeRunLimitCombo) is TextBox box)
                box.Text = selected;
        }
        PersistMazeSettings(quiet: true);
    }

    private void MazeRunLimitCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsLoaded || _suppressMazeRunLimit) return;
        PersistMazeSettings(quiet: true);
        // 把用户填的 0 规范化成「无限」显示。
        AutomationConfig config = ConfigStore.Load();
        SetMazeRunLimitCombo(config.MazeRunLimit);
    }

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
        string raw = MazeRunLimitCombo.Template?.FindName("PART_EditableTextBox", MazeRunLimitCombo) is TextBox box
            ? box.Text
            : MazeRunLimitCombo.Text;
        int limit = ParseMazeRunLimitText(raw, config.MazeRunLimit);
        if (!int.TryParse(DifficultyTargetBox.Text.Trim(), out int target) || target is < 1 or > 999) target = config.MazeDifficultyTarget;
        config.MazeRunLimit = limit;
        config.MazeDifficultyMode =
            DifficultyTowerRadio.IsChecked == true ? "tower"
            : DifficultyCustomRadio.IsChecked == true ? "custom"
            : "keep";
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
        try
        {
            lock (_logFileLock)
            {
                string path = string.IsNullOrWhiteSpace(_sessionLogPath)
                    ? ConfigStore.EnsureLogFilePath()
                    : _sessionLogPath;
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* 日志落盘失败不影响主流程 */ }

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

    /// <summary>
    /// 重新开始任务/一条龙：清空界面日志并新开日志文件。暂停后继续则保留。
    /// </summary>
    private void BeginRunLogSession(bool resume)
    {
        if (resume)
            return;

        void ClearUi()
        {
            LogBox.Clear();
        }

        if (Dispatcher.CheckAccess())
            ClearUi();
        else
            Dispatcher.Invoke(ClearUi);

        Directory.CreateDirectory(ConfigStore.LogsDirectory);
        string path = Path.Combine(
            ConfigStore.LogsDirectory,
            $"Better-Muv-run-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        lock (_logFileLock)
            _sessionLogPath = path;

        AppendLog("版本：" + GetAppVersion());
        AppendLog("本轮日志文件：" + path);
    }
}
