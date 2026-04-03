using System.IO.Pipes;
using System.Text;

namespace Gmux.Core.Ipc;

/// <summary>
/// Named pipe client for sending commands from CLI to the running App.
/// </summary>
public static class PipeClient
{
    public static async Task<IpcMessage?> SendAsync(IpcMessage message, int timeoutMs = 5000)
    {
        using var client = new NamedPipeClientStream(".", IpcMessage.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(timeoutMs);
        }
        catch (TimeoutException)
        {
            return null;
        }

        using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8);

        await writer.WriteLineAsync(message.Serialize());

        var responseLine = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(responseLine)) return null;

        return IpcMessage.Deserialize(responseLine);
    }
}
