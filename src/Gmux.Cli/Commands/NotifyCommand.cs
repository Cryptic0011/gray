using System.CommandLine;
using Gmux.Core.Ipc;

namespace Gmux.Cli.Commands;

public static class NotifyCommand
{
    public static Command Create()
    {
        var messageArg = new Argument<string>("message", "Notification message to display");
        var workspaceOption = new Option<string?>("--workspace", "Target workspace name");
        workspaceOption.AddAlias("-w");

        var command = new Command("notify", "Send a notification to the gray app")
        {
            messageArg,
            workspaceOption
        };

        command.SetHandler(async (string message, string? workspace) =>
        {
            var request = new NotifyRequest(message, workspace);
            var msg = IpcMessage.Create("notify", request);
            var response = await PipeClient.SendAsync(msg);

            if (response == null)
            {
                Console.Error.WriteLine("Error: gray app is not running");
                Environment.ExitCode = 1;
                return;
            }

            if (response.Type == "ok")
                Console.WriteLine($"Notification sent: {message}");
            else
                Console.Error.WriteLine($"Error: {response.Type}");

        }, messageArg, workspaceOption);

        return command;
    }
}
