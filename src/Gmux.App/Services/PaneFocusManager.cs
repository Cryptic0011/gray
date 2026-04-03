using Gmux.Core.Models;

namespace Gmux.App.Services;

public class PaneFocusManager
{
    public Guid? FocusedPaneId { get; private set; }

    public event Action<Guid>? FocusChanged;

    public void SetFocus(Guid paneId)
    {
        if (FocusedPaneId == paneId) return;
        FocusedPaneId = paneId;
        FocusChanged?.Invoke(paneId);
    }

    public Guid? NavigateFocus(SplitNode root, NavigationDirection direction)
    {
        if (FocusedPaneId == null) return null;

        var leaves = root.GetLeavesInOrder();
        var currentIndex = leaves.FindIndex(l => l.PaneId == FocusedPaneId);
        if (currentIndex < 0) return null;

        int nextIndex = direction switch
        {
            NavigationDirection.Next => (currentIndex + 1) % leaves.Count,
            NavigationDirection.Previous => (currentIndex - 1 + leaves.Count) % leaves.Count,
            _ => currentIndex
        };

        var nextPaneId = leaves[nextIndex].PaneId;
        if (nextPaneId.HasValue)
            SetFocus(nextPaneId.Value);
        return nextPaneId;
    }
}

public enum NavigationDirection { Next, Previous }
