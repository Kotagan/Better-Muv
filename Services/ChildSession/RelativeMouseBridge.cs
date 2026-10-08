using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DrawingRectangle = System.Drawing.Rectangle;

namespace BetterMuv.Services.ChildSession;

/// <summary>
/// 根实例：Raw Input 相对鼠标 → Named Pipe；
/// 分身实例：接收并 SendInput 相对移动。
/// </summary>
internal sealed class RelativeMouseBridge : IDisposable
{
    private const int InputMouse = 0;
    private const uint MouseMove = 0x0001;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort HidUsageMouse = 0x02;
    private const int RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private static readonly IntPtr HwndMessage = new(-3);

    private CancellationTokenSource? _hostCts;
    private Thread? _rawInputThread;
    private NamedPipeServerStream? _pipeServer;
    private ChildSessionService? _service;
    private volatile bool _enabled;
    private int _pendingDx;
    private int _pendingDy;
    private readonly object _deltaLock = new();

    private CancellationTokenSource? _clientCts;
    private Thread? _clientThread;

    public void StartHost(ChildSessionService service)
    {
        StopHost();
        _service = service;
        _enabled = true;
        _hostCts = new CancellationTokenSource();
        _rawInputThread = new Thread(() => HostLoop(_hostCts.Token))
        {
            IsBackground = true,
            Name = "Better-Muv-RelativeMouse-Host"
        };
        _rawInputThread.SetApartmentState(ApartmentState.STA);
        _rawInputThread.Start();
    }

    public void StopHost()
    {
        _enabled = false;
        _hostCts?.Cancel();
        try { _pipeServer?.Dispose(); } catch { /* ignore */ }
        _pipeServer = null;
        _hostCts?.Dispose();
        _hostCts = null;
        ReleaseCursorClip();
    }

    public void StartClient()
    {
        StopClient();
        _clientCts = new CancellationTokenSource();
        _clientThread = new Thread(() => ClientLoop(_clientCts.Token))
        {
            IsBackground = true,
            Name = "Better-Muv-RelativeMouse-Client"
        };
        _clientThread.Start();
    }

    public void StopClient()
    {
        _clientCts?.Cancel();
        _clientCts?.Dispose();
        _clientCts = null;
    }

    public void Dispose()
    {
        StopHost();
        StopClient();
    }

    private void HostLoop(CancellationToken token)
    {
        try
        {
            using var hwndSource = new System.Windows.Interop.HwndSource(
                0, 0, 0, 0, 0, 0, 0, "Better-Muv-RelativeMouse", HwndMessage);
            hwndSource.AddHook(WndProc);

            var device = new RAWINPUTDEVICE
            {
                usUsagePage = HidUsagePageGeneric,
                usUsage = HidUsageMouse,
                dwFlags = RidevInputSink,
                hwndTarget = hwndSource.Handle
            };
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                return;

            _ = Task.Run(() => FlushLoop(token), token);
            _ = Task.Run(() => AcceptPipeLoop(token), token);

            var frame = new DispatcherFrame();
            using (token.Register(() => frame.Continue = false))
                Dispatcher.PushFrame(frame);

            device.dwFlags = RidevRemove;
            device.hwndTarget = IntPtr.Zero;
            _ = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch
        {
            // 宿主线程异常不影响主程序
        }
        finally
        {
            ReleaseCursorClip();
        }
    }

    private async Task AcceptPipeLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    AppInstance.RelativeMousePipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                _pipeServer = server;
                await server.WaitForConnectionAsync(token);
                while (!token.IsCancellationRequested && server.IsConnected)
                    await Task.Delay(200, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(500, CancellationToken.None);
            }
            finally
            {
                try { server?.Dispose(); } catch { /* ignore */ }
                if (ReferenceEquals(_pipeServer, server))
                    _pipeServer = null;
            }
        }
    }

    private async Task FlushLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(10, token);
            int dx, dy;
            lock (_deltaLock)
            {
                dx = _pendingDx;
                dy = _pendingDy;
                _pendingDx = 0;
                _pendingDy = 0;
            }

            if (dx == 0 && dy == 0)
                continue;

            NamedPipeServerStream? pipe = _pipeServer;
            if (pipe is null || !pipe.IsConnected)
                continue;

            try
            {
                var payload = new byte[8];
                BitConverter.TryWriteBytes(payload.AsSpan(0, 4), dx);
                BitConverter.TryWriteBytes(payload.AsSpan(4, 4), dy);
                await pipe.WriteAsync(payload, token);
                await pipe.FlushAsync(token);
            }
            catch
            {
                // 客户端断开时下一轮重建
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmInput = 0x00FF;
        if (msg != wmInput || !_enabled)
            return IntPtr.Zero;

        if ((GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0)
        {
            ReleaseCursorClip();
            return IntPtr.Zero;
        }

        ChildSessionService? service = _service;
        if (service is null || !service.TryGetRelativeMouseCaptureBounds(out DrawingRectangle bounds))
        {
            ReleaseCursorClip();
            return IntPtr.Zero;
        }

        ApplyCursorClip(bounds);

        uint size = 0;
        _ = GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0)
            return IntPtr.Zero;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, buffer, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != size)
                return IntPtr.Zero;

            var input = Marshal.PtrToStructure<RAWINPUT>(buffer);
            if (input.header.dwType != 0)
                return IntPtr.Zero;
            if ((input.mouse.usFlags & 0x01) != 0) // MOUSE_MOVE_ABSOLUTE
                return IntPtr.Zero;

            lock (_deltaLock)
            {
                _pendingDx += input.mouse.lLastX;
                _pendingDy += input.mouse.lLastY;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return IntPtr.Zero;
    }

    private async void ClientLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    AppInstance.RelativeMousePipeName,
                    PipeDirection.In,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(3000, token);
                var buffer = new byte[8];
                while (!token.IsCancellationRequested)
                {
                    int read = await client.ReadAsync(buffer.AsMemory(0, 8), token);
                    if (read < 8)
                        break;
                    int dx = BitConverter.ToInt32(buffer, 0);
                    int dy = BitConverter.ToInt32(buffer, 4);
                    if (dx != 0 || dy != 0)
                        SendRelativeMove(dx, dy);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(1000, token); } catch { break; }
            }
        }
    }

    private static void SendRelativeMove(int dx, int dy)
    {
        var input = new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    dwFlags = MouseMove
                }
            }
        };
        _ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void ApplyCursorClip(DrawingRectangle bounds)
    {
        var rect = new NativeRect
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Right,
            Bottom = bounds.Bottom
        };
        _ = ClipCursor(ref rect);
        while (ShowCursor(false) >= 0) { }
    }

    private static void ReleaseCursorClip()
    {
        _ = ReleaseClipCursor(IntPtr.Zero);
        while (ShowCursor(true) < 0) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        public uint ulButtons;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    private static extern bool ReleaseClipCursor(IntPtr rect);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
