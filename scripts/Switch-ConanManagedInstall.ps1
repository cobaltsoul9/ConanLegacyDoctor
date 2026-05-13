[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'CaptureState', 'StageLegacyAsManaged', 'RestoreEnhancedAsManaged')]
    [string]$Action = 'Inspect',
    [string]$SteamAppsRoot = 'C:\Program Files (x86)\Steam\steamapps',
    [string]$LegacyFolderName = 'Conan Exiles Legacy',
    [string]$EnhancedFolderName = 'Conan Exiles Enhanced',
    [string]$ManagedFolderName = 'Conan Exiles',
    [string]$SnapshotRoot,
    [switch]$RestartSteam,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    [System.IO.Path]::GetFullPath($Path)
}

function Get-ProcessNames {
    @(Get-Process steam, steamwebhelper, ConanSandbox, ConanSandbox-Win64-Shipping -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty ProcessName -Unique |
        Sort-Object)
}

function Get-SteamExecutablePath {
    $candidates = @(
        'C:\Program Files (x86)\Steam\steam.exe',
        'C:\Program Files\Steam\steam.exe'
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $null
}

function Get-ManifestInfo {
    param([Parameter(Mandatory)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return [pscustomobject]@{
            Exists = $false
            InstallDir = $null
            BetaKey = $null
            MountedBetaKey = $null
            StateFlags = $null
            TargetBuildId = $null
            BytesToDownload = $null
            BytesDownloaded = $null
            FullValidateAfterNextUpdate = $null
        }
    }

    $content = Get-Content -LiteralPath $ManifestPath -Raw
    [pscustomobject]@{
        Exists = $true
        InstallDir = if ($content -match '"installdir"\s+"([^"]+)"') { $Matches[1] } else { $null }
        BetaKey = if ($content -match '"BetaKey"\s+"([^"]+)"') { $Matches[1] } else { $null }
        MountedBetaKey = if ($content -match '"MountedConfig"[\s\S]*?"BetaKey"\s+"([^"]+)"') { $Matches[1] } else { $null }
        StateFlags = if ($content -match '"StateFlags"\s+"([^"]+)"') { $Matches[1] } else { $null }
        TargetBuildId = if ($content -match '"TargetBuildID"\s+"([^"]+)"') { $Matches[1] } else { $null }
        BytesToDownload = if ($content -match '"BytesToDownload"\s+"([^"]+)"') { [int64]$Matches[1] } else { $null }
        BytesDownloaded = if ($content -match '"BytesDownloaded"\s+"([^"]+)"') { [int64]$Matches[1] } else { $null }
        FullValidateAfterNextUpdate = if ($content -match '"FullValidateAfterNextUpdate"\s+"([^"]+)"') { $Matches[1] } else { $null }
    }
}

function Get-InstallShape {
    param([Parameter(Mandatory)][string]$Path)

    $rootExists = Test-Path -LiteralPath $Path -PathType Container
    $legacyExe = Join-Path $Path 'ConanSandbox\Binaries\Win64\ConanSandbox.exe'
    $enhancedExe = Join-Path $Path 'ConanSandbox\Binaries\Win64\ConanSandbox-Win64-Shipping.exe'

    [pscustomobject]@{
        Path = $Path
        Exists = $rootExists
        HasLegacyExe = $rootExists -and (Test-Path -LiteralPath $legacyExe -PathType Leaf)
        HasEnhancedExe = $rootExists -and (Test-Path -LiteralPath $enhancedExe -PathType Leaf)
    }
}

function Write-Inspection {
    param(
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)]$Managed,
        [Parameter(Mandatory)]$Legacy,
        [Parameter(Mandatory)]$Enhanced,
        [string[]]$RunningProcesses = @()
    )

    Write-Output 'Conan managed-folder swap inspection'
    Write-Output ''
    Write-Output ("Steam or Conan processes running: {0}" -f ($(if ($RunningProcesses.Count -eq 0) { 'none detected' } else { $RunningProcesses -join ', ' })))
    Write-Output ("Steam manifest present: {0}" -f $Manifest.Exists)
    Write-Output ("Manifest install dir: {0}" -f ($(if ($Manifest.InstallDir) { $Manifest.InstallDir } else { '<unknown>' })))
    Write-Output ("Requested beta key: {0}" -f ($(if ($Manifest.BetaKey) { $Manifest.BetaKey } else { '<none>' })))
    Write-Output ("Mounted beta key: {0}" -f ($(if ($Manifest.MountedBetaKey) { $Manifest.MountedBetaKey } else { '<none>' })))
    Write-Output ("Target build id: {0}" -f ($(if ($Manifest.TargetBuildId) { $Manifest.TargetBuildId } else { '<none>' })))
    Write-Output ("Bytes queued to download: {0}" -f ($(if ($null -ne $Manifest.BytesToDownload) { $Manifest.BytesToDownload } else { '<unknown>' })))
    Write-Output ("Full validate after next update: {0}" -f ($(if ($Manifest.FullValidateAfterNextUpdate) { $Manifest.FullValidateAfterNextUpdate } else { '<unknown>' })))
    Write-Output ("Branch transition pending: {0}" -f ($(if ($Manifest.BetaKey -and $Manifest.MountedBetaKey -and $Manifest.BetaKey -ne $Manifest.MountedBetaKey) { 'yes' } else { 'no or unknown' })))
    Write-Output ''
    Write-Output ("Managed folder: {0} | exists={1} | legacyExe={2} | enhancedExe={3}" -f $Managed.Path, $Managed.Exists, $Managed.HasLegacyExe, $Managed.HasEnhancedExe)
    Write-Output ("Legacy folder:  {0} | exists={1} | legacyExe={2} | enhancedExe={3}" -f $Legacy.Path, $Legacy.Exists, $Legacy.HasLegacyExe, $Legacy.HasEnhancedExe)
    Write-Output ("Enhanced folder:{0} | exists={1} | legacyExe={2} | enhancedExe={3}" -f $Enhanced.Path, $Enhanced.Exists, $Enhanced.HasLegacyExe, $Enhanced.HasEnhancedExe)
}

