# Conan Legacy Doctor

`Conan Legacy Doctor` is a Windows PowerShell utility for players who switch from Conan Exiles Enhanced back to the Legacy branch and then hit startup hangs, black screens, or mod/config residue that makes the return path unreliable.

## Downloads

- Source code is available in this repository.
- The latest tested Windows executable build is produced locally at `artifacts\publish\win-x64\ConanLegacyDoctor.exe`.
- Current local build SHA-256: `ce3a3b9dd715ca3cdddb8563f86e82d437f64dcd62273fe8b8234eecdb8ac785`.
- That executable is built and validated, but it has not yet been code-signed or attached to a GitHub Release asset.
- Until the signed release package is posted, the PowerShell launcher and the source remain the most transparent ways to inspect behavior directly.

## Practical Disclaimer

This tool is designed to be careful, reversible, and explicit about what it changes, but it still works with local game files. Read the listed actions before applying them, keep your own backups for saves you consider important, and use the undo flow if you want to restore quarantined items later.

`Conan Legacy Doctor` is an unofficial community utility. It is not affiliated with or endorsed by Funcom, Valve, Steam, or the Conay project.

The tool is intentionally conservative:

- It scans first.
- It records every reversible action in a JSON transaction ledger under `%LOCALAPPDATA%\ConanLegacyDoctor`.
- It prefers renames and moves over deletion.
- It only rewrites `Engine.ini` after backing up the exact original file and storing hashes needed for conflict-aware restore.
- It does **not** modify Conan save databases automatically. Save DB quarantine is an explicit opt-in repair action.

## What It Checks

- Steam-installed Conan roots discovered from common Steam library metadata, or a game root supplied explicitly with `-GameRoot`.
- Side-by-side Conay-style installs such as `Conan Exiles Enhanced` and `Conan Exiles Legacy`.
- `ConanSandbox\Mods\modlist.txt`, which Funcom called out as a startup/crash contributor after Enhanced launched.
- `ConanSandbox\Saved\Config\WindowsNoEditor\Engine.ini` for stale `bUseBuildIdOverride` / `BuildIdOverride` lines.
- `ConanSandbox\Saved\ModControllerCache.json`, which can be moved aside reversibly during Legacy cleanup.
- Presence of `game.db`, `game_0.db`, and `dlc_siptah.db` so players can see which save artifacts are present before deciding whether to quarantine them.
- Presence of `ConanSandbox\Saved\SaveGames\TotCustom`, which the doctor backs up automatically before Legacy cleanup.
- Recent log text for a small set of troubleshooting signals such as version mismatch language, pak/mod references, and graphics-driver/D3D12 hints.
- The nearby Steam app manifest when one is available, including any detected beta key and a confidence-rated branch classification.
- `modlist.txt` entries that point at files which are no longer present at their recorded paths.

## Native GUI

Launch the desktop interface with:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.Gui.ps1
```

For players who should not need to touch PowerShell directly, `Start-LegacyDoctor.cmd` is a simple double-click launcher for the same desktop UI.

The GUI is WPF-based and stays native to Windows. It provides:

- install-folder selection and scan results,
- a guided chooser when both Enhanced and Legacy installs are present,
- confidence-rated branch status display,
- guided reversible Legacy preparation,
- optional save DB quarantine for a fresh vanilla boot test,
- a native vanilla launch button for the selected install,
- readable `Actions` browsing and undo,
- ZIP support bundle export.

## Compiled Windows App

The repository now includes a .NET 10 WPF application:

- `src\ConanLegacyDoctor.Core` - compiled repair engine and transaction logic
- `src\ConanLegacyDoctor.App` - desktop executable
- `tests\ConanLegacyDoctor.Core.Smoke` - compiled smoke test that exercises scan, quarantine, launch planning, and restore

Build it with:

```powershell
dotnet build .\ConanLegacyDoctor.slnx -c Release
```

Publish a self-contained `win-x64` executable and checksum file with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1
```

The publish output goes to:

```text
artifacts\publish\win-x64
```

The current published output includes:

- `ConanLegacyDoctor.exe`
- `SHA256SUMS.txt`

The executable manifest is `asInvoker`, so the app does not request admin rights by default. Release-signing, checksum regeneration after signing, and GitHub Release packaging are documented in `docs\RELEASE.md`.

## Commands

Run from PowerShell. If the machine blocks local scripts by policy, use a one-process bypass:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.ps1 `
  -Action scan `
  -GameRoot "D:\SteamLibrary\steamapps\common\Conan Exiles Enhanced"
