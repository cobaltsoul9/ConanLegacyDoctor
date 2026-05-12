[CmdletBinding()]
param(
    [ValidateSet('scan', 'prepare-legacy', 'restore', 'actions', 'transactions', 'support-bundle', 'launch-vanilla')]
    [string]$Action = 'scan',

    [string]$GameRoot,

    [string]$TransactionId,

    [switch]$QuarantineModsDirectory,

    [switch]$ResetClientConfig,

    [switch]$QuarantineSaveDatabases,

    [ValidateSet('Text', 'Json')]
    [string]$OutputFormat = 'Text',

    [switch]$ForceRestore,

    [string]$StateRoot,

    [string]$DestinationPath,

    [switch]$IncludeRecentLogs,

    [switch]$IncludeConfigSnapshots
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'src\ConanLegacyDoctor.psm1'
Import-Module $modulePath -Force

function Write-JsonResult {
    param([Parameter(Mandatory)]$Value)

    $Value | ConvertTo-Json -Depth 12
}

function Write-TextScan {
    param([Parameter(Mandatory)]$Scan)

    $branchDisplay = switch ($Scan.Branch.BranchMode) {
        'Legacy' { 'Steam Legacy branch' }
        'EnhancedOrDefault' { 'Steam default / Enhanced branch' }
        'LegacySideBySideCopy' { 'Likely side-by-side Legacy copy' }
        'DetachedSideBySideCopy' { 'Detached side-by-side copy' }
        default { $Scan.Branch.BranchMode }
    }

    Write-Output "Game root: $($Scan.GameRoot)"
    Write-Output "Scanned at: $($Scan.ScannedAtUtc)"
    Write-Output "Branch: $branchDisplay ($($Scan.Branch.Confidence))"
    Write-Output ''

    foreach ($finding in $Scan.Findings) {
        $pathText = if ($finding.Path) { " [$($finding.Path)]" } else { '' }
        Write-Output ("[{0}] {1}{2}" -f $finding.Severity.ToUpperInvariant(), $finding.Message, $pathText)
    }

    if ($Scan.Findings.Count -eq 0) {
        Write-Output '[INFO] No findings were produced.'
    }
}

function Write-TextTransaction {
    param([Parameter(Mandatory)]$Transaction)

    Write-Output "Action record: $($Transaction.Id)"
    Write-Output "Action type: $($Transaction.Action)"
    Write-Output "Game root: $($Transaction.GameRoot)"
    Write-Output "Created: $($Transaction.CreatedAtUtc)"
    Write-Output "Status: $($Transaction.Status)"
    Write-Output ''

    foreach ($operation in $Transaction.Operations) {
        switch ($operation.Type) {
            'MovePath' {
                Write-Output ("[{0}] Moved '{1}' to '{2}'." -f $operation.Status.ToUpperInvariant(), $operation.Data.SourcePath, $operation.Data.DestinationPath)
            }
            'CreateDirectory' {
                Write-Output ("[{0}] Created empty placeholder folder '{1}'." -f $operation.Status.ToUpperInvariant(), $operation.Data.Path)
            }
            'RewriteTextFile' {
                Write-Output ("[{0}] Backed up and cleaned '{1}' by removing stale build override lines." -f $operation.Status.ToUpperInvariant(), $operation.Data.Path)
            }
            'CopyPath' {
                Write-Output ("[{0}] Created safety copy of '{1}' at '{2}'." -f $operation.Status.ToUpperInvariant(), $operation.Data.SourcePath, $operation.Data.DestinationPath)
            }
            'CreateBackupArchive' {
                Write-Output ("[{0}] Created TotCustom backup set; newest archive is '{1}'." -f $operation.Status.ToUpperInvariant(), $operation.Data.BackupPath)
            }
            default {
                Write-Output ("[{0}] {1}" -f $operation.Status.ToUpperInvariant(), $operation.Reason)
            }
        }
    }

    if ($Transaction.Warnings.Count -gt 0) {
        Write-Output ''
        foreach ($warning in $Transaction.Warnings) {
            Write-Output "[WARN] $warning"
        }
    }
}

function Write-TextBundle {
    param([Parameter(Mandatory)]$Bundle)

    Write-Output "Support bundle: $($Bundle.DestinationPath)"
    Write-Output "Game root: $($Bundle.GameRoot)"
    Write-Output "Recent logs included: $($Bundle.IncludeRecentLogs)"
    Write-Output "Config snapshots included: $($Bundle.IncludeConfigSnapshots)"
    Write-Output "Staged file count: $($Bundle.StagedFileCount)"
}

function Write-TextVanillaLaunch {
    param([Parameter(Mandatory)]$Launch)

    Write-Output "Vanilla launch started for: $($Launch.GameRoot)"
    Write-Output "Branch classification: $($Launch.BranchMode)"
    Write-Output "Launch path: $($Launch.LaunchStrategy)"
    if ($Launch.ExecutablePath) {
        Write-Output "Executable: $($Launch.ExecutablePath)"
    }
    else {
        Write-Output "Steam URI: $($Launch.SteamUri)"
    }
}

function Resolve-RequestedGameRoot {
    param([string]$RequestedGameRoot)

    if (-not [string]::IsNullOrWhiteSpace($RequestedGameRoot)) {
        return $RequestedGameRoot
    }

    $candidates = @(Get-LegacyDoctorInstallCandidates)
    if ($candidates.Count -eq 0) {
        return $null
    }

    if ($candidates.Count -eq 1) {
        return $candidates[0].Path
    }

    Write-Output 'More than one Conan install was found. This is normal for side-by-side Enhanced and Legacy setups.'
    for ($index = 0; $index -lt $candidates.Count; $index++) {
        $candidate = $candidates[$index]
        Write-Output ("{0}. {1}" -f ($index + 1), $candidate.DisplayName)
        Write-Output ("   {0}" -f $candidate.Path)
    }

    while ($true) {
        $choice = Read-Host 'Choose the install number to inspect or repair'
        $selectedIndex = 0
        if ([int]::TryParse($choice, [ref]$selectedIndex) -and $selectedIndex -ge 1 -and $selectedIndex -le $candidates.Count) {
            return $candidates[$selectedIndex - 1].Path
        }

        Write-Output 'Enter one of the listed numbers.'
    }
}

switch ($Action) {
    'scan' {
        $scan = Get-LegacyDoctorScan -GameRoot (Resolve-RequestedGameRoot -RequestedGameRoot $GameRoot)
        if ($OutputFormat -eq 'Json') {
            Write-JsonResult -Value $scan
        }
        else {
            Write-TextScan -Scan $scan
        }
    }

    'prepare-legacy' {
        $transaction = Invoke-LegacyPreparation `
            -GameRoot (Resolve-RequestedGameRoot -RequestedGameRoot $GameRoot) `
            -QuarantineModsDirectory:$QuarantineModsDirectory `
            -ResetClientConfig:$ResetClientConfig `
            -QuarantineSaveDatabases:$QuarantineSaveDatabases `
            -StateRoot $StateRoot

        if ($OutputFormat -eq 'Json') {
            Write-JsonResult -Value $transaction
        }
        else {
            Write-TextTransaction -Transaction $transaction
        }
    }

    'restore' {
        if ([string]::IsNullOrWhiteSpace($TransactionId)) {
            throw 'Restore requires -TransactionId.'
        }

        $transaction = Restore-LegacyDoctorTransaction `
            -TransactionId $TransactionId `
            -Force:$ForceRestore `
            -StateRoot $StateRoot

        if ($OutputFormat -eq 'Json') {
            Write-JsonResult -Value $transaction
        }
        else {
            Write-TextTransaction -Transaction $transaction
        }
    }

    { $_ -in @('actions', 'transactions') } {
        $actions = Get-LegacyDoctorActions -StateRoot $StateRoot
        if ($OutputFormat -eq 'Json') {
            Write-JsonResult -Value $actions
        }
        else {
            foreach ($actionRecord in $actions) {
                Write-Output ("{0}  {1}  {2}" -f $actionRecord.Id, $actionRecord.Status, $actionRecord.Summary)
            }
        }
    }

    'support-bundle' {
        $bundle = Export-LegacySupportBundle `
            -GameRoot (Resolve-RequestedGameRoot -RequestedGameRoot $GameRoot) `
            -DestinationPath $DestinationPath `
            -IncludeRecentLogs:$IncludeRecentLogs `
            -IncludeConfigSnapshots:$IncludeConfigSnapshots `
            -StateRoot $StateRoot

        if ($OutputFormat -eq 'Json') {
            Write-JsonResult -Value $bundle
        }
        else {
            Write-TextBundle -Bundle $bundle
        }
    }

    'launch-vanilla' {
        $launch = Start-LegacyDoctorVanillaLaunch `
            -GameRoot (Resolve-RequestedGameRoot -RequestedGameRoot $GameRoot)

        if ($OutputFormat -eq 'Json') {
            Write-JsonResult -Value $launch
        }
        else {
            Write-TextVanillaLaunch -Launch $launch
        }
    }
}
