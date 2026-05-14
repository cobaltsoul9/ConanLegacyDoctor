# Conan Legacy Doctor

`Conan Legacy Doctor` is a Windows desktop utility, with matching PowerShell tooling in the repository, for players who switch from Conan Exiles Enhanced back to the Legacy branch and then hit startup hangs, black screens, or mod/config residue that makes the return path unreliable.

## Downloads

- Source code is available in this repository.
- Current public Windows release: `v0.1.5`.
- The latest tested Windows executable build is produced locally at `artifacts\publish\win-x64\ConanLegacyDoctor.exe`.
- Current local build SHA-256: `6b22a2e8d6d26e61bd69c9b5260abffa536a42f50a1a57a9f7e7d9a498b3fb43`.
- Downloadable Windows builds are published through GitHub Releases. The executable is currently unsigned.
- Since an unsigned executable can trigger Windows reputation warnings, the repository also keeps the source, checksum file, and transparent PowerShell entry points available for inspection.

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
- `ConanSandbox\Saved\ModControllerCache.json`, which can be moved aside reversibly during cleanup.
- Presence of `game.db`, `game_0.db`, and `dlc_siptah.db` so players can see which save artifacts are present before deciding whether to quarantine them.
- Presence of `ConanSandbox\Saved\SaveGames\TotCustom`, which the doctor backs up automatically before cleanup.
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

- a `Start Here` assessment that recommends the best branch-switch or troubleshooting path it can identify,
- a guided branch-switch assistant that parks and restores branch folders reversibly while Steam catches up,
- Workshop mod folder preservation during branch switching, so Steam uninstall does not remove the local Workshop cache before rediscovery,
- on-screen Steam screenshots during uninstall and branch-selection steps,
- branch-switch guidance that explains `parked` and `live` folders in plain language,
- short plain-language explanations of actions, quarantine, and vanilla launch,
- tips for what to try next only if the first clean attempt does not help,
- a `ToT Saves` tab for loading dated TotCustom backups into the currently selected install,
- the ability to pull TotCustom directly from a detected live Enhanced install when one is available,
- install-folder selection and scan results,
- a guided chooser when both Enhanced and Legacy installs are present,
- confidence-rated branch status display,
- guided reversible preparation,
- optional save DB quarantine for a fresh vanilla boot test,
- a native vanilla launch button for the selected install,
- a Steam file-validation shortcut for the actively managed Conan install,
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

- Creates a preserved-first plus rolling recent TotCustom backup set when `ConanSandbox\Saved\SaveGames\TotCustom` exists.
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

The doctor keeps the first TotCustom backup it ever makes for a detected install, plus eight rolling recent ZIP slots after that. This preserves an oldest baseline while still keeping a useful run of newer snapshots.
Undo restores the reversible cleanup steps; the ZIP backups remain available on purpose.

The desktop app also exposes those dated TotCustom backups in the `ToT Saves` tab. Loading one into the currently selected install first moves that install's existing `TotCustom` folder aside in a recorded action, then copies in the chosen backup. If a separate Enhanced install is detected and it has live `TotCustom` data, the same tab can load from that folder as well.

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

## Experimental Steam Folder Swap Helper

The repository also includes:

```text
scripts\Switch-ConanManagedInstall.ps1
```

This is a cautious support script for side-by-side installs where one Conan folder is parked as `Conan Exiles Enhanced` and the other needs to become Steam's managed `Conan Exiles` folder before a beta-branch repair or verify pass.

It is intentionally staged:

- `Inspect` only reports what Steam and the folders currently look like.
- `CaptureState` copies the current Conan app manifest and writes a timestamped JSON snapshot for later comparison.
- `StageLegacyAsManaged` and `RestoreEnhancedAsManaged` default to dry-run output.
- Actual renames happen only when `-Apply` is added.
- The script can request a normal Steam shutdown before renaming and can reopen Steam afterward with `-RestartSteam`.
- It still refuses to continue if Steam or Conan does not actually exit.
- The script also refuses when Steam's manifest still shows an unfinished queued Conan download/update.

