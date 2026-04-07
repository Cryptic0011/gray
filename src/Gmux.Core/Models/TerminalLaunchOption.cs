namespace Gmux.Core.Models;

public class TerminalLaunchOption
{
    public AgentCliKind? Agent { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Command { get; init; }
    public bool IsBlank => Agent == AgentCliKind.None || string.IsNullOrWhiteSpace(Command);

    public static TerminalLaunchOption BuiltIn(AgentCliKind agent, string? command) => new()
    {
        Agent = agent,
        Title = agent switch
        {
            AgentCliKind.Claude => "Claude",
            AgentCliKind.Codex => "Codex",
            AgentCliKind.Gemini => "Gemini",
            AgentCliKind.None => "Blank",
            _ => agent.ToString()
        },
        Command = command
    };

    public static TerminalLaunchOption Custom(CustomTerminalCommand custom) => new()
    {
        Agent = null,
        Title = custom.Title.Trim(),
        Command = custom.Command.Trim()
    };
}
