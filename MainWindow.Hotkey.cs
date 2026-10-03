using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using BetterMuv.Core;

namespace BetterMuv;

public partial class MainWindow
{
    private const int PauseHotkeyId = 0x4D55;
    private const int StopHotkeyId = 0x4D56;
    private const int WmHotkey = 0x0312;
    private HwndSource? _windowSource;

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
        bool pause = !hasStartHotkey || RegisterHotKey(handle, PauseHotkeyId, 0,
            (uint)KeyInterop.VirtualKeyFromKey(Enum.Parse<Key>(config.PauseHotkey)));
        int pauseError = pause ? 0 : Marshal.GetLastWin32Error();
        bool stop = RegisterHotKey(handle, StopHotkeyId, 0, (uint)KeyInterop.VirtualKeyFromKey(Enum.Parse<Key>(config.StopHotkey)));
        int stopError = stop ? 0 : Marshal.GetLastWin32Error();
        AppendLog(pause && stop
            ? hasStartHotkey
                ? $"已注册热键：{config.PauseHotkey} 启动，{config.StopHotkey} 停止。"
                : $"已注册热键：{config.StopHotkey} 停止；启动热键未设置。"
            : BuildHotkeyRegistrationFailure(config, pause, pauseError, stop, stopError));
    }

    private static string BuildHotkeyRegistrationFailure(AutomationConfig config, bool pause, int pauseError, bool stop, int stopError)
    {
        var failures = new List<string>();
        if (!pause) failures.Add($"启动键 {config.PauseHotkey}（错误 {pauseError}）");
        if (!stop) failures.Add($"{config.StopHotkey}（错误 {stopError}）");
        return $"全局热键注册失败：{string.Join("、", failures)}。错误 1409 通常表示该按键已被其他程序占用；Windows 不提供占用进程名称。";
    }

    private nint HandleWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey) return 0;
        handled = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (wParam == PauseHotkeyId)
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
                         DailyMissionsPipelineToggle.IsChecked == true ||
                         DailyCirclePipelineToggle.IsChecked == true ||
                         DailyHarvestPipelineToggle.IsChecked == true ||
                         DailyFreeBoostPipelineToggle.IsChecked == true)
                    RunPipelineButton_Click(this, new RoutedEventArgs());
                else
                    RunMazeButton_Click(this, new RoutedEventArgs());
            }
            else if (wParam == StopHotkeyId)
            {
                StopButton_Click(this, new RoutedEventArgs());
            }
        });
        return 0;
    }

    private void ReleaseHotkeys()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            UnregisterHotKey(handle, PauseHotkeyId);
            UnregisterHotKey(handle, StopHotkeyId);
        }
        _windowSource?.RemoveHook(HandleWindowMessage);
        _windowSource = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hwnd, int id);
}
