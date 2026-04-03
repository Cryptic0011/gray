namespace Gmux.Core.Models;

public class GmuxNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkspaceName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
    public NotificationType Type { get; set; } = NotificationType.Custom;
}

public enum NotificationType
{
    AgentComplete,
    Error,
    Custom
}
