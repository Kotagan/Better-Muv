using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using BetterMuv.Core;

namespace BetterMuv;

public partial class MainWindow
{
    private const int PauseHotkeyId = 0x4D55;
    private const int StopHotkeyId = 0x4D56;
    private const int WmHotkey = 0x0312;
    /// <summary>RegisterHotKey：忽略连按产生的重复 WM_HOTKEY。</summary>
    private const uint ModNorepeat = 0x4000;
    private HwndSource? _windowSource;

    /// <summary>
    /// RegisterHotKey 在游戏全屏/独占输入时经常收不到；轮询物理键状态作兜底。
    /// </summary>
    private DispatcherTimer? _hotkeyPollTimer;
    private uint _stopVirtualKey;
    private uint _startVirtualKey;
    private bool _stopKeyWasDown;
    private bool _startKeyWasDown;
    private bool _stopHotkeyRegistered;
    private bool _startHotkeyRegistered;

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource.AddHook(HandleWindowMessage);
        RegisterConfiguredHotkeys();
    }

    private void RegisterConfiguredHotkeys()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        UnregisterHotKey(handle, PauseHotkeyId);
        UnregisterHotKey(handle, StopHotkeyId);
        AutomationConfig config = ConfigStore.Load();
        bool hasStartHotkey = !string.IsNullOrWhiteSpace(config.PauseHotkey);
        _startVirtualKey = hasStartHotkey
            ? (uint)KeyInterop.VirtualKeyFromKey(Enum.Parse<Key>(config.PauseHotkey))
            : 0;
        _stopVirtualKey = (uint)KeyInterop.VirtualKeyFromKey(Enum.Parse<Key>(config.StopHotkey));

        bool pause = !hasStartHotkey || RegisterHotKey(handle, PauseHotkeyId, ModNorepeat, _startVirtualKey);
        int pauseError = pause ? 0 : Marshal.GetLastWin32Error();
        // 部分环境不接受 MOD_NOREPEAT，回退无修饰符再试一次。
        if (!pause && hasStartHotkey && pauseError != 0)
        {
            pause = RegisterHotKey(handle, PauseHotkeyId, 0, _startVirtualKey);
            pauseError = pause ? 0 : Marshal.GetLastWin32Error();
        }

        bool stop = RegisterHotKey(handle, StopHotkeyId, ModNorepeat, _stopVirtualKey);
        int stopError = stop ? 0 : Marshal.GetLastWin32Error();
        if (!stop && stopError != 0)
        {
            stop = RegisterHotKey(handle, StopHotkeyId, 0, _stopVirtualKey);
            stopError = stop ? 0 : Marshal.GetLastWin32Error();
        }

        _startHotkeyRegistered = !hasStartHotkey || pause;
        _stopHotkeyRegistered = stop;

        if (pause && stop)
        {
            AppendLog(hasStartHotkey
                ? $"已注册热键：{config.PauseHotkey} 启动，{config.StopHotkey} 停止。"
                : $"已注册热键：{config.StopHotkey} 停止；启动热键未设置。");
        }
        else
        {
            AppendLog(BuildHotkeyRegistrationFailure(config, pause, pauseError, stop, stopError));
        }

        // 无论 RegisterHotKey 是否成功，都启用物理键轮询兜底（全屏游戏常吞掉 WM_HOTKEY）。
        StartHotkeyPolling(config.StopHotkey, hasStartHotkey ? config.PauseHotkey : null);
    }

    private void StartHotkeyPolling(string stopKeyName, string? startKeyName)
    {
        if (_hotkeyPollTimer is not null)
        {
            _hotkeyPollTimer.Stop();
            _hotkeyPollTimer.Tick -= HotkeyPollTimer_Tick;
            _hotkeyPollTimer = null;
        }

        _stopKeyWasDown = false;
        _startKeyWasDown = false;
        _hotkeyPollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _hotkeyPollTimer.Tick += HotkeyPollTimer_Tick;
        _hotkeyPollTimer.Start();
        AppendLog(_stopHotkeyRegistered
            ? $"已启用 {stopKeyName} 物理键轮询兜底（全屏时可停）。"
            : $"系统热键占用或注册失败，改用 {stopKeyName} 物理键轮询停止（全局可用）"
                + (startKeyName is null ? "。" : $"；{startKeyName} 轮询启动。"));
    }

    private void HotkeyPollTimer_Tick(object? sender, EventArgs e)
    {
        // 焦点在快捷键输入框时，不触发启停，避免录入 F 键时误停。
        if (IsHotkeyCaptureFocused())
            return;

        if (_stopVirtualKey != 0)
        {
            bool stopDown = (GetAsyncKeyState((int)_stopVirtualKey) & 0x8000) != 0;
            if (stopDown && !_stopKeyWasDown)
                RequestStopFromHotkey("轮询");
            _stopKeyWasDown = stopDown;
        }

        if (_startVirtualKey != 0)
        {
            bool startDown = (GetAsyncKeyState((int)_startVirtualKey) & 0x8000) != 0;
            if (startDown && !_startKeyWasDown)
                RequestStartFromHotkey();
            _startKeyWasDown = startDown;
        }
    }

    private bool IsHotkeyCaptureFocused() =>
        ReferenceEquals(Keyboard.FocusedElement, PauseHotkeyBox) ||
        ReferenceEquals(Keyboard.FocusedElement, StopHotkeyBox);

    private static string BuildHotkeyRegistrationFailure(AutomationConfig config, bool pause, int pauseError, bool stop, int stopError)
    {
        var failures = new List<string>();
        if (!pause) failures.Add($"启动键 {config.PauseHotkey}（错误 {pauseError}）");
        if (!stop) failures.Add($"{config.StopHotkey}（错误 {stopError}）");
        return $"全局热键注册失败：{string.Join("、", failures)}。错误 1409 通常表示该按键已被其他程序占用；将改用物理键轮询（仍可停止）。";
    }

    private nint HandleWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey) return 0;
        handled = true;
        // Send：尽快打断运行中任务，避免排在一堆日志/截图刷新后面。
        Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (IsHotkeyCaptureFocused())
                return;
            if (wParam == PauseHotkeyId)
                RequestStartFromHotkey();
            else if (wParam == StopHotkeyId)
                RequestStopFromHotkey("热键");
        });
        return 0;
    }

    private void RequestStartFromHotkey()
    {
        // 用户可选的启动键只负责启动；运行中按下不改变当前任务。
        if (_runCancellation is not null || _isPaused)
            return;
        if (MazePipelineToggle.IsChecked == true ||
            MainQuestPipelineToggle.IsChecked == true ||
            HardMainQuestPipelineToggle.IsChecked == true ||
            DailyShopPipelineToggle.IsChecked == true ||
            DailyFreeGiftPipelineToggle.IsChecked == true ||
            DailyExercisesPipelineToggle.IsChecked == true ||
            DailySimulationTowerPipelineToggle.IsChecked == true ||
            DailyActivityPipelineToggle.IsChecked == true ||
            DailyMgArenaPipelineToggle.IsChecked == true ||
            DailyMissionsPipelineToggle.IsChecked == true ||
            DailyCirclePipelineToggle.IsChecked == true ||
            DailyHarvestPipelineToggle.IsChecked == true ||
            DailyFreeBoostPipelineToggle.IsChecked == true)
            RunPipelineButton_Click(this, new RoutedEventArgs());
        else
            RunMazeButton_Click(this, new RoutedEventArgs());
    }

    private void RequestStopFromHotkey(string source)
    {
        if (_runCancellation is null && !_isPaused)
            return;
        // 热键 + 轮询可能同时触发；已取消则不再刷日志。
        if (_runCancellation is { IsCancellationRequested: true } && !_isPaused)
            return;
        AppendLog($"收到停止（{source}），正在取消全部任务…");
        StopButton_Click(this, new RoutedEventArgs());
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is >= Key.F1 and <= Key.F12)
        {
            string name = key.ToString();
            box.Text = name;
            box.CaretIndex = name.Length;
            e.Handled = true;
            PersistHotkeysFromBoxes();
            return;
        }

        // Backspace / Delete：启动键可清空；停止键不允许空。
        if (key is Key.Back or Key.Delete)
        {
            if (ReferenceEquals(box, PauseHotkeyBox))
            {
                box.Text = "";
                e.Handled = true;
                PersistHotkeysFromBoxes();
            }
            else
            {
                e.Handled = true;
                AppendLog("停止键不能为空，请直接按 F1–F12 更换。");
            }
            return;
        }

        // 阻止普通字符输入，避免把 F10 打成文本碎片。
        if (key is Key.Tab or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        e.Handled = true;
        AppendLog("请直接按下 F1–F12 录入快捷键（不要手打字母）。");
    }

    private void PersistHotkeysFromBoxes()
    {
        string pause = PauseHotkeyBox.Text.Trim().ToUpperInvariant();
        string stop = StopHotkeyBox.Text.Trim().ToUpperInvariant();
        if ((!string.IsNullOrWhiteSpace(pause) && !AutomationConfig.IsSupportedFunctionKey(pause)) ||
            !AutomationConfig.IsSupportedFunctionKey(stop) ||
            (!string.IsNullOrWhiteSpace(pause) && pause == stop))
        {
            AppendLog("启动键可留空；已填写的快捷键须为不同的 F1–F12。已恢复原设置。");
            LoadSettings();
            return;
        }

        AutomationConfig config = ConfigStore.Load();
        if (config.PauseHotkey == pause && config.StopHotkey == stop)
            return;

        config.PauseHotkey = pause;
        config.StopHotkey = stop;
        ConfigStore.Save(config);
        RegisterConfiguredHotkeys();
        AppendLog(string.IsNullOrWhiteSpace(pause)
            ? $"快捷键已保存：停止 {stop}。"
            : $"快捷键已保存：启动 {pause}，停止 {stop}。");
    }

    private void ReleaseHotkeys()
    {
        if (_hotkeyPollTimer is not null)
        {
            _hotkeyPollTimer.Stop();
            _hotkeyPollTimer.Tick -= HotkeyPollTimer_Tick;
            _hotkeyPollTimer = null;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            UnregisterHotKey(handle, PauseHotkeyId);
            UnregisterHotKey(handle, StopHotkeyId);
        }
        _windowSource?.RemoveHook(HandleWindowMessage);
        _windowSource = null;
        _startHotkeyRegistered = false;
        _stopHotkeyRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
