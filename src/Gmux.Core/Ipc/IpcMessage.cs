using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gmux.Core.Ipc;

public class IpcMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    public static string PipeName => $"gray-{Environment.UserName}";

    public string Serialize() => JsonSerializer.Serialize(this);

    public static IpcMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<IpcMessage>(json);

    public static IpcMessage Create(string type, object? payload = null)
    {
        var msg = new IpcMessage { Type = type };
        if (payload != null)
        {
            var json = JsonSerializer.Serialize(payload);
            msg.Payload = JsonSerializer.Deserialize<JsonElement>(json);
        }
        return msg;
    }

    public T? GetPayload<T>() where T : class
    {
        if (Payload == null) return null;
        return JsonSerializer.Deserialize<T>(Payload.Value.GetRawText());
    }
}

// Request/response payloads
public record NotifyRequest(string Message, string? Workspace = null);
public record ListResponse(WorkspaceInfo[] Workspaces);
public record WorkspaceInfo(string Name, string WorkingDirectory, string? GitBranch, int PaneCount, int UnreadNotifications);
public record StatusResponse(string? ActiveWorkspace, int TotalWorkspaces, int UnreadNotifications);
