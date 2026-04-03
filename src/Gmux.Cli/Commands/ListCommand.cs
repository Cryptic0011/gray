using System.CommandLine;
using Gmux.Core.Ipc;

namespace Gmux.Cli.Commands;

public static class ListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List all workspaces");

        command.SetHandler(async () =>
        {
            var msg = IpcMessage.Create("list");
            var response = await PipeClient.SendAsync(msg);

            if (response == null)
            {
                Console.Error.WriteLine("Error: gray app is not running");
                Environment.ExitCode = 1;
                return;
            }

            var listResponse = response.GetPayload<ListResponse>();
            if (listResponse?.Workspaces == null || listResponse.Workspaces.Length == 0)
            {
                Console.WriteLine("No workspaces");
                return;
            }

            Console.WriteLine($"{"NAME",-20} {"BRANCH",-15} {"PANES",-8} {"NOTIF",-8} {"DIRECTORY"}");
            Console.WriteLine(new string('-', 80));

            foreach (var ws in listResponse.Workspaces)
            {
                Console.WriteLine(
                    $"{ws.Name,-20} {ws.GitBranch ?? "-",-15} {ws.PaneCount,-8} {ws.UnreadNotifications,-8} {ws.WorkingDirectory}");
            }
        });

        return command;
    }
}
