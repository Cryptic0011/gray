using System.Text.Json;
using Gmux.Core.Models;

namespace Gmux.Core.Services;

public class WorkspaceManager
{
    private readonly List<Workspace> _workspaces = [];
    private readonly string _stateDir;
    private readonly string _stateFile;

    public IReadOnlyList<Workspace> Workspaces => _workspaces;
    public Workspace? ActiveWorkspace => _workspaces.FirstOrDefault(w => w.IsActive);

    public event Action? WorkspacesChanged;
    public event Action? ActiveTabChanged;
    public event Action? SplitTreeChanged;

    public WorkspaceManager()
    {
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gray");
        _stateFile = Path.Combine(_stateDir, "workspaces.json");
    }

    // --- Workspace Management ---

    public Workspace CreateWorkspace(string name, string workingDirectory)
    {
        var workspace = new Workspace
        {
            Name = name,
            WorkingDirectory = workingDirectory,
        };

        var tab = new Tab { Title = "Terminal 1" };
        workspace.Tabs.Add(tab);
        workspace.ActiveTabId = tab.Id;

        foreach (var w in _workspaces) w.IsActive = false;
        workspace.IsActive = true;

        _workspaces.Add(workspace);
        _ = RefreshGitBranchAsync(workspace).ContinueWith(
            t => System.Diagnostics.Debug.WriteLine($"Git branch refresh failed: {t.Exception?.InnerException?.Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
        WorkspacesChanged?.Invoke();
        return workspace;
    }

    public void ActivateWorkspace(Guid id)
    {
        foreach (var w in _workspaces)
            w.IsActive = w.Id == id;
        var active = _workspaces.FirstOrDefault(w => w.Id == id);
        if (active != null) _ = RefreshGitBranchAsync(active).ContinueWith(
            t => System.Diagnostics.Debug.WriteLine($"Git branch refresh failed: {t.Exception?.InnerException?.Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
        WorkspacesChanged?.Invoke();
    }

    public void RenameWorkspace(Guid id, string newName)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == id);
        if (workspace == null || string.IsNullOrWhiteSpace(newName)) return;
        workspace.Name = newName.Trim();
        WorkspacesChanged?.Invoke();
    }

    public void UpdateWorkspaceDirectory(Guid id, string workingDirectory)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == id);
        if (workspace == null || string.IsNullOrWhiteSpace(workingDirectory)) return;

        workspace.WorkingDirectory = workingDirectory;
        _ = RefreshGitBranchAsync(workspace).ContinueWith(
            t => System.Diagnostics.Debug.WriteLine($"Git branch refresh failed: {t.Exception?.InnerException?.Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
        WorkspacesChanged?.Invoke();
    }

    public void RemoveWorkspace(Guid id)
    {
        _workspaces.RemoveAll(w => w.Id == id);
        if (_workspaces.Count > 0 && !_workspaces.Any(w => w.IsActive))
            _workspaces[0].IsActive = true;
        WorkspacesChanged?.Invoke();
    }

    // --- Tab Management ---

    public void RenameTab(Guid workspaceId, Guid tabId, string newTitle)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        var tab = workspace?.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab == null || string.IsNullOrWhiteSpace(newTitle)) return;
        tab.Title = newTitle.Trim();
        ActiveTabChanged?.Invoke();
    }

    public Tab? AddTab(Guid workspaceId)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (workspace == null) return null;

        var tab = new Tab { Title = $"Terminal {workspace.Tabs.Count + 1}" };
        workspace.Tabs.Add(tab);

