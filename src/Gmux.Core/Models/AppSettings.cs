namespace Gmux.Core.Models;

public class AppSettings
{
    public bool UseClaudeCli { get; set; } = true;
    public bool UseCodexCli { get; set; }
    public bool UseGeminiCli { get; set; }
    public string DefaultShell { get; set; } = "cmd.exe";
    public AgentLaunchMode AgentLaunchMode { get; set; } = AgentLaunchMode.PreferredAgent;
    public AgentCliKind PreferredAgent { get; set; } = AgentCliKind.Claude;
    public string ClaudeLaunchCommand { get; set; } = "claude --dangerously-skip-permissions";
    public string CodexLaunchCommand { get; set; } = "codex --yolo";
    public string GeminiLaunchCommand { get; set; } = "gemini --yolo";
    public NotificationScope WaitingNotificationScope { get; set; } = NotificationScope.AllTabs;
    public int WaitingNotificationDurationSeconds { get; set; } = 5;
    public double TerminalFontSize { get; set; } = 14;
    public int ScrollbackSize { get; set; } = 5000;

    public IReadOnlyList<AgentCliKind> GetEnabledAgentClis()
    {
        var result = new List<AgentCliKind>(3);
        if (UseClaudeCli) result.Add(AgentCliKind.Claude);
        if (UseCodexCli) result.Add(AgentCliKind.Codex);
        if (UseGeminiCli) result.Add(AgentCliKind.Gemini);
        return result;
    }

    public string? GetLaunchCommand(AgentCliKind agent) => agent switch
    {
        AgentCliKind.Claude => ClaudeLaunchCommand,
        AgentCliKind.Codex => CodexLaunchCommand,
        AgentCliKind.Gemini => GeminiLaunchCommand,
        AgentCliKind.None => null,
        _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, null)
    };
}
