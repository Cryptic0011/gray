using Gmux.Core.Models;

namespace Gmux.Core.Services;

/// <summary>
/// Manages notifications and dispatches them to the UI/toast layer.
/// </summary>
public class NotificationService
{
    private const int MaxNotifications = 1000;
    private readonly List<GmuxNotification> _notifications = [];

    public IReadOnlyList<GmuxNotification> Notifications => _notifications;

    /// <summary>
    /// Fired when a new notification arrives. The UI layer should display a toast.
    /// </summary>
    public event Action<GmuxNotification>? OnNotification;

    public GmuxNotification Notify(string workspaceName, string message, NotificationType type = NotificationType.Custom)
    {
        var notification = new GmuxNotification
        {
            WorkspaceName = workspaceName,
            Message = message,
            Type = type
        };

        _notifications.Add(notification);

        // Prune old notifications
        if (_notifications.Count > MaxNotifications)
        {
            // Remove oldest read notifications first
            _notifications.RemoveAll(n => n.IsRead);
            // If still over limit, keep only the newest MaxNotifications
            if (_notifications.Count > MaxNotifications)
            {
                _notifications.RemoveRange(0, _notifications.Count - MaxNotifications);
            }
        }

        OnNotification?.Invoke(notification);
        return notification;
    }

    public IEnumerable<GmuxNotification> GetUnread() =>
        _notifications.Where(n => !n.IsRead);

    public int UnreadCount => _notifications.Count(n => !n.IsRead);

    public int UnreadCountForWorkspace(string workspaceName) =>
        _notifications.Count(n => !n.IsRead && n.WorkspaceName == workspaceName);

    public void MarkRead(Guid id)
    {
        var notification = _notifications.FirstOrDefault(n => n.Id == id);
        if (notification != null)
            notification.IsRead = true;
    }

    public void MarkAllRead(string? workspaceName = null)
    {
        var targets = workspaceName == null
            ? _notifications
            : _notifications.Where(n => n.WorkspaceName == workspaceName);

        foreach (var n in targets)
            n.IsRead = true;
    }

    public void Clear() => _notifications.Clear();
}
