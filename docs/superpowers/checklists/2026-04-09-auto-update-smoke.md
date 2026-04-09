# Auto-update smoke test

Run before every release. Assumes you have an installed copy of the **previous** version of gray and are about to ship a new one.

## Setup
1. Confirm you have gray vN-1 installed via MSI (check Add/Remove Programs).
2. Tag and push vN. Wait for `.github/workflows/release.yml` to publish the release with all three assets (`gray-installer-win-x64.msi`, `gray-app-win-x64.zip`, `gray-cli-win-x64.zip`).
3. Verify the release page shows the expected assets.

## In-app update flow
1. Launch installed gray vN-1. The update banner should appear at the top within a few seconds. Title: `gray vN is available`. Body: first 200 chars of release notes.
2. Click **What's new** → browser opens to the release page. Close browser, return to gray.
3. Click **Install** → banner shows "Downloading vN… X%" with a progress bar.
4. When download completes, banner briefly shows "Installing vN — gray will restart", then gray exits.
5. Wait ~10 seconds. gray relaunches as vN. Confirm by checking sidebar → Settings → Check for updates → should report "You are up to date".

## Skipped version
1. Revert to vN-1 (uninstall vN, reinstall vN-1 from the older MSI).
2. Launch → banner appears. Click **Skip this version**. Banner disappears.
3. Close gray and relaunch. Banner should NOT appear.
4. Verify `%LocalAppData%\gray\settings.json` contains `"SkippedVersion": "vN"`.

## Later
1. Revert to vN-1 again and delete `SkippedVersion` from settings.
2. Launch → banner appears. Click **Later** (or the ✕). Banner disappears.
3. Relaunch gray (without waiting 4 hours). The banner **should** reappear because there's no persistence for Later beyond the current session. Note: there's also a 4h throttle, so if you've already been testing the banner in a tight loop, delete `LastCheckUtc` from settings first.

## Download failure recovery
1. From vN-1 with banner visible, disconnect your network mid-download (or click Install then unplug).
2. Banner transitions to `Error` with "Retry" and "Open in browser" buttons.
3. Reconnect, click **Retry**. Banner returns to `Available`. Click **Install** again → should succeed.

## Multi-window refusal
1. Open gray in two windows (right-click tray / Start menu → launch twice, or `Gmux.App.exe` from two terminals).
2. In one window, click **Install**. Banner transitions to `Error` with "Close other gray windows first, then try again."
3. Close the second window. Click **Retry** → install proceeds normally.

## PowerShell one-liner (fresh install)
1. On a clean Windows VM (or uninstall gray completely first).
2. Open a PowerShell prompt (non-admin).
3. Run: `iwr https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1 -UseBasicParsing | iex`
4. Expect `==>` progress messages, an msiexec progress dialog, and `OK  gray vN installed.` at the end.
5. Run `gray --version` (CLI) and launch gray from Start menu. Both should report vN.

## Version pinning
1. `iwr https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1 -OutFile $env:TEMP\install.ps1; & $env:TEMP\install.ps1 -Version vN-1`
2. Confirms pinning a specific version works. Useful for CI.

## Cleanup
- `%LocalAppData%\gray\updates\` should be empty after success (or contain only `last-failure.txt` after a failed smoke).
- `%TEMP%\gray-installer-*.msi` should be gone unless `-KeepInstaller` was used.