Inspect current state:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Switch-ConanManagedInstall.ps1 -Action Inspect
```

Capture the current Steam manifest and folder state before any experiment:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Switch-ConanManagedInstall.ps1 -Action CaptureState
```

Snapshots are written under:

```text
artifacts\steam-swap-snapshots
```

Preview restoring Enhanced to the Steam-managed folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Switch-ConanManagedInstall.ps1 -Action RestoreEnhancedAsManaged
```

Only after Steam has fully finished its download or repair work and Steam has been closed, apply the actual folder swap:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Switch-ConanManagedInstall.ps1 `
  -Action RestoreEnhancedAsManaged `
  -RestartSteam `
  -Apply
```

The reason for this care is simple: Steam validates and updates whichever folder it currently treats as the installed Conan target. Showing Steam the wrong folder at the wrong time can cause the exact large replacement download this workflow is meant to avoid.

For later rediscovery experiments, preserve the full `appmanifest_440900.acf` snapshot rather than trying to hand-edit individual beta or queue fields. Steam branch requests, currently mounted branch data, installed depot state, and transient update progress live together there; the helper treats that manifest as evidence to capture, not as a safe file to rewrite manually.

The helper does not try to switch Conan's Steam branch automatically. Valve documents branch selection through the Steam client UI, or through Steamworks APIs used by the game itself, not as a general-purpose desktop command for external tools. After the folder move, the script tells the user exactly which branch to choose in Steam before they continue.

## Guided Steam Rediscovery Flow

After testing, the more reliable branch-switch recovery path is the Steam rediscovery flow in:

```text
scripts\Guide-ConanSteamRediscovery.ps1
```

This script does not edit app manifests. It guides the user through the workflow that lets Steam relink an already-present branch folder:

1. park the currently managed `Conan Exiles` folder under a branch-specific name,
2. park Conan's Workshop content and Workshop metadata if they are present,
3. tell the user to uninstall Conan in Steam,
4. check whether Steam now considers it uninstalled,
5. tell the user to choose the desired branch while it is uninstalled,
6. expose the matching parked folder back as `Conan Exiles`,
7. restore the parked Workshop content before Steam install/verification,
8. tell the user to press Install so Steam can discover and verify the existing files.

Check the current state:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Guide-ConanSteamRediscovery.ps1 `
  -Step Status `
  -TargetBranch Enhanced
```

Preview the first rename step:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Guide-ConanSteamRediscovery.ps1 `
  -Step PrepareUninstall `
  -TargetBranch Enhanced
```

Apply it only when the printed folders and target branch are correct:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Guide-ConanSteamRediscovery.ps1 `
  -Step PrepareUninstall `
  -TargetBranch Enhanced `
  -Apply
```

The later stages are:

```text
ConfirmUninstalled
ExposeTargetForInstall
CheckAfterInstall
```

Run each stage only when the script tells you to. Steam Support's own recovery guidance relies on the same principle: when Steam thinks a game is not installed, pressing Install can make it rediscover files already present in the correct library folder rather than downloading everything again.

## Twilight Mire Mod Pak Checker

The repository includes a reference manifest built from the currently observed Twilight Mire Legacy mod list on this machine:

```text
reference\TwilightMire.Legacy.ModPakManifest.json
```

To check whether a player's local Workshop `.pak` files match that reference:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-ModPakManifest.ps1 `
  -ManifestPath .\reference\TwilightMire.Legacy.ModPakManifest.json `
  -WorkshopRoot "C:\Program Files (x86)\Steam\steamapps\workshop\content\440900"
```

The checker reports missing files, SHA-256 mismatches, and size mismatches per mod entry.

To regenerate a manifest from a known-good `modlist.txt`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-ModPakManifest.ps1 `
  -ModListPath "C:\Program Files (x86)\Steam\steamapps\common\Conan Exiles\ConanSandbox\Mods\modlist.txt" `
  -OutputPath .\reference\TwilightMire.Legacy.ModPakManifest.json
```

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
