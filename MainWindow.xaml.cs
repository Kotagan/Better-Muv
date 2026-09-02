using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BetterMuv.Core;

namespace BetterMuv;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x4D55;
    private const int WmHotkey = 0x0312;
    private static readonly IReadOnlyDictionary<string, string> TreasureNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["diamond"] = "钻石",
            ["sparkle"] = "闪光",
            ["shield"] = "盾",
            ["sword"] = "剑",
            ["heart"] = "心"
        };

    private CancellationTokenSource? _cancellation;
    private readonly string _configPath = ConfigStore.EnsureUserConfigPath();
    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        LoadTreasurePriority();
        LoadHotkeySetting();
        InitializeShopPanel();
        LoadMazeRunLimit();
        AppendLog("配置文件：" + _configPath);
        AppendLog("等待开始。");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null)
            return;

        PersistShopPurchases(quiet: true);
        PersistMazeRunLimit(quiet: true);

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = "运行中";
        _cancellation = new CancellationTokenSource();

        try
        {
            AutomationConfig config = ConfigStore.Load();
            var automation = new MazeAutomation(config, AppendLog);
            await automation.RunOnceAsync(_cancellation.Token);
            StatusText.Text = "已完成";
        }
        catch (OperationCanceledException)
        {
            AppendLog("流程已停止。");
            StatusText.Text = "已停止";
        }
        catch (Exception exception)
        {
            AppendLog("错误：" + exception.Message);
            StatusText.Text = "失败";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        _cancellation?.Cancel();
    }

    private void ShowHomeButton_Click(object sender, RoutedEventArgs e)
    {
        SetNavVisibility(home: true);
        HomeNavButton.Background = new SolidColorBrush(Color.FromRgb(48, 57, 70));
        ShopNavButton.Background = Brushes.Transparent;
        PriorityNavButton.Background = Brushes.Transparent;
        HotkeyNavButton.Background = Brushes.Transparent;
    }

    private void ShowPriorityButton_Click(object sender, RoutedEventArgs e)
    {
        SetNavVisibility(priority: true);
        HomeNavButton.Background = Brushes.Transparent;
        ShopNavButton.Background = Brushes.Transparent;
        PriorityNavButton.Background = new SolidColorBrush(Color.FromRgb(48, 57, 70));
        HotkeyNavButton.Background = Brushes.Transparent;
    }

    private void ShowHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        SetNavVisibility(hotkey: true);
        HomeNavButton.Background = Brushes.Transparent;
        ShopNavButton.Background = Brushes.Transparent;
        PriorityNavButton.Background = Brushes.Transparent;
        HotkeyNavButton.Background = new SolidColorBrush(Color.FromRgb(48, 57, 70));
    }

    private void MovePriorityUp_Click(object sender, RoutedEventArgs e) =>
        MovePriorityItem(-1);

    private void MovePriorityDown_Click(object sender, RoutedEventArgs e) =>
        MovePriorityItem(1);

    private void MovePriorityItem(int offset)
    {
        int index = TreasurePriorityList.SelectedIndex;
        int target = index + offset;
        if (index < 0 || target < 0 || target >= TreasurePriorityList.Items.Count)
            return;
        object item = TreasurePriorityList.Items[index];
        TreasurePriorityList.Items.RemoveAt(index);
        TreasurePriorityList.Items.Insert(target, item);
        TreasurePriorityList.SelectedIndex = target;
    }

    private void SavePriority_Click(object sender, RoutedEventArgs e)
    {
        AutomationConfig config = ConfigStore.Load();
        config.TreasurePriority = TreasurePriorityList.Items
            .Cast<string>()
            .Select(display => TreasureNames.First(pair => pair.Value == display).Key)
            .ToList();
        ConfigStore.Save(config);
        AppendLog("宝物优先级已保存：" + string.Join(" → ", TreasurePriorityList.Items.Cast<string>()));
    }

    private void LoadTreasurePriority()
    {
        AutomationConfig config = ConfigStore.Load();
        TreasurePriorityList.Items.Clear();
        foreach (string key in config.TreasurePriority)
            TreasurePriorityList.Items.Add(TreasureNames.TryGetValue(key, out string? name) ? name : key);
        if (TreasurePriorityList.Items.Count > 0)
            TreasurePriorityList.SelectedIndex = 0;
    }

    private void LoadMazeRunLimit()
    {
        AutomationConfig config = ConfigStore.Load();
        MazeRunLimitBox.Text = config.MazeRunLimit.ToString();
    }

    private void MazeRunLimitBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        PersistMazeRunLimit();

    private void MazeRunLimitBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            PersistMazeRunLimit();
    }

    private void PersistMazeRunLimit(bool quiet = false)
    {
        if (!int.TryParse(MazeRunLimitBox.Text.Trim(), out int limit) || limit < 0)
        {
            AutomationConfig current = ConfigStore.Load();
            MazeRunLimitBox.Text = current.MazeRunLimit.ToString();
            if (!quiet)
                AppendLog("迷宫次数须为大于等于 0 的整数（0 为无限）。");
            return;
        }

        AutomationConfig config = ConfigStore.Load();
        if (config.MazeRunLimit == limit)
            return;
        config.MazeRunLimit = limit;
        ConfigStore.Save(config);
        if (!quiet)
            AppendLog(limit == 0 ? "迷宫次数已设为无限。" : $"迷宫次数已设为 {limit}。");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        PersistShopPurchases(quiet: true);
        PersistMazeRunLimit(quiet: true);
        _cancellation?.Cancel();
        ReleaseHotkey();
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(message));
            return;
        }

        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}