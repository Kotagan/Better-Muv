using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using BetterMuv.Core;
using BetterMuv;

namespace BetterMuv.Services.ChildSession;

public sealed class ChildSessionService : IDisposable
{
    private static readonly DrawingSize DefaultDesktopSize = new(1920, 1080);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);
    private const int InitialConnectionRetryCount = 3;
    private const int RdpSettingsReconnectRetryCount = 2;
    private const int ErrorTimeout = 1460;

    private readonly ChildSessionConfig _config;
    private readonly DispatcherTimer _statusTimer;
    private readonly SemaphoreSlim _launchSemaphore = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();

    private ChildSessionWindow? _desktopWindow;
    private bool _autoLaunchBetterMuvPending;
    private TaskCompletionSource<bool>? _connectionAttemptCompletionSource;
    private ChildSessionConnectionFailedEventArgs? _lastConnectionFailure;
    private int _initialConnectionRetriesRemaining;
    private bool _connectionRetryInProgress;
    private RdpSettingChange _pendingRdpSettingChanges;
    private int _rdpSettingsReconnectRetriesRemaining;
    private bool _rdpSettingsReconnectRetryInProgress;
    private bool _statusTickInProgress;
    private bool _disposed;
    private string? _lastOperationMessage;

    public event EventHandler? StateChanged;

    public event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    public event EventHandler? SystemShortcutsReconnectCompleted;

    public event EventHandler? AudioReconnectCompleted;

    public string StatusText { get; private set; } = "桌面分身尚未启动";

    public bool IsDesktopVisible => _desktopWindow?.IsVisible == true;

    public int ConnectedState { get; private set; }

    public uint? ChildSessionId { get; private set; }

    public bool SendSystemShortcutsToRemote { get; private set; } = true;

    public bool TopmostEnabled => _config.TopmostEnabled;

    public bool SmartSizingEnabled => _config.SmartSizingEnabled;

    public bool KeepAspectRatio => _config.KeepAspectRatio;

    public bool AudioMuted => _config.AudioMuted;

    public WindowPositionConfig? NormalWindowPosition
    {
        get => _config.NormalWindowPosition;
        set => _config.NormalWindowPosition = value;
    }

    public WindowPositionConfig? SmallWindowPosition
    {
        get => _config.SmallWindowPosition;
        set => _config.SmallWindowPosition = value;
    }

    public bool IsRdpWrapperEnabled()
    {
        return ChildSessionNativeMethods.IsRdpWrapperEnabled();
    }

    public bool HasActiveChildSession()
    {
        if (!AppInstance.IsRoot)
            return false;
        RefreshState();
        return ChildSessionId is not null;
    }

    private readonly Action<string>? _log;
    private ChildSessionLoginCredentials? _pendingLoginCredentials;
    private static bool _termServiceHealedThisProcess;

    public ChildSessionService(Action<string>? log = null)
    {
        _log = log;
        AutomationConfig automationConfig = ConfigStore.Load();
        _config = automationConfig.ChildSession ?? new ChildSessionConfig();
        automationConfig.ChildSession = _config;
        SendSystemShortcutsToRemote = _config.SendSystemShortcutsToRemote;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
        RefreshState();
    }

    private void Log(string message) => _log?.Invoke(message);

    private void PersistConfig()
    {
        AutomationConfig automationConfig = ConfigStore.Load();
        automationConfig.ChildSession = _config;
        ConfigStore.Save(automationConfig);
    }

    /// <param name="temporarilyDisableRdpWrapper">
    /// 若本机启用了 RDP Wrapper，临时切回原生 termsrv.dll（关闭分身时自动还原）。
    /// </param>
    public async Task StartAsync(
        bool temporarilyDisableRdpWrapper = false,
        ChildSessionLoginCredentials? loginCredentials = null)
    {
        ThrowIfDisposed();
        Log("桌面分身：开始连接 Child Session / RDP");
        if (temporarilyDisableRdpWrapper && ChildSessionNativeMethods.IsRdpWrapperEnabled())
        {
            await Task.Run(() => RdpWrapperSwitch.TemporarilyUseNativeTermService(Log));
            RefreshState("已临时禁用 RDP Wrapper，准备连接桌面分身");
        }
        else if (ChildSessionNativeMethods.IsRdpWrapperEnabled())
        {
            Log("桌面分身警告：检测到 RDP Wrapper，未切换时极易出现错误 516");
        }

        // 已有分身会话则复用（保留游戏登录）；仅在没有会话时才新建，避免每次登录。
        uint? existingChildSessionId = await Task.Run(() =>
        {
            try
            {
                return ChildSessionNativeMethods.TryGetChildSessionId();
            }
            catch
            {
                return null;
            }
        });
        if (existingChildSessionId is not null)
        {
            Log($"桌面分身：复用已有会话 {existingChildSessionId.Value}（游戏登录状态会保留）");
        }

        // 探测证实：TermService 卡住时会一直 Connecting 直到超时；管理员下重启一次即可 LoginComplete。
        // 仅在无已有会话时重启，避免打断正在挂机的分身。
        if (existingChildSessionId is null
            && !_termServiceHealedThisProcess
            && Services.ElevationHelper.IsElevated())
        {
            _termServiceHealedThisProcess = true;
            Log("桌面分身：重启 TermService 以清除卡住的 Child Session 状态");
            await Task.Run(() => RdpWrapperSwitch.TryRestartTermService(Log));
        }

        _pendingLoginCredentials = loginCredentials;

        EnsureChildSessionsEnabled();
        RefreshState();

        if (ConnectedState == 1)
        {
            return;
        }

        // 分身登录会再次跑当前用户开机启动项；连接前临时禁用，登录完成后立刻还原。
        await Task.Run(() => ChildSessionStartupSuppressor.Suppress(Log));

        var completionSource = _connectionAttemptCompletionSource;
        if (completionSource is null || completionSource.Task.IsCompleted)
        {
            completionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionAttemptCompletionSource = completionSource;
            _lastConnectionFailure = null;

            try
            {
                ConnectCore("正在启动 Better-Muv 桌面分身");
            }
            catch
            {
                _autoLaunchBetterMuvPending = false;
                _initialConnectionRetriesRemaining = 0;
                completionSource.TrySetResult(false);
                _connectionAttemptCompletionSource = null;
                try { ChildSessionStartupSuppressor.Restore(Log); } catch { /* ignore */ }
                throw;
            }
        }

        try
        {
            await completionSource.Task.WaitAsync(
                ConnectionTimeout,
                _disposeCancellationTokenSource.Token);
        }
        catch (TimeoutException)
        {
            if (!completionSource.Task.IsCompleted)
            {
                _autoLaunchBetterMuvPending = false;
                _initialConnectionRetriesRemaining = 0;
                CompleteConnectionFailure(CreateConnectionTimeoutFailure());
                TryDisconnectRdpHost();
            }
        }
        finally
        {
            if (ReferenceEquals(_connectionAttemptCompletionSource, completionSource))
            {
                _connectionAttemptCompletionSource = null;
            }
        }
    }

    public void ShowWindow()
    {
        ThrowIfDisposed();
        ShowDesktopWindow(EnsureDesktopWindow());
        RefreshState();
    }

    /// <summary>打开分身窗口并立即开始连接（主页「桌面分身」入口）。</summary>
    public async Task ShowWindowAndStartAsync()
    {
        ThrowIfDisposed();
        ChildSessionWindow window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        RefreshState();
        await window.StartSessionAsync();
    }

    public void HideWindow()
    {
        ThrowIfDisposed();
        _desktopWindow?.Hide();
        RefreshState("已隐藏 Better-Muv 桌面分身，RDP 连接保持不变");
    }

    public void ShowChildSessionDesktop()
    {
        ThrowIfDisposed();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendShowDesktopShortcut();
        RefreshState("已向 Better-Muv 桌面分身发送 Win+D");
    }

    public void ShowChildSessionTaskView()
    {
        ThrowIfDisposed();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendTaskViewShortcut();
        RefreshState("已向 Better-Muv 桌面分身发送 Win+Tab");
    }

    public void SetSmartSizing(bool enabled)
    {
        ThrowIfDisposed();
        EnsureDesktopWindow().RdpHost.SetSmartSizing(enabled);
        _config.SmartSizingEnabled = enabled;
        PersistConfig();
        RefreshState(enabled ? "窗口显示模式已切换为自适应" : "窗口显示模式已切换为 1:1");
    }

    public void SetKeepAspectRatio(bool enabled)
    {
        ThrowIfDisposed();
        _config.KeepAspectRatio = enabled;
        PersistConfig();
        RefreshState(enabled ? "桌面分身窗口将保持宽高比" : "桌面分身窗口不再保持宽高比");
    }

    public void SetTopmost(bool enabled)
    {
        ThrowIfDisposed();
        _config.TopmostEnabled = enabled;
        PersistConfig();
        RefreshState(enabled ? "桌面分身窗口已置顶" : "桌面分身窗口已取消置顶");
    }

    public bool SetSendSystemShortcutsToRemote(bool enabled)
    {
        ThrowIfDisposed();
        if (SendSystemShortcutsToRemote == enabled)
        {
            return false;
        }

        SendSystemShortcutsToRemote = enabled;
        _config.SendSystemShortcutsToRemote = enabled;
        PersistConfig();
        var window = EnsureDesktopWindow();
        window.RdpHost.SetSendSystemShortcutsToRemote(enabled);
        var target = enabled ? "桌面分身" : "本机";
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.SystemShortcuts,
            $"系统组合键已改为在{target}生效");
    }

    public bool SetAudioMuted(bool muted)
    {
        ThrowIfDisposed();
        if (AudioMuted == muted)
        {
            return false;
        }

        _config.AudioMuted = muted;
        PersistConfig();
        var window = EnsureDesktopWindow();
        window.RdpHost.SetAudioMuted(muted);
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.Audio,
            muted ? "桌面分身声音已关闭" : "桌面分身声音已开启");
    }

    public Task LaunchBetterMuvAsync()
    {
        ThrowIfDisposed();
        return LaunchBetterMuvCoreAsync(isAutomatic: false);
    }

    public bool IsCursorOverRdpClient()
    {
        if (_desktopWindow?.IsVisible != true || _desktopWindow.RdpHost is not { IsHandleCreated: true } rdpHost)
            return false;
        if (rdpHost.ConnectedState != 1)
            return false;

        DrawingRectangle bounds = rdpHost.RectangleToScreen(rdpHost.ClientRectangle);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        System.Drawing.Point pos = System.Windows.Forms.Control.MousePosition;
        return bounds.Contains(pos);
    }

    /// <summary>
    /// 仅当鼠标在分身画面内时，把键盘交给 RDP；鼠标在外面则收回，避免按键误进分身。
    /// </summary>
    public void SyncKeyboardFocusToMouse()
    {
        if (_disposed || _desktopWindow is null || !_desktopWindow.IsVisible || !_desktopWindow.IsActive)
            return;
        if (_desktopWindow.RdpHost is not { IsHandleCreated: true } rdpHost || rdpHost.ConnectedState != 1)
            return;

        var helper = new System.Windows.Interop.WindowInteropHelper(_desktopWindow);
        if (IsCursorOverRdpClient())
        {
            if (!rdpHost.IsInputWindowFocused())
                _ = ChildSessionNativeMethods.TryFocusRdpInputWindow(rdpHost.Handle);
        }
        else if (rdpHost.IsInputWindowFocused())
        {
            ChildSessionNativeMethods.ReleaseRdpInputFocus(helper.Handle);
        }
    }

    public async Task LaunchExecutableAsync(string executablePath)
    {
        ThrowIfDisposed();
        var childSessionId = GetRequiredChildSessionId();

        await _launchSemaphore.WaitAsync();
        try
        {
            RefreshState($"正在以管理员权限启动 {System.IO.Path.GetFileName(executablePath)}");
            await ChildSessionProcessLauncher.LaunchElevatedAsync(childSessionId, executablePath);
            RefreshState(
                $"已在桌面分身（会话 {childSessionId}）中以管理员权限启动 {System.IO.Path.GetFileName(executablePath)}");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    /// <summary>只断开 RDP 画面并隐藏窗口，保留 Child Session 与其中游戏登录。</summary>
    public async Task DisconnectAndHideAsync()
    {
        ThrowIfDisposed();
        _autoLaunchBetterMuvPending = false;
        _initialConnectionRetriesRemaining = 0;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;
        _connectionAttemptCompletionSource?.TrySetResult(false);

        await _launchSemaphore.WaitAsync();
        try
        {
            TryDisconnectRdpHost();
            _desktopWindow?.Hide();
            await Task.Run(() => ChildSessionStartupSuppressor.Restore(Log));
            uint? sessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            RefreshState(sessionId is null
                ? "已断开画面；当前没有可复用的分身会话"
                : $"已断开画面，分身会话 {sessionId.Value} 仍在运行（游戏登录保留）");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    /// <summary>注销 Child Session（最多等待约 15 秒）。调用方负责随后关闭窗口。</summary>
    public async Task LogoffAndHideAsync()
    {
        ThrowIfDisposed();
        _autoLaunchBetterMuvPending = false;
        _initialConnectionRetriesRemaining = 0;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;
        _connectionAttemptCompletionSource?.TrySetResult(false);

        await _launchSemaphore.WaitAsync();
        try
        {
            TryDisconnectRdpHost();
            RefreshState("正在断开 RDP 并注销 Better-Muv 桌面分身…");

            // wait:false，避免 WTSLogoffSession 永久卡住导致窗口关不掉。
            uint? terminatedSessionId = await Task.Run(() =>
                ChildSessionNativeMethods.TerminateChildSession(wait: false));

            for (int i = 0; i < 30; i++)
            {
                if (ChildSessionNativeMethods.TryGetChildSessionId() is null)
                    break;
                await Task.Delay(500);
            }

            await Task.Run(() =>
            {
                ChildSessionStartupSuppressor.Restore(Log);
                RdpWrapperSwitch.RestoreIfNeeded(Log);
            });
            ChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            RefreshState(ChildSessionId is null
                ? (terminatedSessionId is null
                    ? "当前没有桌面分身会话"
                    : $"桌面分身会话 {terminatedSessionId.Value} 已注销")
                : $"已请求注销分身会话，仍可能残留（会话 {ChildSessionId.Value}）");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    public void RefreshState(string? operationMessage = null)
    {
        if (_disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(operationMessage))
        {
            _lastOperationMessage = operationMessage;
        }

        try
        {
            var enabled = ChildSessionNativeMethods.IsChildSessionsEnabled();
            ChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            ConnectedState = _desktopWindow?.RdpHost.ConnectedState ?? 0;

            var connectionText = ConnectedState switch
            {
                0 => "未连接",
                1 => "已连接",
                2 => "正在连接",
                _ => $"未知连接状态 {ConnectedState}"
            };
            var sessionText = ChildSessionId?.ToString() ?? "无";
            var mainText = _lastOperationMessage ?? connectionText;

            StatusText =
                $"{mainText} | RDP：{connectionText} | 桌面分身会话：{sessionText} | 功能已启用：{enabled}";
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            StatusText = exception.GetBaseException().Message;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _disposeCancellationTokenSource.Cancel();
        _autoLaunchBetterMuvPending = false;
        _connectionAttemptCompletionSource?.TrySetCanceled();
        _connectionAttemptCompletionSource = null;
        _initialConnectionRetriesRemaining = 0;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;

        if (_desktopWindow is not null)
        {
            _desktopWindow.IsVisibleChanged -= OnDesktopWindowVisibilityChanged;
            _desktopWindow.RdpHost.ConnectionFailed -= OnRdpConnectionFailed;
            _desktopWindow.RdpHost.LoginCompleted -= OnRdpLoginCompleted;
            TryDisconnectRdpHost();
        }

        if (AppInstance.IsRoot)
        {
            try
            {
                _ = ChildSessionNativeMethods.TerminateChildSession(wait: false);
            }
            catch (Exception exception) when (IsExpectedChildSessionException(exception))
            {
                // 应用正在退出，Child Session 清理失败不应阻止主程序关闭。
            }
            try
            {
                ChildSessionStartupSuppressor.Restore(Log);
            }
            catch
            {
                // 退出时还原启动项失败不阻止关闭
            }
            try
            {
                RdpWrapperSwitch.RestoreIfNeeded(Log);
            }
            catch
            {
                // 退出时还原失败不阻止关闭
            }
        }

        if (_desktopWindow is not null)
        {
            _desktopWindow.AllowClose = true;
            _desktopWindow.Close();
            _desktopWindow = null;
        }

        _launchSemaphore.Dispose();
        _disposeCancellationTokenSource.Dispose();
    }

    private void ConnectCore(string operationMessage)
    {
        var existingSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);

        _autoLaunchBetterMuvPending = existingSessionId is null;
        _initialConnectionRetriesRemaining = _autoLaunchBetterMuvPending
            ? InitialConnectionRetryCount
            : 0;
        if (_pendingLoginCredentials is not null)
            window.RdpHost.SetLoginCredentials(_pendingLoginCredentials);

        bool reuseLoggedOnSession = existingSessionId is not null;
        int state = window.RdpHost.ConnectedState;
        // 0=断开 1=已连接 2=连接中。卡在 Connecting 时必须先断开再连，否则会空等超时。
        if (state == 1)
        {
            RefreshState(operationMessage);
            return;
        }

        if (state != 0)
        {
            Log($"桌面分身：RDP 状态为 {state}，先断开再连接"
                + (reuseLoggedOnSession ? $"（复用会话 {existingSessionId}）" : string.Empty));
            window.RdpHost.ReconnectToChildSession(
                DefaultDesktopSize,
                sessionAlreadyLoggedOn: reuseLoggedOnSession);
        }
        else
        {
            window.RdpHost.ConnectToChildSession(
                DefaultDesktopSize,
                sessionAlreadyLoggedOn: reuseLoggedOnSession);
        }

        RefreshState(operationMessage);
    }

    private ChildSessionWindow EnsureDesktopWindow()
    {
        if (_desktopWindow is not null)
        {
            return _desktopWindow;
        }

        _desktopWindow = new ChildSessionWindow(this);
        _desktopWindow.IsVisibleChanged += OnDesktopWindowVisibilityChanged;
        _desktopWindow.RdpHost.ConnectionFailed += OnRdpConnectionFailed;
        _desktopWindow.RdpHost.LoginCompleted += OnRdpLoginCompleted;
        _desktopWindow.RdpHost.SetSmartSizing(_config.SmartSizingEnabled);
        _desktopWindow.RdpHost.SetSendSystemShortcutsToRemote(SendSystemShortcutsToRemote);
        _desktopWindow.RdpHost.SetAudioMuted(AudioMuted);
        return _desktopWindow;
    }

    private static void ShowDesktopWindow(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_statusTickInProgress || _disposed)
        {
            return;
        }

        _statusTickInProgress = true;
        try
        {
            RefreshState();
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState(exception.GetBaseException().Message);
        }
        finally
        {
            _statusTickInProgress = false;
        }
    }

    private async Task LaunchBetterMuvCoreAsync(bool isAutomatic)
    {
        await _launchSemaphore.WaitAsync();
        try
        {
            var childSessionId = GetRequiredChildSessionId();
            RefreshState(isAutomatic
                ? "桌面分身已加载，正在自动以管理员权限启动 Better-Muv"
                : "正在以管理员权限启动 Better-Muv");
            await ChildSessionProcessLauncher.LaunchBetterMuvAsync(childSessionId);
            RefreshState(
                $"已在桌面分身（会话 {childSessionId}）中以管理员权限启动 Better-Muv");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    private uint GetRequiredChildSessionId()
    {
        var childSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (childSessionId is null)
        {
            throw new InvalidOperationException("当前没有可用的桌面分身，请先启动桌面分身。");
        }

        return childSessionId.Value;
    }

    private void EnsureChildSessionsEnabled()
    {
        if (!ChildSessionNativeMethods.IsChildSessionsEnabled())
        {
            ChildSessionNativeMethods.EnableChildSessions();
        }
    }

    private void OnDesktopWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RefreshState();
    }

    private void OnRdpConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        _lastConnectionFailure = e;

        if (_pendingRdpSettingChanges != RdpSettingChange.None)
        {
            if (_rdpSettingsReconnectRetryInProgress)
            {
                return;
            }

            if (_rdpSettingsReconnectRetriesRemaining > 0)
            {
                RetryRdpSettingsReconnectAsync(e);
                return;
            }

            _pendingRdpSettingChanges = RdpSettingChange.None;
        }

        // 断开原因 4 / 516 重试通常无效，直接失败，避免空等 60 秒超时。
        bool shouldRetryInitial = _autoLaunchBetterMuvPending
            && _initialConnectionRetriesRemaining > 0
            && !_connectionRetryInProgress
            && e.ErrorCode is not 4 and not 516 and not 0x204;

        if (shouldRetryInitial)
        {
            RetryInitialConnectionAsync(e);
            return;
        }

        CompleteConnectionFailure(e);
    }

    // RDP 报告连接成功后稍作等待，让新 Child Session 的桌面初始化完成。
    // 使用 OnLoginComplete 等待实际登录完成，避免依赖固定时长的延时。
    private async void OnRdpLoginCompleted(object? sender, EventArgs e)
    {
        _lastConnectionFailure = null;
        _initialConnectionRetriesRemaining = 0;
        _connectionRetryInProgress = false;
        _connectionAttemptCompletionSource?.TrySetResult(true);

        // 登录已完成，Explorer 已按抑制策略跳过启动项；立刻还原父会话的启动配置。
        _ = Task.Run(() => ChildSessionStartupSuppressor.Restore(Log));

        var completedSettingChanges = _pendingRdpSettingChanges;
        if (completedSettingChanges != RdpSettingChange.None)
        {
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _rdpSettingsReconnectRetryInProgress = false;
            RefreshState("自动重新连接完成，RDP 设置已生效");

            if (completedSettingChanges.HasFlag(RdpSettingChange.SystemShortcuts))
            {
                SystemShortcutsReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }

            if (completedSettingChanges.HasFlag(RdpSettingChange.Audio))
            {
                AudioReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            RefreshState("桌面分身登录初始化完成");
        }

        if (_autoLaunchBetterMuvPending)
        {
            _autoLaunchBetterMuvPending = false;
            await Task.Yield();
            if (_disposed)
            {
                return;
            }

            try
            {
                RefreshState();
                if (ConnectedState == 1 && ChildSessionId is not null)
                {
                    await LaunchBetterMuvCoreAsync(isAutomatic: true);
                }
            }
            catch (Exception exception) when (IsExpectedChildSessionException(exception))
            {
                RefreshState($"自动启动 Better-Muv 失败：{exception.GetBaseException().Message}");
            }
        }

        // 分身就绪后关掉主界面，只留分身观看窗 + 分身内的 Better-Muv。
        TryCloseRootMainWindow();
    }

    private void TryCloseRootMainWindow()
    {
        if (!AppInstance.IsRoot || _disposed)
            return;

        try
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_desktopWindow is not null)
                        System.Windows.Application.Current.MainWindow = _desktopWindow;

                    foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
                    {
                        if (window is global::BetterMuv.MainWindow main)
                        {
                            main.AllowCloseWhileChildSessionActive = true;
                            main.Close();
                            break;
                        }
                    }
                }
                catch
                {
                    // 关主窗失败不影响分身
                }
            });
        }
        catch
        {
            // ignore
        }
    }

    private async void RetryRdpSettingsReconnectAsync(
        ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _rdpSettingsReconnectRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = RdpSettingsReconnectRetryCount
                          - _rdpSettingsReconnectRetriesRemaining
                          + 1;
        _rdpSettingsReconnectRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << retryNumber);
        RefreshState(
            $"RDP 自动重连暂未成功，{retryDelay.TotalSeconds:0} 秒后重试"
            + $"（{retryNumber}/{RdpSettingsReconnectRetryCount}）");

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed || _pendingRdpSettingChanges == RdpSettingChange.None)
            {
                return;
            }

            RefreshState(
                $"正在重试 RDP 自动连接（{retryNumber}/{RdpSettingsReconnectRetryCount}）");
            _rdpSettingsReconnectRetryInProgress = false;
            EnsureDesktopWindow().RdpHost.ReconnectToChildSession(
                DefaultDesktopSize,
                sessionAlreadyLoggedOn: ChildSessionNativeMethods.TryGetChildSessionId() is not null);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                $"RDP 自动重新连接失败：{actualException.Message}",
                errorCode);
        }
        finally
        {
            _rdpSettingsReconnectRetryInProgress = false;
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    private async void RetryInitialConnectionAsync(ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _connectionRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = InitialConnectionRetryCount - _initialConnectionRetriesRemaining + 1;
        _initialConnectionRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << (retryNumber - 1));
        RefreshState(
            $"桌面分身首次初始化尚未完成，{retryDelay.TotalSeconds:0} 秒后自动重试"
            + $"（{retryNumber}/{InitialConnectionRetryCount}）");

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed || !_autoLaunchBetterMuvPending)
            {
                return;
            }

            RefreshState();
            if (_desktopWindow is null)
            {
                return;
            }

            RefreshState(
                $"正在重试桌面分身连接（{retryNumber}/{InitialConnectionRetryCount}）");
            _connectionRetryInProgress = false;
            // OnLogonError 触发后 ActiveX 可能仍处于连接状态，必须先断开再重连。
            // 否则这里直接退出会丢失已捕获的真实错误，最终只剩外层连接超时。
            _desktopWindow.RdpHost.ReconnectToChildSession(
                DefaultDesktopSize,
                sessionAlreadyLoggedOn: ChildSessionNativeMethods.TryGetChildSessionId() is not null);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                $"重试桌面分身连接失败：{actualException.Message}",
                errorCode);
        }
        finally
        {
            _connectionRetryInProgress = false;
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    private void CompleteConnectionFailure(ChildSessionConnectionFailedEventArgs e)
    {
        _lastConnectionFailure = e;
        _autoLaunchBetterMuvPending = false;
        _initialConnectionRetriesRemaining = 0;
        _connectionAttemptCompletionSource?.TrySetResult(false);
        _ = Task.Run(() => ChildSessionStartupSuppressor.Restore(Log));
        string oneLine = e.Message.Replace("\r\n", " | ").Replace('\n', ' ');
        Log($"桌面分身连接失败：code={e.ErrorCode} ext={e.ExtendedErrorCode?.ToString() ?? "-"} | {oneLine}");
        RefreshState(e.Message);
        ConnectionFailed?.Invoke(this, e);
    }

    private ChildSessionConnectionFailedEventArgs CreateConnectionTimeoutFailure()
    {
        var timeoutMessage =
            $"桌面分身连接及登录初始化未能在 {ConnectionTimeout.TotalSeconds:0} 秒内完成。";
        var lastDiagnostic =
            _desktopWindow?.RdpHost.LastConnectionDiagnostic
            ?? _lastConnectionFailure;
        if (lastDiagnostic is null)
        {
            return new ChildSessionConnectionFailedEventArgs(
                timeoutMessage
                + "\n\nRDP ActiveX 未报告更具体的失败原因。"
                + "\n若多次卡住：请以管理员运行，程序会自动重启 TermService；仍不行请重启电脑。",
                ErrorTimeout);
        }

        return new ChildSessionConnectionFailedEventArgs(
            $"{timeoutMessage}\n\nRDP ActiveX 最后报告：\n{lastDiagnostic.Message}",
            lastDiagnostic.ErrorCode,
            lastDiagnostic.ExtendedErrorCode);
    }

    private void TryDisconnectRdpHost()
    {
        try
        {
            _desktopWindow?.RdpHost.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException)
        {
            // ActiveX 正在自行断开时可能返回 COM 错误，仍可继续注销 Child Session。
        }
    }

    private static bool IsExpectedChildSessionException(Exception exception)
    {
        return exception is Win32Exception
            or COMException
            or EntryPointNotFoundException
            or TargetInvocationException
            or InvalidCastException
            or InvalidOperationException
            or FileNotFoundException
            or ArgumentException;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool ReconnectForRdpSettingChange(
        ChildSessionWindow window,
        RdpSettingChange settingChange,
        string operationMessage)
    {
        RefreshState();
        if (window.RdpHost.ConnectedState == 0 && ChildSessionId is null)
        {
            RefreshState($"{operationMessage}，将在下次 RDP 连接后应用");
            return false;
        }

        var previousSettingChanges = _pendingRdpSettingChanges;
        _pendingRdpSettingChanges |= settingChange;
        _rdpSettingsReconnectRetriesRemaining = RdpSettingsReconnectRetryCount;
        try
        {
            window.RdpHost.ReconnectToChildSession(DefaultDesktopSize);
        }
        catch
        {
            _pendingRdpSettingChanges = previousSettingChanges;
            if (_pendingRdpSettingChanges == RdpSettingChange.None)
            {
                _rdpSettingsReconnectRetriesRemaining = 0;
            }

            throw;
        }

        RefreshState($"{operationMessage}，正在自动重新连接 RDP");
        return true;
    }

    [Flags]
    private enum RdpSettingChange
    {
        None = 0,
        SystemShortcuts = 1,
        Audio = 2
    }
}
