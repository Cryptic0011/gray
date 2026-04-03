using System.IO.Pipes;
using System.Text;

namespace Gmux.Core.Ipc;

/// <summary>
/// Named pipe server for CLI-to-App IPC communication.
/// </summary>
public class PipeServer : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public delegate Task<IpcMessage> MessageHandler(IpcMessage message);
    public event MessageHandler? OnMessage;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = ListenLoop(_cts.Token);
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    IpcMessage.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                // Handle connection on background task
                _ = HandleConnectionAsync(server, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                // Log and retry
                await Task.Delay(100, ct);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            using (server)
            {
                using var reader = new StreamReader(server, Encoding.UTF8);
                using var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) return;

                var message = IpcMessage.Deserialize(line);
                if (message == null) return;

                IpcMessage response;
                if (OnMessage != null)
                {
                    response = await OnMessage.Invoke(message);
                }
                else
                {
                    response = IpcMessage.Create("error", new { message = "No handler registered" });
                }

                await writer.WriteLineAsync(response.Serialize());
            }
        }
        catch (Exception)
        {
            // Connection error - client may have disconnected
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_listenTask != null)
        {
            try { await _listenTask; } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
    }
}
