using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace BetterMuv.Services.ChildSession;

/// <summary>
/// 根实例 ↔ 分身内 Better-Muv：启动/停止一条龙任务。
/// 分身实例开 Named Pipe Server；根实例作为 Client 发命令。
/// </summary>
internal sealed class ChildSessionTaskBridge : IDisposable
{
    internal const string CmdStart = "START";
    internal const string CmdStop = "STOP";
    internal const string CmdStatus = "STATUS";
    internal const string ReplyIdle = "IDLE";
    internal const string ReplyBusy = "BUSY";
    internal const string ReplyOk = "OK";

    private CancellationTokenSource? _serverCts;
    private Func<string, string>? _handler;

    private static string ResolvePipeName()
    {
        string sid;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value ?? "unknown";
        }
        catch
        {
            sid = "unknown";
        }

        return $"Better-Muv.v1.user-{sid}.childTask";
    }

    internal void StartServer(Func<string, string> handler)
    {
        StopServer();
        _handler = handler;
        _serverCts = new CancellationTokenSource();
        CancellationToken token = _serverCts.Token;
        _ = Task.Run(() => ServerLoopAsync(token), token);
    }

    internal void StopServer()
    {
        try { _serverCts?.Cancel(); } catch { /* ignore */ }
        _serverCts?.Dispose();
        _serverCts = null;
        _handler = null;
    }

    internal static async Task<string> SendAsync(string command, int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await using var client = new NamedPipeClientStream(
            ".",
            ResolvePipeName(),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);
        await WriteLineAsync(client, command, cts.Token);
        return await ReadLineAsync(client, cts.Token);
    }

    internal static async Task<bool?> TryQueryBusyAsync(int timeoutMs = 800)
    {
        try
        {
            string reply = await SendAsync(CmdStatus, timeoutMs);
            if (string.Equals(reply, ReplyBusy, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(reply, ReplyIdle, StringComparison.OrdinalIgnoreCase))
                return false;
            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => StopServer();

    private async Task ServerLoopAsync(CancellationToken token)
    {
        string pipeName = ResolvePipeName();
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                string command = await ReadLineAsync(server, token);
                string reply;
                try
                {
                    Func<string, string>? handler = _handler;
                    reply = handler is null ? "ERR no handler" : handler(command);
                }
                catch (Exception ex)
                {
                    reply = "ERR " + ex.GetBaseException().Message;
                }

                await WriteLineAsync(server, reply, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(500, token); } catch { break; }
            }
        }
    }

    private static async Task WriteLineAsync(PipeStream stream, string line, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line.Trim() + "\n");
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private static async Task<string> ReadLineAsync(PipeStream stream, CancellationToken token)
    {
        var buffer = new byte[512];
        var sb = new StringBuilder();
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read <= 0)
                break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
            int nl = IndexOfNewline(sb);
            if (nl >= 0)
                return sb.ToString(0, nl).Trim();
            if (sb.Length > 2048)
                break;
        }

        return sb.ToString().Trim();
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] is '\n' or '\r')
                return i;
        }

        return -1;
    }
}
