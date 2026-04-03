namespace Gmux.Core.Models;

public class Tab
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Terminal";
    public SplitNode RootSplit { get; set; } = SplitNode.CreateLeaf();
    public Guid? FocusedPaneId { get; set; }
    public bool IsActive { get; set; }
}