        // Activate the new tab
        workspace.ActiveTabId = tab.Id;
        ActiveTabChanged?.Invoke();
        return tab;
    }

    public List<Guid> RemoveTab(Guid workspaceId, Guid tabId)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (workspace == null) return [];

        var tab = workspace.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab == null) return [];

        // Collect all pane IDs to destroy
        var paneIds = tab.RootSplit.GetAllPaneIds().ToList();

        workspace.Tabs.Remove(tab);

        // If we removed the active tab, activate another
        if (workspace.ActiveTabId == tabId)
        {
            workspace.ActiveTabId = workspace.Tabs.FirstOrDefault()?.Id;
        }

        if (workspace.Tabs.Count == 0)
        {
            // Last tab closed — remove workspace
            RemoveWorkspace(workspaceId);
        }
        else
        {
            ActiveTabChanged?.Invoke();
        }

        return paneIds;
    }

    public void ActivateTab(Guid workspaceId, Guid tabId)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (workspace == null) return;

        workspace.ActiveTabId = tabId;
        ActiveTabChanged?.Invoke();
    }

    public void MoveTab(Guid workspaceId, Guid tabId, int newIndex)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (workspace == null) return;

        var oldIndex = workspace.Tabs.FindIndex(t => t.Id == tabId);
        if (oldIndex < 0 || newIndex < 0 || newIndex >= workspace.Tabs.Count || oldIndex == newIndex) return;

        var tab = workspace.Tabs[oldIndex];
        workspace.Tabs.RemoveAt(oldIndex);
        workspace.Tabs.Insert(newIndex, tab);
        ActiveTabChanged?.Invoke();
    }

    public void CycleTab(Guid workspaceId, int direction)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (workspace == null || workspace.Tabs.Count <= 1) return;

        var currentIndex = workspace.Tabs.FindIndex(t => t.Id == workspace.ActiveTabId);
        if (currentIndex < 0) return;

        var nextIndex = (currentIndex + direction + workspace.Tabs.Count) % workspace.Tabs.Count;
        workspace.ActiveTabId = workspace.Tabs[nextIndex].Id;
        ActiveTabChanged?.Invoke();
    }

    // --- Split Management ---

    public Guid? SplitPane(Guid workspaceId, Guid tabId, Guid paneId, SplitDirection direction)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        var tab = workspace?.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab == null) return null;

        var leaf = tab.RootSplit.FindLeaf(paneId);
        if (leaf == null) return null;

        // Create a new leaf for the second pane
        var newLeaf = SplitNode.CreateLeaf();

        // Replace the leaf with a split containing the old leaf and new leaf
        var oldPaneId = leaf.PaneId;
        var firstChild = new SplitNode
        {
            Type = SplitNodeType.Leaf,
            PaneId = oldPaneId
        };

        leaf.Type = SplitNodeType.Split;
        leaf.Direction = direction;
        leaf.PaneId = null;
        leaf.First = firstChild;
        leaf.Second = newLeaf;
        leaf.Ratio = 0.5;

        SplitTreeChanged?.Invoke();
        return newLeaf.PaneId;
    }

    public List<Guid> ClosePane(Guid workspaceId, Guid tabId, Guid paneId)
    {
        var workspace = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
        var tab = workspace?.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab == null || workspace == null) return [];

        // If this is the only leaf, close the tab instead
        var allPanes = tab.RootSplit.GetAllPaneIds().ToList();
        if (allPanes.Count <= 1)
        {
            return RemoveTab(workspaceId, tabId);
        }

        // Find the leaf and its parent
        var leaf = tab.RootSplit.FindLeaf(paneId);
        if (leaf == null) return [];

        var parent = tab.RootSplit.FindParent(leaf.Id);
        if (parent == null)
        {
            // This is the root — shouldn't happen since we checked count > 1
            return [];
        }

        // Determine the sibling (the one that survives)
        var sibling = parent.First?.Id == leaf.Id ? parent.Second : parent.First;
        if (sibling == null) return [];

        // Replace parent with sibling's content
        parent.Type = sibling.Type;
        parent.Direction = sibling.Direction;
        parent.Ratio = sibling.Ratio;
        parent.PaneId = sibling.PaneId;
        parent.First = sibling.First;
        parent.Second = sibling.Second;

        SplitTreeChanged?.Invoke();
        return [paneId];
    }

    // --- Git ---

    private async Task RefreshGitBranchAsync(Workspace workspace)
    {
        var branch = await GetGitBranchAsync(workspace.WorkingDirectory);
        if (workspace.GitBranch != branch)
        {
            workspace.GitBranch = branch;
            WorkspacesChanged?.Invoke();
        }
    }

    public async Task<string?> GetGitBranchAsync(string workingDirectory)
    {
        try
        {
            var gitDir = await ResolveGitDirectoryAsync(workingDirectory);
            if (gitDir == null) return null;

            var gitHead = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(gitHead)) return null;
            var content = await File.ReadAllTextAsync(gitHead);
            if (content.StartsWith("ref: refs/heads/"))
                return content["ref: refs/heads/".Length..].Trim();
            var trimmed = content.Trim();
            return trimmed.Length <= 7 ? trimmed : trimmed[..7];
        }
        catch
        {
            return null;
        }
    }

    // --- Persistence ---

    public async Task SaveStateAsync()
    {
        Directory.CreateDirectory(_stateDir);
        var json = JsonSerializer.Serialize(_workspaces, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_stateFile, json);
    }

    public async Task LoadStateAsync()
    {
        if (!File.Exists(_stateFile)) return;
        try
        {
            var json = await File.ReadAllTextAsync(_stateFile);
            var workspaces = JsonSerializer.Deserialize<List<Workspace>>(json);
            if (workspaces != null)
            {
                _workspaces.Clear();
                _workspaces.AddRange(workspaces);
                if (_workspaces.Count > 0 && !_workspaces.Any(w => w.IsActive))
                    _workspaces[0].IsActive = true;
                WorkspacesChanged?.Invoke();

                foreach (var workspace in _workspaces)
                {
                    _ = RefreshGitBranchAsync(workspace).ContinueWith(
                        t => System.Diagnostics.Debug.WriteLine($"Git branch refresh failed: {t.Exception?.InnerException?.Message}"),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load workspace state: {ex.Message}");
        }
    }

    private static async Task<string?> ResolveGitDirectoryAsync(string workingDirectory)
    {
        var gitPath = Path.Combine(workingDirectory, ".git");
        if (Directory.Exists(gitPath))
            return gitPath;

        if (!File.Exists(gitPath))
            return null;

        var gitPointer = (await File.ReadAllTextAsync(gitPath)).Trim();
        const string prefix = "gitdir:";
        if (!gitPointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var gitDir = gitPointer[prefix.Length..].Trim();
        return Path.GetFullPath(Path.IsPathRooted(gitDir)
            ? gitDir
            : Path.Combine(workingDirectory, gitDir));
    }
}