```

Apply the baseline reversible cleanup:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.ps1 `
  -Action prepare-legacy `
  -GameRoot "D:\SteamLibrary\steamapps\common\Conan Exiles Enhanced"
```

That baseline mode:

- Creates a rotating TotCustom backup first when `ConanSandbox\Saved\SaveGames\TotCustom` exists.
- Moves `modlist.txt` into the transaction quarantine area if it exists.
- Removes only the two stale build override lines from `Engine.ini`, if present, while preserving an exact backup for restore.
- Moves `ModControllerCache.json` aside reversibly when present.

For a stronger clean-room test, add one or both explicit switches:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.ps1 `
  -Action prepare-legacy `
  -GameRoot "D:\SteamLibrary\steamapps\common\Conan Exiles Enhanced" `
  -QuarantineModsDirectory `
  -ResetClientConfig
```

Those opt-in switches:

- Move the entire `Mods` directory aside and create a temporary empty replacement.
- Move `Saved\Config\WindowsNoEditor` aside and create a temporary empty replacement.

Both actions are transaction-backed and reversible.

If Legacy may be trying to open local databases that were touched after an Enhanced launch, add the save quarantine switch:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.ps1 `
  -Action prepare-legacy `
  -GameRoot "D:\SteamLibrary\steamapps\common\Conan Exiles Enhanced" `
  -QuarantineSaveDatabases
```

That opt-in action:

- creates a byte-for-byte safety copy first,
- moves `game.db`, `game_0.db`, and `dlc_siptah.db` into the recorded action quarantine area when present,
- leaves the selected install without those local DB files so the game can create fresh ones during a vanilla repair boot,
- restores the originals through `Undo Selected` or the CLI `restore` action.

After preparation, launch the selected install in vanilla test mode:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.ps1 `
  -Action launch-vanilla `
  -GameRoot "D:\SteamLibrary\steamapps\common\Conan Exiles Enhanced"
```

The doctor refuses to call this a vanilla launch while `ConanSandbox\Mods\modlist.txt` is still active. It prefers the selected install's direct executable and only falls back to the Steam protocol when the nearby Steam manifest confirms that the selected folder is the Steam-managed target.

List recorded actions:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.ps1 -Action actions
```

Undo an action record:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.ps1 `
  -Action restore `
  -TransactionId "20260512T182233Z-8A12F1C4"
```

If a player or launcher has changed a staged file after the doctor ran, restore prefers to stop and report the conflict rather than overwrite newer local work.

When no `-GameRoot` is provided and more than one Conan install is found, the CLI asks which install to inspect or repair instead of failing. The GUI shows a native chooser for the same case.

TotCustom backups are stored under:

```text
%LOCALAPPDATA%\ConanLegacyDoctor\backups\TotCustom
```

The doctor keeps three rotating ZIP slots per detected install path, mirroring the conservative rolling-backup approach used by Conay.
Undo restores the reversible cleanup steps; the ZIP backups remain available on purpose.

Create a support bundle:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.ps1 `
  -Action support-bundle `
  -GameRoot "D:\SteamLibrary\steamapps\common\Conan Exiles Enhanced" `
  -DestinationPath "D:\Temp\ConanLegacyDoctor-support.zip"
```

Recent log tails and config snapshots are explicit opt-ins:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\LegacyDoctor.ps1 `
  -Action support-bundle `
  -GameRoot "D:\SteamLibrary\steamapps\common\Conan Exiles Enhanced" `
  -DestinationPath "D:\Temp\ConanLegacyDoctor-support.zip" `
  -IncludeRecentLogs `
  -IncludeConfigSnapshots
```

Support bundles never include save database files. They include human-readable action records rather than raw internal ledger details.

## JSON Output

Every command supports:

```powershell
-OutputFormat Json
```

That is useful for bug reports, issue templates, or a future GUI wrapper.

## Design Notes

`Legacy Doctor` is deliberately not a save converter and not a branch downloader. It is a reversible local-state repair and triage tool for cases where switching between Enhanced and Legacy leaves startup or connection-related residue behind.

The first release focuses on:

1. predictable scan output,
2. transaction-safe cleanup,
3. exact restore behavior,
4. keeping player saves out of automated mutation,
5. making save-file isolation explicit, reversible, and player-controlled.

## Development

Repository layout:

- `LegacyDoctor.ps1` - CLI entry point
- `LegacyDoctor.Gui.ps1` - native WPF desktop interface
- `src\ConanLegacyDoctor.psm1` - implementation module
- `tests\ConanLegacyDoctor.Tests.ps1` - Pester tests

Suggested validation:

```powershell
Invoke-Pester .\tests
```
