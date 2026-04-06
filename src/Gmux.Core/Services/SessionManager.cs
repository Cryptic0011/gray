using Gmux.Core.Models;
using Gmux.Core.Terminal;

namespace Gmux.Core.Services;

public class SessionManager : IDisposable
{
    private static readonly System.Text.RegularExpressions.Regex AgentCommandRegex =
        new(@"(^|[;&|]\s*|^\s*cmd\s+/c\s+|^\s*powershell(?:\.exe)?\s+-Command\s+)(?:[""']?)(claude|codex|gemini)(?:\.exe)?(?=\s|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly Dictionary<Guid, TerminalSession> _sessions = new();
    private readonly Dictionary<Guid, System.Timers.Timer> _pollTimers = new();
    private readonly Dictionary<Guid, string> _pendingWorkingDirs = new();
    private readonly Dictionary<Guid, AgentCliKind> _pendingAutoLaunchAgents = new();
    private readonly Dictionary<Guid, IReadOnlyList<AgentCliKind>> _pendingAgentChoices = new();
    private readonly HashSet<Guid> _pendingDirectorySelections = new();
    private readonly HashSet<Guid> _trackedAgentPanes = new();
    private readonly string _defaultWorkingDirectory;
    private readonly SettingsManager _settingsManager;
    private AgentMonitorService? _agentMonitor;

    public SessionManager(string defaultWorkingDirectory, SettingsManager settingsManager)
    {
        _defaultWorkingDirectory = defaultWorkingDirectory;
        _settingsManager = settingsManager;
    }

    /// <summary>
    /// Connect the agent monitor so sessions automatically report Claude prompt state.
    /// </summary>
    public void SetAgentMonitor(AgentMonitorService monitor)
    {
        _agentMonitor = monitor;
    }

    public TerminalSession GetOrCreateSession(Guid paneId)
    {
        if (_sessions.TryGetValue(paneId, out var existing))
            return existing;

        var session = new TerminalSession(scrollbackSize: _settingsManager.Current.ScrollbackSize);
        _sessions[paneId] = session;
        session.InputSent += (_, text) =>
        {
            if (LooksLikeAgentLaunch(text))
            {
                _trackedAgentPanes.Add(paneId);
                _agentMonitor?.RegisterAgentLaunch(paneId);
            }
            else if (_trackedAgentPanes.Contains(paneId))
            {
                _agentMonitor?.MarkUserInput(paneId);
            }
        };

        // Monitor this session for Claude prompt detection.
        // Strategy: combine prompt detection with output RATE.
        // When Claude is working, it sends many output events/sec (spinner, text).
        // When idle, only cursor blinks (~1-2 events/sec).
        // So: "prompt visible + low output rate" = Claude is waiting.
        if (_agentMonitor != null)
        {
            var monitor = _agentMonitor;
            var checkLock = new object();
            int outputEventCount = 0;
            DateTime windowStart = DateTime.UtcNow;
            DateTime lastCheck = DateTime.MinValue;

            void checkPrompt()
            {
                lock (checkLock)
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastCheck).TotalMilliseconds < 100) return; // Throttle to 10Hz
                    lastCheck = now;

                    if (!_trackedAgentPanes.Contains(paneId))
                    {
                        monitor.SetPromptDetected(paneId, false);
                        return;
                    }

                    // Count output events in 3-second sliding window
                    double windowSeconds = (now - windowStart).TotalSeconds;
                    double eventsPerSecond = windowSeconds > 0 ? outputEventCount / windowSeconds : 0;

                    // Reset window periodically to keep it current
                    if (windowSeconds >= 3)
                    {
                        outputEventCount = 0;
                        windowStart = now;
                    }

                    bool promptVisible = AgentMonitorService.DetectAgentPrompt(session.Buffer);
                    // Cursor blinks: ~1-2 events/sec. Claude working: 5+ events/sec.
                    bool lowOutputRate = eventsPerSecond < 3;

                    bool isWaiting = promptVisible && lowOutputRate;
                    AgentMonitorService.LogDetection(paneId, isWaiting, session.Buffer);
                    monitor.SetPromptDetected(paneId, isWaiting);
                }
            }