function Save-StateSnapshot {
    param(
        [Parameter(Mandatory)][string]$SnapshotRootPath,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)]$Managed,
        [Parameter(Mandatory)]$Legacy,
        [Parameter(Mandatory)]$Enhanced,
        [string[]]$RunningProcesses = @()
    )

    $timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $destination = Join-Path (Get-FullPath -Path $SnapshotRootPath) $timestamp
    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    if (Test-Path -LiteralPath $ManifestPath -PathType Leaf) {
        Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $destination 'appmanifest_440900.acf') -Force
    }

    $state = [pscustomobject]@{
        CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        SteamAppsRoot = $steamAppsRootFull
        ManifestPath = $ManifestPath
        Manifest = $Manifest
        RunningProcesses = $RunningProcesses
        Folders = [pscustomobject]@{
            Managed = $Managed
            Legacy = $Legacy
            Enhanced = $Enhanced
        }
    }

    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'state.json') -Encoding UTF8
    @(
        'Conan Steam folder swap snapshot',
        '',
        'This snapshot preserves the Steam manifest and the folder-state reading taken before any experiment.',
        'Do not treat it as an automatic restore point for live Steam processes.',
        'Use it to compare what changed after a branch switch, verify, or install rediscovery attempt.'
    ) | Set-Content -LiteralPath (Join-Path $destination 'README.txt') -Encoding UTF8

    Write-Output ''
    Write-Output ("Snapshot created: {0}" -f $destination)
}

