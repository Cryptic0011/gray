# HANDOFF

## Project state

This repo is a Windows terminal multiplexer app (`gray`) with:
- workspaces, tabs, split panes
- agent launcher support for Claude, Codex, and Gemini
- waiting-state detection and notifications for tracked agent panes
- persisted workspace state and app settings
- basic GitHub Releases update-check support
- release workflow for zip artifacts

Build status at handoff:
- `dotnet build gmux.sln` succeeds
- 0 errors
- 0 warnings

## Major work completed

### Terminal and PTY correctness

Implemented missing VT operations and cleanup:
- `CSI @`, `CSI P`, `CSI X`
- scrollback copy now matches what is rendered
- reduced render-time buffer locking with snapshots
- hardened ConPTY startup/cleanup failure paths

Key files:
- `src/Gmux.Core/Terminal/VtParser/AnsiParser.cs`
- `src/Gmux.Core/Terminal/VtParser/TerminalBuffer.cs`
- `src/Gmux.App/Controls/TerminalControl.xaml.cs`
- `src/Gmux.Core/Terminal/ConPty/PseudoConsole.cs`
- `src/Gmux.Core/Terminal/ConPty/ProcessFactory.cs`

### Notification / waiting-state fixes

Improved tracked-agent waiting detection:
- thread-safe prompt-rate checks
- active pane dismissal cleanup
- no longer monitoring every generic terminal as Claude by default
- manual `claude`, `codex`, `gemini` launches typed into the terminal are tracked
- waiting banner can now be global or active-tab-only
- notification banner does not re-open repeatedly for unchanged state

Key files:
- `src/Gmux.Core/Services/AgentMonitorService.cs`
- `src/Gmux.Core/Services/SessionManager.cs`
- `src/Gmux.App/MainWindow.xaml.cs`

### Multi-agent launcher settings

Added persistent settings for:
- enabling Claude / Codex / Gemini
- default shell
- agent launch mode:
  - `PreferredAgent`
  - `ShowChooser`
  - `Disabled`
- preferred agent
- per-agent launch commands
- waiting notification scope
- waiting toast duration
- terminal font size
- scrollback size

New panes/tabs:
- auto-launch the preferred agent if configured and enabled
- show a chooser overlay if chooser mode or multiple enabled without preferred match
- support commands:
  - Claude: default `claude --dangerously-skip-permissions`
  - Codex: default `codex --yolo`
  - Gemini: default `gemini --yolo`

Key files:
- `src/Gmux.Core/Models/AppSettings.cs`
- `src/Gmux.Core/Models/AgentCliKind.cs`
- `src/Gmux.Core/Models/AgentLaunchMode.cs`
- `src/Gmux.Core/Models/NotificationScope.cs`
- `src/Gmux.Core/Services/SettingsManager.cs`
- `src/Gmux.Core/Services/SessionManager.cs`
- `src/Gmux.App/Controls/SplitPaneContainer.xaml.cs`
- `src/Gmux.App/Controls/WorkspaceSidebar.xaml`
- `src/Gmux.App/Controls/WorkspaceSidebar.xaml.cs`
- `src/Gmux.App/MainWindow.xaml.cs`
- `src/Gmux.App/App.xaml.cs`

### Production-readiness foundation

Added:
- shared assembly/app metadata
- tool availability diagnostics
- GitHub Releases update checker
- startup update check notification
- release-oriented README
- GitHub Actions workflow for build + publish zip artifacts

Key files:
- `Directory.Build.props`
- `src/Gmux.Core/Models/ToolStatus.cs`
- `src/Gmux.Core/Models/UpdateCheckResult.cs`
- `src/Gmux.Core/Services/AppInfoService.cs`
- `src/Gmux.Core/Services/UpdateCheckerService.cs`
- `README.md`
- `.github/workflows/release.yml`

## Important placeholders / follow-up

### Publishing metadata

In `Directory.Build.props`:
- `RepositoryUrl` is set to:
  - `https://github.com/Cryptic0011/gray`

Update checker depends on this staying aligned with the real GitHub repo URL.

### Current release workflow scope

The GitHub Actions workflow currently:
- builds solution
- publishes app and CLI
- zips artifacts
- uploads to workflow artifacts
- creates GitHub Release assets on `v*` tags

It does **not** yet do:
- MSI creation
- winget manifest publishing
- code signing
- checksums

## Known limitations

1. Default shell changes only affect new sessions, not already-running panes.
2. Agent tracking from manual launch depends on command text containing:
   - `claude`
   - `codex`
   - `gemini`
   Aliases/wrapper scripts are not detected automatically.
3. Waiting prompt detection is still prompt-shape based; it is now scoped to tracked agent panes, but not fully tool-specific.
4. README mentions MSI/winget as planned, not implemented.

## Recommended next steps

### Highest value next

1. Add MSI packaging
2. Add winget packaging
3. Add code signing plan
4. Add checksums to releases

### Engineering hardening

1. Add tests for:
   - settings persistence
   - update version parsing
   - tool availability detection
   - session launch mode behavior
   - waiting notification state transitions
2. Add logging for update-check failures
3. Add a visible About section in settings with:
   - version
   - release URL
   - copy diagnostic info
4. Add first-run validation for configured shell/agent commands

## Suggested implementation order

1. MSI packaging
2. winget manifest
3. release checksums + release notes
4. tests
5. code signing

## Commands used recently

Build:

```powershell
dotnet build gmux.sln
```

Run app from source:

```powershell
dotnet run --project src/Gmux.App/Gmux.App.csproj
```

Run CLI from source:

```powershell
dotnet run --project src/Gmux.Cli/Gmux.Cli.csproj -- status
```

## Files added recently

- `HANDOFF.md`
- `README.md`
- `.github/workflows/release.yml`
- `src/Gmux.Core/Models/AgentCliKind.cs`
- `src/Gmux.Core/Models/AgentLaunchMode.cs`
- `src/Gmux.Core/Models/NotificationScope.cs`
- `src/Gmux.Core/Models/AppSettings.cs`
- `src/Gmux.Core/Models/ToolStatus.cs`
- `src/Gmux.Core/Models/UpdateCheckResult.cs`
- `src/Gmux.Core/Services/SettingsManager.cs`
- `src/Gmux.Core/Services/AppInfoService.cs`
- `src/Gmux.Core/Services/UpdateCheckerService.cs`
