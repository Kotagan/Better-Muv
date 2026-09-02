using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BetterMuv.Core;

namespace BetterMuv;

public partial class MainWindow
{
    private bool _capturingHotkey;
    private string _currentHotkey = "F10";

    private void LoadHotkeySetting()
    {
        AutomationConfig config = ConfigStore.Load();
        _currentHotkey = config.ToggleHotkey;
        HotkeyCaptureButton.Content = config.ToggleHotkey;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource.AddHook(HandleWindowMessage);
        string hotkey = ConfigStore.Load().ToggleHotkey;
        if (TryRegisterConfiguredHotkey(hotkey, out string? error))
            AppendLog($"已注册全局快捷键：{hotkey}");
        else
            AppendLog("全局快捷键注册失败：" + error);
    }

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        BeginHotkeyCapture();
    }

    private void HotkeyCaptureButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey)
            return;

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return;

        string hotkey = key.ToString();
        if (!IsSupportedHotkey(hotkey))
        {
            HotkeyCaptureButton.Content = "请按 F1–F12";
            return;
        }

        ApplyHotkeyImmediately(hotkey);
    }

    private void HotkeyCaptureButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_capturingHotkey)
            return;

        _capturingHotkey = false;
        HotkeyCaptureButton.Content = _currentHotkey;
        TryRegisterConfiguredHotkey(_currentHotkey, out _);
    }

    private void BeginHotkeyCapture()
    {
        _capturingHotkey = true;
        HotkeyCaptureButton.Content = "请按下快捷键…";
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            UnregisterHotKey(handle, HotkeyId);
        HotkeyCaptureButton.Focus();
    }

    private void ApplyHotkeyImmediately(string hotkey)
    {
        _capturingHotkey = false;
        string previous = _currentHotkey;
        AutomationConfig config = ConfigStore.Load();
        config.ToggleHotkey = hotkey;
        ConfigStore.Save(config);

        if (!TryRegisterConfiguredHotkey(hotkey, out string? error))
        {
            config.ToggleHotkey = previous;
            ConfigStore.Save(config);
            _currentHotkey = previous;
            HotkeyCaptureButton.Content = previous;
            TryRegisterConfiguredHotkey(previous, out _);
            AppendLog($"快捷键 {hotkey} 注册失败，已回退到 {previous}：{error}");
            MessageBox.Show(
                $"无法注册全局快捷键 {hotkey}，可能已被其他程序占用。\n已回退到 {previous}。",
                "快捷键",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _currentHotkey = hotkey;
        HotkeyCaptureButton.Content = hotkey;
        AppendLog($"全局快捷键已设置为：{hotkey}");
    }

    private static bool IsSupportedHotkey(string hotkey) =>
        hotkey is "F1" or "F2" or "F3" or "F4" or "F5" or "F6" or "F7" or
                  "F8" or "F9" or "F10" or "F11" or "F12";

    private bool TryRegisterConfiguredHotkey(string hotkey, out string? error)
    {
        error = null;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            error = "窗口句柄尚未就绪。";
            return false;
        }

        UnregisterHotKey(handle, HotkeyId);
        Key key = Enum.Parse<Key>(hotkey);
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (RegisterHotKey(handle, HotkeyId, 0, virtualKey))
            return true;

        int code = Marshal.GetLastWin32Error();
        error = $"Win32 错误码 {code}";
        return false;
    }

    private nint HandleWindowMessage(
        nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey || wParam != HotkeyId)
            return 0;

        handled = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (_cancellation is null)
                StartButton_Click(this, new RoutedEventArgs());
            else
                StopButton_Click(this, new RoutedEventArgs());
        });
        return 0;
    }

    private void ReleaseHotkey()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            UnregisterHotKey(handle, HotkeyId);
        _windowSource?.RemoveHook(HandleWindowMessage);
        _windowSource = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
