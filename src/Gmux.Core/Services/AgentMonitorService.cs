using System.Timers;
using Gmux.Core.Terminal.VtParser;

namespace Gmux.Core.Services;

/// <summary>
/// Per-pane state machine for tracking Claude Code agent status.
/// </summary>
public enum PaneAgentState
{
    /// <summary>Initial state — no prompt seen yet, Claude may not be running.</summary>
    Idle,
    /// <summary>Claude's initial prompt appeared — ready for user input, no notification.</summary>
    PromptSeen,
    /// <summary>Claude is actively working (prompt disappeared after being seen). No notification.</summary>
    Working,
    /// <summary>Claude finished work and is waiting for input. SHOW notification.</summary>
    Waiting,
    /// <summary>User clicked into the pane. Suppressed until next work→wait cycle.</summary>
    Dismissed,
}

/// <summary>
/// Tracks which terminal panes have a Claude Code agent waiting for user input.
/// Uses a state machine per pane to avoid false positives from initial prompts
/// and to persist dismiss state across detection flickers.
/// </summary>
public class AgentMonitorService : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, PaneAgentState> _paneStates = new();
    private readonly Dictionary<Guid, DateTime> _workingStartTime = new();
    private readonly System.Timers.Timer _debounceTimer;

    // Pane must be in Working state for this long before transition to Waiting.
    // Prevents false notification during Claude's startup sequence.
    private static readonly TimeSpan MinWorkingDuration = TimeSpan.FromSeconds(5);

    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "gmux", "agent_monitor.log");

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    /// <summary>Fires when any pane's waiting state changes (debounced).</summary>
    public event Action? StateChanged;

    public AgentMonitorService()
    {
        _debounceTimer = new System.Timers.Timer(300); // 300ms debounce
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (s, e) =>
        {
            StateChanged?.Invoke();
        };
    }

    /// <summary>True only in the Waiting state (Claude finished work, user hasn't clicked in).</summary>
    public bool IsWaiting(Guid paneId)
    {
        lock (_lock)
        {
            return _paneStates.TryGetValue(paneId, out var s) && s == PaneAgentState.Waiting;
        }
    }

    public int WaitingCountForPanes(IEnumerable<Guid> paneIds)
    {
        lock (_lock)
        {
            return paneIds.Count(id =>
                _paneStates.TryGetValue(id, out var s) && s == PaneAgentState.Waiting);
        }
    }

    /// <summary>
    /// Feed the raw prompt detection result for a pane. The state machine
    /// decides whether to fire a notification based on transitions.
    /// </summary>
    public void SetPromptDetected(Guid paneId, bool promptVisible)
    {
        lock (_lock)
        {
            _paneStates.TryGetValue(paneId, out var current); // default = Idle

            // promptVisible here means "Claude appears idle" (prompt + low output rate).
            bool workingLongEnough = current == PaneAgentState.Working
                && _workingStartTime.TryGetValue(paneId, out var startTime)
                && (DateTime.UtcNow - startTime) >= MinWorkingDuration;

            var next = (current, promptVisible) switch
            {
                // Idle: no signal yet. Stay idle until Claude is detected as idle.
                (PaneAgentState.Idle, false) => PaneAgentState.Idle,
                (PaneAgentState.Idle, true) => PaneAgentState.PromptSeen,

                // PromptSeen: Claude showed its initial prompt (idle after startup).
                // When output starts flowing, Claude started working.
                (PaneAgentState.PromptSeen, true) => PaneAgentState.PromptSeen,
                (PaneAgentState.PromptSeen, false) => PaneAgentState.Working,

                // Working: only transition to Waiting after minimum working duration.
                (PaneAgentState.Working, false) => PaneAgentState.Working,
                (PaneAgentState.Working, true) when workingLongEnough => PaneAgentState.Waiting,
                (PaneAgentState.Working, true) => PaneAgentState.Working, // too soon

                // Waiting: Claude finished. Still idle → keep notification.
                // Output flowing again → user gave more input.
                (PaneAgentState.Waiting, true) => PaneAgentState.Waiting,
                (PaneAgentState.Waiting, false) => PaneAgentState.Working,

                // Dismissed: user clicked in. Stay suppressed while idle.
                // Output flowing → Claude working, ready for next cycle.
                (PaneAgentState.Dismissed, true) => PaneAgentState.Dismissed,
                (PaneAgentState.Dismissed, false) => PaneAgentState.Working,

                _ => current,
            };

            if (next != current)
            {
                Log($"Pane {paneId.ToString()[..8]} : {current} → {next} (isWaiting={promptVisible})");
            }

            if (next == current) return;

            if (next == PaneAgentState.Working)
                _workingStartTime[paneId] = DateTime.UtcNow;

            _paneStates[paneId] = next;

            // Debounce: restart timer on each change
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    /// <summary>
    /// Dismiss notification for a pane (user clicked/navigated into it).
    /// Suppressed until Claude does another work→wait cycle.
    /// </summary>
    public void Dismiss(Guid paneId)
    {
        bool changed;
        lock (_lock)
        {
            _paneStates.TryGetValue(paneId, out var current);
            changed = current == PaneAgentState.Waiting;
            if (changed)
            {
                _paneStates[paneId] = PaneAgentState.Dismissed;
                Log($"Pane {paneId.ToString()[..8]} : Waiting → Dismissed (user clicked)");
            }
            else
            {
                Log($"Pane {paneId.ToString()[..8]} : Dismiss called but state is {current} — no-op");
            }
        }
        if (changed)
            StateChanged?.Invoke();
    }

    public void Remove(Guid paneId)
    {
        bool changed;
        lock (_lock)
        {
            changed = _paneStates.TryGetValue(paneId, out var current) && current == PaneAgentState.Waiting;
            _paneStates.Remove(paneId);
            _workingStartTime.Remove(paneId);
        }
        lock (_detectionLog)
        {
            _detectionLog.Remove(paneId);
        }
        if (changed)
            StateChanged?.Invoke();
    }

    // Periodic detection logging — logs every 5s per pane to avoid spam
    private static readonly Dictionary<Guid, (bool lastResult, DateTime lastLog)> _detectionLog = new();

    public static void LogDetection(Guid paneId, bool promptVisible, TerminalBuffer buffer)
    {
        lock (_detectionLog)
        {
            var now = DateTime.UtcNow;
            if (_detectionLog.TryGetValue(paneId, out var prev))
            {
                // Log on change, or every 5 seconds
                if (prev.lastResult == promptVisible && (now - prev.lastLog).TotalSeconds < 5)
                    return;
            }
            _detectionLog[paneId] = (promptVisible, now);

            // Read a few rows near cursor for context
            int cursorRow = buffer.CursorRow;
            var lines = new List<string>();
            for (int row = Math.Max(0, cursorRow - 2); row <= Math.Min(buffer.Rows - 1, cursorRow + 2); row++)
            {
                var chars = new char[Math.Min(buffer.Columns, 40)];
                for (int col = 0; col < chars.Length; col++)
                {
                    var ch = buffer.GetCell(row, col).Character;
                    chars[col] = ch == '\0' ? '·' : ch;
                }
                var marker = row == cursorRow ? " ←cursor" : "";
                lines.Add($"    row {row,3}: [{new string(chars).TrimEnd()}]{marker}");
            }
            Log($"Detection pane {paneId.ToString()[..8]}: prompt={promptVisible}, cursorRow={cursorRow}\n{string.Join("\n", lines)}");
        }
    }

    /// <summary>
    /// Detect Claude Code's input prompt in a terminal buffer.
    /// Scans the bottom portion of the visible buffer for the "> " prompt pattern.
    /// </summary>
    public static bool DetectClaudePrompt(TerminalBuffer buffer)
    {
        // Search around the cursor for Claude's "> " prompt at column 0.
        // Claude's status bar can be up to ~7 rows below the prompt, so we
        // use a generous window of ±10 rows. This avoids matching old prompts
        // that are 12+ rows away when Claude is actively working.
        int cursorRow = buffer.CursorRow;
        int startRow = Math.Min(cursorRow + 10, buffer.Rows - 1);
        int endRow = Math.Max(0, cursorRow - 10);

        for (int row = startRow; row >= endRow; row--)
        {
            if (buffer.Columns < 2) continue;

            char c0 = buffer.GetCell(row, 0).Character;
            char c1 = buffer.GetCell(row, 1).Character;

            // Claude Code's prompt: "❯" (U+276F) or ">" at column 0,
            // followed by space, NBSP, or null
            bool isPromptChar = c0 == '\u276F' || c0 == '>';
            bool isFollowedBySpace = c1 == ' ' || c1 == '\u00A0' || c1 == '\0';
            if (isPromptChar && isFollowedBySpace)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
        _debounceTimer.Dispose();
    }
}
