namespace Gmux.Core.Models;

public class SplitNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SplitNodeType Type { get; set; }
    public SplitDirection Direction { get; set; }
    public double Ratio { get; set; } = 0.5;
    public SplitNode? First { get; set; }
    public SplitNode? Second { get; set; }
    public Guid? PaneId { get; set; }

    public static SplitNode CreateLeaf()
    {
        return new SplitNode { Type = SplitNodeType.Leaf, PaneId = Guid.NewGuid() };
    }

    public static SplitNode CreateSplit(SplitDirection direction, SplitNode first, SplitNode second, double ratio = 0.5)
    {
        return new SplitNode
        {
            Type = SplitNodeType.Split,
            Direction = direction,
            First = first,
            Second = second,
            Ratio = ratio
        };
    }

    public SplitNode? FindLeaf(Guid paneId)
    {
        if (Type == SplitNodeType.Leaf && PaneId == paneId) return this;
        return First?.FindLeaf(paneId) ?? Second?.FindLeaf(paneId);
    }

    public SplitNode? FindParent(Guid nodeId)
    {
        if (First?.Id == nodeId || Second?.Id == nodeId) return this;
        return First?.FindParent(nodeId) ?? Second?.FindParent(nodeId);
    }

    public IEnumerable<Guid> GetAllPaneIds()
    {
        if (Type == SplitNodeType.Leaf && PaneId.HasValue)
        {
            yield return PaneId.Value;
            yield break;
        }
        if (First != null)
            foreach (var id in First.GetAllPaneIds()) yield return id;
        if (Second != null)
            foreach (var id in Second.GetAllPaneIds()) yield return id;
    }

    public List<SplitNode> GetLeavesInOrder()
    {
        var result = new List<SplitNode>();
        CollectLeaves(result);
        return result;
    }

    private void CollectLeaves(List<SplitNode> result)
    {
        if (Type == SplitNodeType.Leaf) { result.Add(this); return; }
        First?.CollectLeaves(result);
        Second?.CollectLeaves(result);
    }
}

public enum SplitNodeType { Leaf, Split }

public enum SplitDirection
{
    None,
    Horizontal,
    Vertical
}