function Assert-ManifestIsStableEnough {
    param(
        [Parameter(Mandatory)]$Manifest
    )

    if ($Manifest.Exists -and $Manifest.BytesToDownload -gt 0 -and $Manifest.BytesDownloaded -lt $Manifest.BytesToDownload) {
        throw "Steam's manifest shows a Conan download/update is not finished. Let Steam finish first, then close Steam, then run the swap."
    }
}

function Stop-SteamCleanly {
    param([Parameter(Mandatory)][bool]$ShouldApply)

    $steamProcesses = @(Get-Process steam, steamwebhelper -ErrorAction SilentlyContinue)
    if ($steamProcesses.Count -eq 0) {
        Write-Output 'Steam is already closed.'
        return
    }

    Write-Output 'Steam is running and must be closed before folder renames.'
    if (-not $ShouldApply) {
        Write-Output 'Dry run: would request Steam shutdown and wait for it to exit.'
        return
    }

    $steamExe = Get-SteamExecutablePath
    if (-not $steamExe) {
        throw 'Steam is running, but steam.exe could not be located for a clean shutdown request.'
    }

    Write-Output 'Requesting Steam shutdown through steam.exe -shutdown...'
    Start-Process -FilePath $steamExe -ArgumentList '-shutdown' -WindowStyle Hidden | Out-Null

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 750
        $remaining = @(Get-Process steam, steamwebhelper -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            Write-Output 'Steam closed.'
            return
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw 'Steam did not exit cleanly within 45 seconds. Close Steam manually, then rerun the script.'
}

function Start-SteamAgain {
    param([Parameter(Mandatory)][bool]$ShouldApply)

    if (-not $RestartSteam) {
        return
    }

    $steamExe = Get-SteamExecutablePath
    if (-not $steamExe) {
        Write-Output 'Steam relaunch was requested, but steam.exe was not found automatically.'
        return
    }

    if (-not $ShouldApply) {
        Write-Output ("Dry run: would relaunch Steam from {0}" -f $steamExe)
        return
    }

    Start-Process -FilePath $steamExe -WindowStyle Hidden | Out-Null
    Write-Output ("Steam relaunch requested: {0}" -f $steamExe)
}

function Write-BranchInstructions {
    param([Parameter(Mandatory)][string]$RequestedAction)

    Write-Output ''
    switch ($RequestedAction) {
        'StageLegacyAsManaged' {
            Write-Output 'Next Steam step: if Steam is not already set to the Legacy beta, open Conan Exiles Properties > Game Versions & Betas and choose the Legacy branch.'
            Write-Output 'Then let Steam settle or verify files against the now-managed Legacy folder.'
        }
        'RestoreEnhancedAsManaged' {
            Write-Output 'Next Steam step: switch Conan Exiles back to the default / Enhanced branch in Properties > Game Versions & Betas.'
            Write-Output 'Then let Steam settle or verify files against the now-managed Enhanced folder.'
        }
    }
}

function Assert-DirectoryMove {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required source folder is missing: $Source"
    }

    if (Test-Path -LiteralPath $Destination) {
        throw "Destination already exists and will not be overwritten: $Destination"
    }
}

function Assert-SwapPlan {
    param(
        [Parameter(Mandatory)][string]$FirstSource,
        [Parameter(Mandatory)][string]$SecondSource,
        [Parameter(Mandatory)][string]$FinalDestination,
        [Parameter(Mandatory)][string]$TempPath
    )

    if (-not (Test-Path -LiteralPath $FirstSource -PathType Container)) {
        throw "Required source folder is missing: $FirstSource"
    }

    if (-not (Test-Path -LiteralPath $SecondSource -PathType Container)) {
        throw "Required source folder is missing: $SecondSource"
    }

    if (Test-Path -LiteralPath $TempPath) {
        throw "Temporary swap folder already exists and must be resolved first: $TempPath"
    }

    if (Test-Path -LiteralPath $FinalDestination) {
        throw "Final destination already exists and will not be overwritten: $FinalDestination"
    }
}

