namespace Gmux.Core.Models;

public class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string? GitBranch { get; set; }
    public List<Tab> Tabs { get; set; } = [];
    public Guid? ActiveTabId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; }

    public Tab? ActiveTab => Tabs.FirstOrDefault(t => t.Id == ActiveTabId);
}
