using System.CommandLine;
using Gmux.Cli.Commands;

var rootCommand = new RootCommand("gray - Terminal multiplexer for Claude Code on Windows");

rootCommand.AddCommand(NotifyCommand.Create());
rootCommand.AddCommand(ListCommand.Create());
rootCommand.AddCommand(StatusCommand.Create());

return await rootCommand.InvokeAsync(args);
