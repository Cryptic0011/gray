using Microsoft.UI.Xaml;
using Gmux.Core.Ipc;
using Gmux.Core.Services;

namespace Gmux.App;

public partial class App : Application
{
    private Window? _window;

    public static WorkspaceManager WorkspaceManager { get; } = new();
    public static SettingsManager SettingsManager { get; } = new();
    public static NotificationService NotificationService { get; } = new();
    public static AgentMonitorService AgentMonitor { get; } = new();
    public static UpdateCheckerService UpdateChecker { get; } = new();
    public static PipeServer PipeServer { get; } = new();
    public static SessionManager? SessionManager { get; set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await SettingsManager.LoadAsync();

        _window = new MainWindow();
        _window.Activate();

        // Start IPC server
        PipeServer.OnMessage += HandleIpcMessage;
        await PipeServer.StartAsync();

        // Load saved state
        await WorkspaceManager.LoadStateAsync();

        // If no workspaces, create a default one
        if (WorkspaceManager.Workspaces.Count == 0)
        {
            WorkspaceManager.CreateWorkspace("Default",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        _ = Task.Run(async () =>
        {
            var update = await UpdateChecker.CheckForUpdatesAsync();
            if (update.IsUpdateAvailable)
            {
                NotificationService.Notify("gray", update.Message);
            }
        });
    }

    private async Task<IpcMessage> HandleIpcMessage(IpcMessage message)
    {
        switch (message.Type)
        {
            case "notify":
                var notifyReq = message.GetPayload<NotifyRequest>();
                if (notifyReq != null)
                {
                    var workspace = notifyReq.Workspace ?? WorkspaceManager.ActiveWorkspace?.Name ?? "Unknown";
                    NotificationService.Notify(workspace, notifyReq.Message);
                }
                return IpcMessage.Create("ok");

            case "list":
                var workspaces = WorkspaceManager.Workspaces.Select(w => new WorkspaceInfo(
                    w.Name,
                    w.WorkingDirectory,
                    w.GitBranch,
                    w.Tabs.Sum(t => t.RootSplit.GetAllPaneIds().Count()),
                    NotificationService.UnreadCountForWorkspace(w.Name)
                )).ToArray();
                return IpcMessage.Create("response", new ListResponse(workspaces));

            case "status":
                return IpcMessage.Create("response", new StatusResponse(
                    WorkspaceManager.ActiveWorkspace?.Name,
                    WorkspaceManager.Workspaces.Count,
                    NotificationService.UnreadCount
                ));

            default:
                return IpcMessage.Create("error", new { message = $"Unknown command: {message.Type}" });
        }
    }
}
