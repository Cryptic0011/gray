using System.CommandLine;
using Gmux.Core.Ipc;

namespace Gmux.Cli.Commands;

public static class StatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "Show gray status");

        command.SetHandler(async () =>
        {
            var msg = IpcMessage.Create("status");
            var response = await PipeClient.SendAsync(msg);

            if (response == null)
            {
                Console.Error.WriteLine("Error: gray app is not running");
                Environment.ExitCode = 1;
                return;
            }

            var status = response.GetPayload<StatusResponse>();
            if (status == null)
            {
                Console.Error.WriteLine("Error: invalid response");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Active workspace: {status.ActiveWorkspace ?? "none"}");
            Console.WriteLine($"Total workspaces:  {status.TotalWorkspaces}");
            Console.WriteLine($"Unread notifs:     {status.UnreadNotifications}");
        });

        return command;
    }
}