            // Count output events for rate calculation
            session.OutputChanged += _ =>
            {
                lock (checkLock)
                {
                    outputEventCount++;
                }
                checkPrompt();
            };

            // Poll every 2 seconds — needed to detect idle state
            var pollTimer = new System.Timers.Timer(2000);
            pollTimer.AutoReset = true;
            pollTimer.Elapsed += (s, e) => checkPrompt();
            pollTimer.Start();
            _pollTimers[paneId] = pollTimer;
        }

        return session;
    }

    public async Task EnsureStartedAsync(Guid paneId, string? workingDirectory = null)
    {
        if (_pendingDirectorySelections.Contains(paneId))
            return;

        var session = GetOrCreateSession(paneId);
        if (!session.IsRunning)
        {
            // Use pending override (from split/new tab inheriting source CWD), then fallback
            var dir = _pendingWorkingDirs.Remove(paneId, out var pending)
                ? pending
                : workingDirectory ?? _defaultWorkingDirectory;
            await session.StartAsync(_settingsManager.Current.DefaultShell, dir);

            if (_pendingAutoLaunchAgents.Remove(paneId, out var agent))
            {
                await LaunchAgentInSessionAsync(paneId, session, agent);
            }
        }
    }

    /// <summary>
    /// Mark a pane so it prompts for a working directory before starting a shell.
    /// </summary>
    public void RequireDirectorySelection(Guid paneId)
    {
        _pendingDirectorySelections.Add(paneId);
        _pendingWorkingDirs.Remove(paneId);
        _pendingAutoLaunchAgents.Remove(paneId);
        _pendingAgentChoices.Remove(paneId);
    }

    public bool NeedsDirectorySelection(Guid paneId) => _pendingDirectorySelections.Contains(paneId);

    public void CompleteDirectorySelection(Guid paneId, string workingDirectory)
    {
        _pendingDirectorySelections.Remove(paneId);
        _pendingWorkingDirs[paneId] = workingDirectory;
    }

    /// <summary>
    /// Set pane startup behavior: inherit working directory, then either auto-launch
    /// the single configured agent or show a chooser when multiple are enabled.
    /// </summary>
    public void ConfigurePendingPane(Guid paneId, string workingDirectory, IReadOnlyList<AgentCliKind> enabledAgents)
    {
        _pendingWorkingDirs[paneId] = workingDirectory;
        _pendingDirectorySelections.Remove(paneId);
        _pendingAutoLaunchAgents.Remove(paneId);
        _pendingAgentChoices.Remove(paneId);

        var settings = _settingsManager.Current;
        if (enabledAgents.Count == 0 || settings.AgentLaunchMode == AgentLaunchMode.Disabled)
        {
            return;
        }

        if (settings.AgentLaunchMode == AgentLaunchMode.ShowChooser)
        {
            _pendingAgentChoices[paneId] = enabledAgents.ToArray();
            return;
        }

        if (enabledAgents.Contains(settings.PreferredAgent))
        {
            _pendingAutoLaunchAgents[paneId] = settings.PreferredAgent;
        }
        else if (enabledAgents.Count == 1)
        {
            _pendingAutoLaunchAgents[paneId] = enabledAgents[0];
        }
        else
        {
            _pendingAgentChoices[paneId] = enabledAgents.ToArray();
        }
    }

    public IReadOnlyList<AgentCliKind> GetPendingAgentChoices(Guid paneId)
    {
        return _pendingAgentChoices.TryGetValue(paneId, out var choices)
            ? choices
            : Array.Empty<AgentCliKind>();
    }

    public async Task LaunchAgentAsync(Guid paneId, AgentCliKind agent)
    {
        _pendingAgentChoices.Remove(paneId);
        var session = GetOrCreateSession(paneId);
        bool wasRunning = session.IsRunning;
        if (!wasRunning)
            await EnsureStartedAsync(paneId);

        await LaunchAgentInSessionAsync(paneId, session, agent, waitForShellOutput: !wasRunning);
    }

    public void DestroySession(Guid paneId)
    {
        if (_pollTimers.Remove(paneId, out var timer))
        {
            timer.Stop();
            timer.Dispose();
        }
        _trackedAgentPanes.Remove(paneId);
        _pendingDirectorySelections.Remove(paneId);
        _pendingAutoLaunchAgents.Remove(paneId);
        _pendingAgentChoices.Remove(paneId);
        if (_sessions.Remove(paneId, out var session))
            session.Dispose();
        _agentMonitor?.Remove(paneId);
    }

    public void DestroySessions(IEnumerable<Guid> paneIds)
    {
        foreach (var id in paneIds)
            DestroySession(id);
    }

    public bool HasSession(Guid paneId) => _sessions.ContainsKey(paneId);

    /// <summary>
    /// Get the current working directory of a pane's terminal by reading the
    /// cmd.exe prompt from the buffer (e.g. "C:\Users\gpatt>"). Falls back to
    /// the session's initial working directory.
    /// </summary>
    public string? GetWorkingDirectory(Guid paneId)
    {
        if (!_sessions.TryGetValue(paneId, out var session))
            return null;

        // Try to extract CWD from the terminal buffer's prompt line.
        // cmd.exe shows "C:\path>" as its prompt. Scan backwards from the cursor
        // to find the most recent prompt.
        var buffer = session.Buffer;
        for (int row = buffer.CursorRow; row >= 0; row--)
        {
            var line = ReadBufferLine(buffer, row);
            var cwd = ExtractCwdFromPrompt(line);
            if (cwd != null) return cwd;
        }

        return session.WorkingDirectory;
    }

    private static string ReadBufferLine(Terminal.VtParser.TerminalBuffer buffer, int row)
    {
        var chars = new char[buffer.Columns];
        for (int col = 0; col < buffer.Columns; col++)
        {
            var ch = buffer.GetCell(row, col).Character;
            chars[col] = ch == '\0' ? ' ' : ch;
        }
        return new string(chars).TrimEnd();
    }

    /// <summary>
    /// Extract a directory path from a cmd.exe prompt like "C:\Users\foo>"
    /// or "C:\Users\foo>some command". Returns null if the line doesn't look
    /// like a prompt.
    /// </summary>
    private static string? ExtractCwdFromPrompt(string line)
    {
        // Look for "X:\...>" pattern — the standard cmd.exe prompt
        int gtIdx = line.IndexOf('>');
        if (gtIdx < 3) return null; // Need at least "C:\"

        var candidate = line[..gtIdx];

        // Must look like a Windows path: drive letter + colon + backslash
        if (candidate.Length >= 3 && char.IsLetter(candidate[0]) &&
            candidate[1] == ':' && candidate[2] == '\\')
        {
            return candidate;
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var timer in _pollTimers.Values)
        {
            timer.Stop();
            timer.Dispose();
        }
        _pollTimers.Clear();
        _pendingDirectorySelections.Clear();
        _pendingAutoLaunchAgents.Clear();
        _pendingAgentChoices.Clear();
        _trackedAgentPanes.Clear();
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }

    public void ApplySettings()
    {
        foreach (var session in _sessions.Values)
            session.ApplySettings(_settingsManager.Current.ScrollbackSize);
    }

    private static bool LooksLikeAgentLaunch(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (AgentCommandRegex.IsMatch(line))
                return true;
        }

        return false;
    }

    private async Task LaunchAgentInSessionAsync(Guid paneId, TerminalSession session, AgentCliKind agent, bool waitForShellOutput = true)
    {
        var command = _settingsManager.Current.GetLaunchCommand(agent);
        if (command == null)
            return; // None — just open a blank shell

        _trackedAgentPanes.Add(paneId);
        _agentMonitor?.RegisterAgentLaunch(paneId);

        if (waitForShellOutput)
        {
            var tcs = new TaskCompletionSource();
            void onOutput(Terminal.TerminalSession _) { tcs.TrySetResult(); }
            session.OutputChanged += onOutput;
            await Task.WhenAny(tcs.Task, Task.Delay(2000));
            session.OutputChanged -= onOutput;
            await Task.Delay(50);
        }

        session.SendInput(command + "\r");
    }
}