function Move-FolderSafely {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][bool]$ShouldApply
    )

    Write-Output ("Move: {0}" -f $Source)
    Write-Output ("  To: {0}" -f $Destination)
    if ($ShouldApply) {
        Move-Item -LiteralPath $Source -Destination $Destination
    }
}

$steamAppsRootFull = Get-FullPath -Path $SteamAppsRoot
$scriptRoot = Split-Path -Parent (Get-FullPath -Path $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($SnapshotRoot)) {
    $SnapshotRoot = Join-Path $scriptRoot '..\artifacts\steam-swap-snapshots'
}
$commonRoot = Join-Path $steamAppsRootFull 'common'
$manifestPath = Join-Path $steamAppsRootFull 'appmanifest_440900.acf'
$managedPath = Join-Path $commonRoot $ManagedFolderName
$legacyPath = Join-Path $commonRoot $LegacyFolderName
$enhancedPath = Join-Path $commonRoot $EnhancedFolderName
$swapTempPath = Join-Path $commonRoot 'Conan Exiles.__swap_temp__'

$manifest = Get-ManifestInfo -ManifestPath $manifestPath
$runningProcesses = @(Get-ProcessNames)
$managed = Get-InstallShape -Path $managedPath
$legacy = Get-InstallShape -Path $legacyPath
$enhanced = Get-InstallShape -Path $enhancedPath

Write-Inspection -Manifest $manifest -Managed $managed -Legacy $legacy -Enhanced $enhanced -RunningProcesses $runningProcesses

if ($Action -eq 'Inspect') {
    return
}

if ($Action -eq 'CaptureState') {
    Save-StateSnapshot `
        -SnapshotRootPath $SnapshotRoot `
        -ManifestPath $manifestPath `
        -Manifest $manifest `
        -Managed $managed `
        -Legacy $legacy `
        -Enhanced $enhanced `
        -RunningProcesses $runningProcesses
    return
}

Write-Output ''
Write-Output ("Requested action: {0}" -f $Action)
Write-Output ("Execution mode: {0}" -f ($(if ($Apply) { 'APPLY' } else { 'DRY RUN' })))
Write-Output ''

Assert-ManifestIsStableEnough -Manifest $manifest
Stop-SteamCleanly -ShouldApply:$Apply

if ($Apply) {
    $runningProcessesAfterStop = @(Get-ProcessNames)
    if ($runningProcessesAfterStop.Count -gt 0) {
        throw "Steam or Conan is still running after the shutdown step: $($runningProcessesAfterStop -join ', ')"
    }
}

switch ($Action) {
    'StageLegacyAsManaged' {
        Assert-SwapPlan -FirstSource $managedPath -SecondSource $legacyPath -FinalDestination $enhancedPath -TempPath $swapTempPath

        Move-FolderSafely -Source $managedPath -Destination $swapTempPath -ShouldApply:$Apply
        Move-FolderSafely -Source $legacyPath -Destination $managedPath -ShouldApply:$Apply
        Move-FolderSafely -Source $swapTempPath -Destination $enhancedPath -ShouldApply:$Apply
    }

    'RestoreEnhancedAsManaged' {
        Assert-SwapPlan -FirstSource $managedPath -SecondSource $enhancedPath -FinalDestination $legacyPath -TempPath $swapTempPath

        Move-FolderSafely -Source $managedPath -Destination $swapTempPath -ShouldApply:$Apply
        Move-FolderSafely -Source $enhancedPath -Destination $managedPath -ShouldApply:$Apply
        Move-FolderSafely -Source $swapTempPath -Destination $legacyPath -ShouldApply:$Apply
    }
}

Write-Output ''
Write-Output ($(if ($Apply) { 'Folder moves completed.' } else { 'Dry run complete. No folders were renamed.' }))
Start-SteamAgain -ShouldApply:$Apply
Write-BranchInstructions -RequestedAction $Action
