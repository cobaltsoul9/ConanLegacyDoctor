Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SchemaVersion = 1
$script:ToolName = 'ConanLegacyDoctor'

function Get-UtcNowString {
    [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
}

function Get-TransactionId {
    '{0}-{1}' -f [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'), ([Guid]::NewGuid().ToString('N').Substring(0, 8).ToUpperInvariant())
}

function Get-DoctorStateRoot {
    param([string]$StateRoot)

    if (-not [string]::IsNullOrWhiteSpace($StateRoot)) {
        return [System.IO.Path]::GetFullPath($StateRoot)
    }

    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw 'Unable to resolve LocalApplicationData.'
    }

    return (Join-Path $localAppData $script:ToolName)
}

function Ensure-Directory {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        [void](New-Item -ItemType Directory -Path $Path -Force)
    }
}

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    [System.IO.Path]::GetFullPath($Path)
}

function Test-PathInsideRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    $fullPath = Resolve-FullPath -Path $Path
    $fullRoot = (Resolve-FullPath -Path $Root).TrimEnd('\')
    $comparison = [System.StringComparison]::OrdinalIgnoreCase

    if ($fullPath.Equals($fullRoot, $comparison)) {
        return $true
    }

    return $fullPath.StartsWith(($fullRoot + '\'), $comparison)
}

function Assert-PathInsideRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    if (-not (Test-PathInsideRoot -Path $Path -Root $Root)) {
        throw "Refusing to operate outside the Conan game root: $Path"
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function New-Finding {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][ValidateSet('info', 'warning', 'risk')][string]$Severity,
        [Parameter(Mandatory)][string]$Message,
        [string]$Path,
        [hashtable]$Details
    )

    [pscustomobject]@{
        Id       = $Id
        Severity = $Severity
        Message  = $Message
        Path     = $Path
        Details  = if ($Details) { [pscustomobject]$Details } else { $null }
    }
}

function Get-SteamRootCandidates {
    $candidates = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)

    $registryPaths = @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\Software\WOW6432Node\Valve\Steam',
        'HKLM:\Software\Valve\Steam'
    )

    foreach ($registryPath in $registryPaths) {
        try {
            $entry = Get-ItemProperty -Path $registryPath -ErrorAction Stop
            foreach ($propertyName in @('SteamPath', 'InstallPath')) {
                if ($entry.PSObject.Properties.Name -contains $propertyName) {
                    $value = [string]$entry.$propertyName
                    if (-not [string]::IsNullOrWhiteSpace($value)) {
                        [void]$candidates.Add((Resolve-FullPath -Path $value))
                    }
                }
            }
        }
        catch {
            continue
        }
    }

    foreach ($fallback in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Steam'),
        (Join-Path $env:ProgramFiles 'Steam')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($fallback) -and (Test-Path -LiteralPath $fallback)) {
            [void]$candidates.Add((Resolve-FullPath -Path $fallback))
        }
    }

    return @($candidates)
}

function Get-SteamLibraryRoots {
    $libraries = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($steamRoot in Get-SteamRootCandidates) {
        if (Test-Path -LiteralPath $steamRoot) {
            [void]$libraries.Add($steamRoot)
        }

        $libraryFile = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $libraryFile -PathType Leaf)) {
            continue
        }

        $raw = Get-Content -LiteralPath $libraryFile -Raw
        $patterns = @(
            '"path"\s*"(?<path>[^"]+)"',
            '"\d+"\s*"(?<path>[^"]+)"'
        )

        foreach ($pattern in $patterns) {
            foreach ($match in [regex]::Matches($raw, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                $pathValue = $match.Groups['path'].Value -replace '\\\\', '\'
                if (-not [string]::IsNullOrWhiteSpace($pathValue)) {
                    [void]$libraries.Add((Resolve-FullPath -Path $pathValue))
                }
            }
        }
    }

    return @($libraries)
}

function Get-ConanInstallCandidates {
    $installNames = @('Conan Exiles', 'Conan Exiles Enhanced', 'Conan Exiles Legacy')
    $results = New-Object System.Collections.Generic.List[string]

    foreach ($libraryRoot in Get-SteamLibraryRoots) {
        foreach ($installName in $installNames) {
            $candidate = Join-Path $libraryRoot ("steamapps\common\{0}" -f $installName)
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $results.Add((Resolve-FullPath -Path $candidate))
            }
        }
    }

    return @($results | Sort-Object -Unique)
}

function Resolve-ConanGameRoot {
    param([string]$GameRoot)

    if (-not [string]::IsNullOrWhiteSpace($GameRoot)) {
        $resolved = Resolve-FullPath -Path $GameRoot
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "The supplied Conan game root does not exist: $resolved"
        }
        return $resolved
    }

    $candidates = @(Get-ConanInstallCandidates)
    if ($candidates.Count -eq 1) {
        return $candidates[0]
    }

    if ($candidates.Count -eq 0) {
        throw 'No Conan install was discovered automatically. Re-run with -GameRoot.'
    }

    throw ("Multiple Conan installs were discovered. Re-run with -GameRoot:`n{0}" -f ($candidates -join [Environment]::NewLine))
}

function Get-SteamManifestInfo {
    param([Parameter(Mandatory)][string]$GameRoot)

    $commonRoot = Split-Path -Parent $GameRoot
    if ((Split-Path -Leaf $commonRoot) -ne 'common') {
        return [pscustomobject]@{
            Detected                  = $false
            ManifestPath              = $null
            BetaKey                   = $null
            InstallDir                = $null
            InstallDirMatchesGameRoot = $null
            BranchMode                = 'Unknown'
            Confidence                = 'low'
            Message                   = 'Steam manifest lookup is not available for this folder layout.'
        }
    }

    $steamAppsRoot = Split-Path -Parent $commonRoot
    $manifestPath = Join-Path $steamAppsRoot 'appmanifest_440900.acf'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return [pscustomobject]@{
            Detected                  = $false
            ManifestPath              = $manifestPath
            BetaKey                   = $null
            InstallDir                = $null
            InstallDirMatchesGameRoot = $null
            BranchMode                = 'Unknown'
            Confidence                = 'low'
            Message                   = 'Steam manifest was not found next to this Conan install.'
        }
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw
    $betaMatch = [regex]::Match($manifest, '"betakey"\s*"(?<value>[^"]*)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $installDirMatch = [regex]::Match($manifest, '"installdir"\s*"(?<value>[^"]*)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $buildIdMatch = [regex]::Match($manifest, '"buildid"\s*"(?<value>[^"]*)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    $betaKey = if ($betaMatch.Success) { $betaMatch.Groups['value'].Value } else { $null }
    $installDir = if ($installDirMatch.Success) { $installDirMatch.Groups['value'].Value } else { $null }
    $gameRootLeaf = Split-Path -Leaf $GameRoot
    $installDirMatches = if ($installDir) {
        $installDir.Equals($gameRootLeaf, [System.StringComparison]::OrdinalIgnoreCase)
    }
    else {
        $null
    }

    if ($installDirMatches -eq $false) {
        $folderName = Split-Path -Leaf $GameRoot
        if ($folderName -eq 'Conan Exiles Legacy') {
            $branchMode = 'LegacySideBySideCopy'
            $confidence = 'medium'
            $message = "This folder matches Conay's side-by-side Legacy naming convention. The nearby Steam manifest belongs to '$installDir', not this folder."
        }
        else {
            $branchMode = 'DetachedSideBySideCopy'
            $confidence = 'low'
            $message = "The nearby Steam manifest belongs to '$installDir', not this selected folder, so the selected folder is treated as a detached side-by-side copy."
        }
    }
    elseif ($betaKey -eq 'conan-exiles-legacy') {
        $branchMode = 'Legacy'
        $confidence = 'high'
        $message = 'Steam manifest indicates the Conan Exiles Legacy beta branch.'
    }
    elseif ([string]::IsNullOrWhiteSpace($betaKey)) {
        $branchMode = 'EnhancedOrDefault'
        $confidence = 'high'
        $message = 'Steam manifest has no beta key, which usually means the default Enhanced branch is selected.'
    }
    else {
        $branchMode = 'OtherBeta'
        $confidence = 'medium'
        $message = "Steam manifest uses beta key '$betaKey', which Legacy Doctor does not classify further."
    }

    return [pscustomobject]@{
        Detected                  = $true
        ManifestPath              = $manifestPath
        BetaKey                   = $betaKey
        InstallDir                = $installDir
        InstallDirMatchesGameRoot = $installDirMatches
        BuildId                   = if ($buildIdMatch.Success) { $buildIdMatch.Groups['value'].Value } else { $null }
        BranchMode                = $branchMode
        Confidence                = $confidence
        Message                   = $message
    }
}

function Get-LegacyDoctorInstallCandidates {
    [CmdletBinding()]
    param()

    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($path in Get-ConanInstallCandidates) {
        $branch = Get-SteamManifestInfo -GameRoot $path
        $folderName = Split-Path -Leaf $path
        $label = if ($branch.BranchMode -eq 'LegacySideBySideCopy') {
            "$folderName - likely Conay side-by-side Legacy copy"
        }
        elseif ($branch.BranchMode -eq 'Legacy') {
            "$folderName - Steam Legacy branch"
        }
        elseif ($branch.BranchMode -eq 'EnhancedOrDefault') {
            "$folderName - Steam default/Enhanced branch"
        }
        else {
            "$folderName - branch not confirmed"
        }

        $candidates.Add([pscustomobject]@{
            DisplayName = ("{0} | {1}" -f $label, $path)
            Path        = $path
            FolderName  = $folderName
            Branch      = $branch.BranchMode
            Confidence  = $branch.Confidence
            Message     = $branch.Message
        })
    }

    return $candidates.ToArray()
}

function Get-SteamManifestFinding {
    param([Parameter(Mandatory)]$ManifestInfo)

    if (-not $ManifestInfo.Detected) {
        return New-Finding `
            -Id 'steam.manifest' `
            -Severity 'info' `
            -Message $ManifestInfo.Message `
            -Path $ManifestInfo.ManifestPath `
            -Details @{
                BranchMode = $ManifestInfo.BranchMode
                Confidence = $ManifestInfo.Confidence
            }
    }

    $details = @{
        BetaKey                   = $ManifestInfo.BetaKey
        InstallDir                = $ManifestInfo.InstallDir
        InstallDirMatchesGameRoot = $ManifestInfo.InstallDirMatchesGameRoot
        BuildId                   = $ManifestInfo.BuildId
        BranchMode                = $ManifestInfo.BranchMode
        Confidence                = $ManifestInfo.Confidence
    }

    $message = if ($ManifestInfo.BetaKey) {
        "Steam manifest beta key detected: $($ManifestInfo.BetaKey)"
    }
    else {
        'Steam manifest found; no beta key was detected.'
    }

    return New-Finding `
        -Id 'steam.manifest' `
        -Severity 'info' `
        -Message $message `
        -Path $ManifestInfo.ManifestPath `
        -Details $details
}

function Get-ModListFindings {
    param([Parameter(Mandatory)][string]$GameRoot)

    $findings = New-Object System.Collections.Generic.List[object]
    $modsRoot = Join-Path $GameRoot 'ConanSandbox\Mods'
    if (-not (Test-Path -LiteralPath $modsRoot -PathType Container)) {
        return $findings.ToArray()
    }

    $pakCount = @(Get-ChildItem -LiteralPath $modsRoot -File -Filter '*.pak' -ErrorAction SilentlyContinue).Count
    $findings.Add((New-Finding `
        -Id 'mods.directory' `
        -Severity 'info' `
        -Message "Mods directory exists with $pakCount direct .pak file(s)." `
        -Path $modsRoot `
        -Details @{ DirectPakFileCount = $pakCount }))

    $modlistPath = Join-Path $modsRoot 'modlist.txt'
    if (-not (Test-Path -LiteralPath $modlistPath -PathType Leaf)) {
        return $findings.ToArray()
    }

    $content = Get-Content -LiteralPath $modlistPath -ErrorAction SilentlyContinue
    $entries = @(
        $content |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_.TrimStart('*').Trim() }
    )

    $findings.Add((New-Finding `
        -Id 'mods.modlist' `
        -Severity 'warning' `
        -Message "modlist.txt exists with $($entries.Count) active or staged line(s)." `
        -Path $modlistPath `
        -Details @{ NonEmptyLineCount = $entries.Count }))

    $missingEntries = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $entries) {
        $candidate = if ([System.IO.Path]::IsPathRooted($entry)) {
            $entry
        }
        else {
            Join-Path $modsRoot $entry
        }

        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $missingEntries.Add($entry)
        }
    }

    if ($missingEntries.Count -gt 0) {
        $findings.Add((New-Finding `
            -Id 'mods.modlist-missing-targets' `
            -Severity 'warning' `
            -Message "modlist.txt references $($missingEntries.Count) file(s) that are not currently present at their recorded paths." `
            -Path $modlistPath `
            -Details @{
                MissingCount = $missingEntries.Count
                MissingEntries = $missingEntries.ToArray()
            }))
    }

    return $findings.ToArray()
}

function Get-LogSignalFindings {
    param([Parameter(Mandatory)][string]$GameRoot)

    $logsRoot = Join-Path $GameRoot 'ConanSandbox\Saved\Logs'
    if (-not (Test-Path -LiteralPath $logsRoot -PathType Container)) {
        return @()
    }

    $latestLog = Get-ChildItem -LiteralPath $logsRoot -Filter '*.log' -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if (-not $latestLog) {
        return @()
    }

    $tail = Get-Content -LiteralPath $latestLog.FullName -Tail 1200 -ErrorAction SilentlyContinue
    if (-not $tail) {
        return @()
    }

    $text = $tail -join [Environment]::NewLine
    $signals = @(
        @{ Id = 'logs.version'; Pattern = 'incompatible version|wrong version|buildidoverride'; Severity = 'warning'; Message = 'Recent log text mentions version mismatch or build override language.' },
        @{ Id = 'logs.mods'; Pattern = 'modlist|mounting pak|pak file'; Severity = 'warning'; Message = 'Recent log text mentions mod list or pak mounting activity.' },
        @{ Id = 'logs.memory'; Pattern = 'out of memory|ran out of memory'; Severity = 'warning'; Message = 'Recent log text mentions memory exhaustion.' },
        @{ Id = 'logs.graphics'; Pattern = 'd3d12|driver version'; Severity = 'info'; Message = 'Recent log text mentions D3D12 or graphics-driver language.' }
    )

    $findings = New-Object System.Collections.Generic.List[object]
    foreach ($signal in $signals) {
        if ([regex]::IsMatch($text, $signal.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $findings.Add((New-Finding `
                -Id $signal.Id `
                -Severity $signal.Severity `
                -Message $signal.Message `
                -Path $latestLog.FullName `
                -Details @{ LogFile = $latestLog.Name }))
        }
    }

    return $findings.ToArray()
}

function Get-LegacyDoctorScan {
    [CmdletBinding()]
    param([string]$GameRoot)

    $resolvedGameRoot = Resolve-ConanGameRoot -GameRoot $GameRoot
    $findings = New-Object System.Collections.Generic.List[object]

    $manifestInfo = Get-SteamManifestInfo -GameRoot $resolvedGameRoot
    $manifestFinding = Get-SteamManifestFinding -ManifestInfo $manifestInfo
    if ($manifestFinding) {
        $findings.Add($manifestFinding)
    }

    if ($manifestInfo.Detected) {
        $findings.Add((New-Finding `
            -Id 'steam.branch' `
            -Severity 'info' `
            -Message $manifestInfo.Message `
            -Path $manifestInfo.ManifestPath `
            -Details @{
                BranchMode = $manifestInfo.BranchMode
                Confidence = $manifestInfo.Confidence
            }))
    }

    foreach ($modFinding in Get-ModListFindings -GameRoot $resolvedGameRoot) {
        $findings.Add($modFinding)
    }

    $engineIniPath = Join-Path $resolvedGameRoot 'ConanSandbox\Saved\Config\WindowsNoEditor\Engine.ini'
    if (Test-Path -LiteralPath $engineIniPath -PathType Leaf) {
        $engineIni = Get-Content -LiteralPath $engineIniPath -Raw
        $overrideMatches = [regex]::Matches(
            $engineIni,
            '(?im)^[ \t]*(bUseBuildIdOverride|BuildIdOverride)\s*=.*$'
        )

        if ($overrideMatches.Count -gt 0) {
            $findings.Add((New-Finding `
                -Id 'config.engine-build-override' `
                -Severity 'warning' `
                -Message 'Engine.ini contains build override lines that can cause branch or server version trouble.' `
                -Path $engineIniPath `
                -Details @{ MatchCount = $overrideMatches.Count }))
        }
    }

    $modControllerCachePath = Join-Path $resolvedGameRoot 'ConanSandbox\Saved\ModControllerCache.json'
    if (Test-Path -LiteralPath $modControllerCachePath -PathType Leaf) {
        $findings.Add((New-Finding `
            -Id 'saved.mod-controller-cache' `
            -Severity 'info' `
            -Message 'ModControllerCache.json is present. Prepare Legacy can move it aside reversibly during cleanup.' `
            -Path $modControllerCachePath `
            -Details $null))
    }

    $totCustomPath = Join-Path $resolvedGameRoot 'ConanSandbox\Saved\SaveGames\TotCustom'
    if (Test-Path -LiteralPath $totCustomPath -PathType Container) {
        $totFiles = @(Get-ChildItem -LiteralPath $totCustomPath -File -Recurse -ErrorAction SilentlyContinue).Count
        $findings.Add((New-Finding `
            -Id 'saves.totcustom' `
            -Severity 'info' `
            -Message "TotCustom save data is present. Prepare Legacy will create a rotating backup before cleanup." `
            -Path $totCustomPath `
            -Details @{ FileCount = $totFiles }))
    }

    $savedRoot = Join-Path $resolvedGameRoot 'ConanSandbox\Saved'
    foreach ($saveName in @('game.db', 'game_0.db', 'dlc_siptah.db')) {
        $savePath = Join-Path $savedRoot $saveName
        if (Test-Path -LiteralPath $savePath -PathType Leaf) {
            $item = Get-Item -LiteralPath $savePath
            $findings.Add((New-Finding `
                -Id ("saves.{0}" -f $saveName.ToLowerInvariant().Replace('.', '-')) `
                -Severity 'info' `
                -Message "$saveName is present. Legacy Doctor leaves it alone unless the player explicitly chooses save-database quarantine." `
                -Path $savePath `
                -Details @{
                    LengthBytes      = $item.Length
                    LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
                }))
        }
    }

    foreach ($logFinding in Get-LogSignalFindings -GameRoot $resolvedGameRoot) {
        $findings.Add($logFinding)
    }

    [pscustomobject]@{
        SchemaVersion = $script:SchemaVersion
        Tool          = $script:ToolName
        GameRoot      = $resolvedGameRoot
        ScannedAtUtc  = (Get-UtcNowString)
        Branch        = $manifestInfo
        Findings      = $findings.ToArray()
    }
}

function Get-TransactionFolder {
    param(
        [Parameter(Mandatory)][string]$TransactionId,
        [string]$StateRoot
    )

    Join-Path (Join-Path (Get-DoctorStateRoot -StateRoot $StateRoot) 'transactions') $TransactionId
}

function Save-Transaction {
    param(
        [Parameter(Mandatory)]$Transaction,
        [string]$StateRoot
    )

    $folder = Get-TransactionFolder -TransactionId $Transaction.Id -StateRoot $StateRoot
    Ensure-Directory -Path $folder
    $transactionPath = Join-Path $folder 'transaction.json'
    $Transaction | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $transactionPath -Encoding UTF8
}

function Load-Transaction {
    param(
        [Parameter(Mandatory)][string]$TransactionId,
        [string]$StateRoot
    )

    $folder = Get-TransactionFolder -TransactionId $TransactionId -StateRoot $StateRoot
    $transactionPath = Join-Path $folder 'transaction.json'
    if (-not (Test-Path -LiteralPath $transactionPath -PathType Leaf)) {
        throw "Transaction not found: $TransactionId"
    }

    Get-Content -LiteralPath $transactionPath -Raw | ConvertFrom-Json
}

function Add-PendingOperation {
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][string]$Type,
        [Parameter(Mandatory)][string]$Reason,
        [hashtable]$Data,
        [string]$StateRoot
    )

    $operation = [ordered]@{
        Id          = [Guid]::NewGuid().ToString('N')
        Type        = $Type
        Reason      = $Reason
        Status      = 'pending'
        StartedAtUtc = Get-UtcNowString
        CompletedAtUtc = $null
        Data        = if ($Data) { [pscustomobject]$Data } else { $null }
    }

    $Transaction.Operations += [pscustomobject]$operation
    Save-Transaction -Transaction $Transaction -StateRoot $StateRoot
    return $Transaction.Operations[-1]
}

function Complete-Operation {
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)]$Operation,
        [string]$StateRoot
    )

    $Operation.Status = 'completed'
    $Operation.CompletedAtUtc = Get-UtcNowString
    Save-Transaction -Transaction $Transaction -StateRoot $StateRoot
}

function Move-PathTransactionally {
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][string]$Reason,
        [string]$StateRoot
    )

    Assert-PathInsideRoot -Path $SourcePath -Root $Transaction.GameRoot
    Ensure-Directory -Path (Split-Path -Parent $DestinationPath)

    $operation = Add-PendingOperation `
        -Transaction $Transaction `
        -Type 'MovePath' `
        -Reason $Reason `
        -Data @{
            SourcePath      = $SourcePath
            DestinationPath = $DestinationPath
        } `
        -StateRoot $StateRoot

    Move-Item -LiteralPath $SourcePath -Destination $DestinationPath
    Complete-Operation -Transaction $Transaction -Operation $operation -StateRoot $StateRoot
}

function New-DirectoryTransactionally {
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Reason,
        [string]$StateRoot
    )

    Assert-PathInsideRoot -Path $Path -Root $Transaction.GameRoot

    $operation = Add-PendingOperation `
        -Transaction $Transaction `
        -Type 'CreateDirectory' `
        -Reason $Reason `
        -Data @{ Path = $Path } `
        -StateRoot $StateRoot

    Ensure-Directory -Path $Path
    Complete-Operation -Transaction $Transaction -Operation $operation -StateRoot $StateRoot
}

function Copy-PathTransactionally {
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][string]$Reason,
        [string]$StateRoot
    )

    Assert-PathInsideRoot -Path $SourcePath -Root $Transaction.GameRoot
    Ensure-Directory -Path (Split-Path -Parent $DestinationPath)

    $operation = Add-PendingOperation `
        -Transaction $Transaction `
        -Type 'CopyPath' `
        -Reason $Reason `
        -Data @{
            SourcePath      = $SourcePath
            DestinationPath = $DestinationPath
        } `
        -StateRoot $StateRoot

    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
    Complete-Operation -Transaction $Transaction -Operation $operation -StateRoot $StateRoot
}

function Rewrite-EngineIniTransactionally {
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][string]$EngineIniPath,
        [string]$StateRoot
    )

    Assert-PathInsideRoot -Path $EngineIniPath -Root $Transaction.GameRoot

    $raw = Get-Content -LiteralPath $EngineIniPath -Raw
    $rewritten = [regex]::Replace(
        $raw,
        '(?im)^[ \t]*(bUseBuildIdOverride|BuildIdOverride)\s*=.*(?:\r?\n|$)',
        ''
    )

    if ($rewritten -eq $raw) {
        return
    }

    $transactionFolder = Get-TransactionFolder -TransactionId $Transaction.Id -StateRoot $StateRoot
    $backupFolder = Join-Path $transactionFolder 'backups'
    Ensure-Directory -Path $backupFolder
    $backupPath = Join-Path $backupFolder 'Engine.ini.original'
    Copy-Item -LiteralPath $EngineIniPath -Destination $backupPath -Force

    $operation = Add-PendingOperation `
        -Transaction $Transaction `
        -Type 'RewriteTextFile' `
        -Reason 'Remove stale Engine.ini build override lines.' `
        -Data @{
            Path         = $EngineIniPath
            BackupPath   = $backupPath
            OriginalHash = Get-FileSha256 -Path $EngineIniPath
            ResultHash   = $null
        } `
        -StateRoot $StateRoot

    [System.IO.File]::WriteAllText($EngineIniPath, $rewritten, [System.Text.UTF8Encoding]::new($false))
    $operation.Data.ResultHash = Get-FileSha256 -Path $EngineIniPath
    Complete-Operation -Transaction $Transaction -Operation $operation -StateRoot $StateRoot
}

function Get-BackupPathKey {
    param([Parameter(Mandatory)][string]$GameRoot)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes((Resolve-FullPath -Path $GameRoot).ToLowerInvariant())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
        return $hash.Substring(0, 10)
    }
    finally {
        $sha.Dispose()
    }
}

function Add-CompletedOperation {
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][string]$Type,
        [Parameter(Mandatory)][string]$Reason,
        [hashtable]$Data,
        [string]$StateRoot
    )

    $operation = Add-PendingOperation `
        -Transaction $Transaction `
        -Type $Type `
        -Reason $Reason `
        -Data $Data `
        -StateRoot $StateRoot
    Complete-Operation -Transaction $Transaction -Operation $operation -StateRoot $StateRoot
}

function Backup-TotCustomIfPresent {
    param(
        [Parameter(Mandatory)]$Transaction,
        [string]$StateRoot
    )

    $sourcePath = Join-Path $Transaction.GameRoot 'ConanSandbox\Saved\SaveGames\TotCustom'
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
        return
    }

    $stateRootPath = Get-DoctorStateRoot -StateRoot $StateRoot
    $backupRoot = Join-Path $stateRootPath 'backups\TotCustom'
    $folderName = (Split-Path -Leaf $Transaction.GameRoot) -replace '[^A-Za-z0-9._-]', '_'
    $backupFolder = Join-Path $backupRoot ("{0}-{1}" -f $folderName, (Get-BackupPathKey -GameRoot $Transaction.GameRoot))
    Ensure-Directory -Path $backupFolder

    $slot1 = Join-Path $backupFolder 'TotCustom_1.zip'
    $slot2 = Join-Path $backupFolder 'TotCustom_2.zip'
    $slot3 = Join-Path $backupFolder 'TotCustom_3.zip'
    $tempZip = Join-Path $backupFolder ("TotCustom_{0}.tmp.zip" -f [Guid]::NewGuid().ToString('N'))

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($sourcePath, $tempZip)

        if (Test-Path -LiteralPath $slot2 -PathType Leaf) {
            Move-Item -LiteralPath $slot2 -Destination $slot3 -Force
        }

        if (Test-Path -LiteralPath $slot1 -PathType Leaf) {
            Move-Item -LiteralPath $slot1 -Destination $slot2 -Force
        }

        Move-Item -LiteralPath $tempZip -Destination $slot1 -Force

        Add-CompletedOperation `
            -Transaction $Transaction `
            -Type 'CreateBackupArchive' `
            -Reason 'Create a rotating backup of TotCustom save files before Legacy cleanup.' `
            -Data @{
                SourcePath = $sourcePath
                BackupPath = $slot1
                RotationSlots = @($slot1, $slot2, $slot3)
            } `
            -StateRoot $StateRoot
    }
    catch {
        if (Test-Path -LiteralPath $tempZip -PathType Leaf) {
            Remove-Item -LiteralPath $tempZip -Force
        }

        Add-TransactionWarning `
            -Transaction $Transaction `
            -Message ("TotCustom backup could not be created: {0}" -f $_.Exception.Message) `
            -StateRoot $StateRoot
    }
}

function New-Transaction {
    param(
        [Parameter(Mandatory)][string]$GameRoot,
        [Parameter(Mandatory)][string]$Action,
        [string]$StateRoot
    )

    $transaction = [pscustomobject]@{
        SchemaVersion = $script:SchemaVersion
        Tool          = $script:ToolName
        Id            = Get-TransactionId
        Action        = $Action
        GameRoot      = $GameRoot
        CreatedAtUtc  = Get-UtcNowString
        CompletedAtUtc = $null
        RestoredAtUtc = $null
        Status        = 'pending'
        Operations    = @()
        Warnings      = @()
    }

    Save-Transaction -Transaction $transaction -StateRoot $StateRoot
    return $transaction
}

function Invoke-LegacyPreparation {
    [CmdletBinding()]
    param(
        [string]$GameRoot,
        [switch]$QuarantineModsDirectory,
        [switch]$ResetClientConfig,
        [switch]$QuarantineSaveDatabases,
        [string]$StateRoot
    )

    $resolvedGameRoot = Resolve-ConanGameRoot -GameRoot $GameRoot
    $transaction = New-Transaction -GameRoot $resolvedGameRoot -Action 'prepare-legacy' -StateRoot $StateRoot
    $transactionFolder = Get-TransactionFolder -TransactionId $transaction.Id -StateRoot $StateRoot
    $quarantineFolder = Join-Path $transactionFolder 'quarantine'
    Ensure-Directory -Path $quarantineFolder

    Backup-TotCustomIfPresent -Transaction $transaction -StateRoot $StateRoot

    $modlistPath = Join-Path $resolvedGameRoot 'ConanSandbox\Mods\modlist.txt'
    if (Test-Path -LiteralPath $modlistPath -PathType Leaf) {
        Move-PathTransactionally `
            -Transaction $transaction `
            -SourcePath $modlistPath `
            -DestinationPath (Join-Path $quarantineFolder 'modlist.txt') `
            -Reason 'Temporarily remove active mod list for Legacy startup triage.' `
            -StateRoot $StateRoot
    }

    $engineIniPath = Join-Path $resolvedGameRoot 'ConanSandbox\Saved\Config\WindowsNoEditor\Engine.ini'
    if (Test-Path -LiteralPath $engineIniPath -PathType Leaf) {
        Rewrite-EngineIniTransactionally -Transaction $transaction -EngineIniPath $engineIniPath -StateRoot $StateRoot
    }

    $modControllerCachePath = Join-Path $resolvedGameRoot 'ConanSandbox\Saved\ModControllerCache.json'
    if (Test-Path -LiteralPath $modControllerCachePath -PathType Leaf) {
        Move-PathTransactionally `
            -Transaction $transaction `
            -SourcePath $modControllerCachePath `
            -DestinationPath (Join-Path $quarantineFolder 'ModControllerCache.json') `
            -Reason 'Temporarily move ModControllerCache.json aside before Legacy triage.' `
            -StateRoot $StateRoot
    }

    if ($QuarantineSaveDatabases) {
        $savedRoot = Join-Path $resolvedGameRoot 'ConanSandbox\Saved'
        $saveQuarantineFolder = Join-Path $quarantineFolder 'SaveDatabases'

        foreach ($saveName in @('game.db', 'game_0.db', 'dlc_siptah.db')) {
            $savePath = Join-Path $savedRoot $saveName
            if (-not (Test-Path -LiteralPath $savePath -PathType Leaf)) {
                continue
            }

            $backupPath = Join-Path $saveQuarantineFolder ("{0}.backup" -f $saveName)
            Copy-PathTransactionally `
                -Transaction $transaction `
                -SourcePath $savePath `
                -DestinationPath $backupPath `
                -Reason ("Create a byte-for-byte safety copy of {0} before save quarantine." -f $saveName) `
                -StateRoot $StateRoot

            Move-PathTransactionally `
                -Transaction $transaction `
                -SourcePath $savePath `
                -DestinationPath (Join-Path $saveQuarantineFolder $saveName) `
                -Reason ("Temporarily quarantine {0} so Legacy can start without a possibly UE5-converted database." -f $saveName) `
                -StateRoot $StateRoot
        }
    }

    if ($QuarantineModsDirectory) {
        $modsPath = Join-Path $resolvedGameRoot 'ConanSandbox\Mods'
        if (Test-Path -LiteralPath $modsPath -PathType Container) {
            Move-PathTransactionally `
                -Transaction $transaction `
                -SourcePath $modsPath `
                -DestinationPath (Join-Path $quarantineFolder 'Mods') `
                -Reason 'Temporarily isolate the Mods directory for a clean-room Legacy startup test.' `
                -StateRoot $StateRoot

            New-DirectoryTransactionally `
                -Transaction $transaction `
                -Path $modsPath `
                -Reason 'Create an empty placeholder Mods directory after quarantine.' `
                -StateRoot $StateRoot
        }
    }

    if ($ResetClientConfig) {
        $configPath = Join-Path $resolvedGameRoot 'ConanSandbox\Saved\Config\WindowsNoEditor'
        if (Test-Path -LiteralPath $configPath -PathType Container) {
            Move-PathTransactionally `
                -Transaction $transaction `
                -SourcePath $configPath `
                -DestinationPath (Join-Path $quarantineFolder 'WindowsNoEditor') `
                -Reason 'Temporarily isolate WindowsNoEditor client config for a clean-room Legacy startup test.' `
                -StateRoot $StateRoot

            New-DirectoryTransactionally `
                -Transaction $transaction `
                -Path $configPath `
                -Reason 'Create an empty placeholder client config directory after quarantine.' `
                -StateRoot $StateRoot
        }
    }

    $transaction.Status = 'completed'
    $transaction.CompletedAtUtc = Get-UtcNowString
    Save-Transaction -Transaction $transaction -StateRoot $StateRoot
    return $transaction
}

function Add-TransactionWarning {
    param(
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][string]$Message,
        [string]$StateRoot
    )

    $Transaction.Warnings += $Message
    Save-Transaction -Transaction $Transaction -StateRoot $StateRoot
}

function Remove-EmptyDoctorDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Transaction,
        [string]$StateRoot
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $true
    }

    $children = @(Get-ChildItem -LiteralPath $Path -Force)
    if ($children.Count -gt 0) {
        Add-TransactionWarning `
            -Transaction $Transaction `
            -Message "Restore skipped removing non-empty doctor-created directory: $Path" `
            -StateRoot $StateRoot
        return $false
    }

    Remove-Item -LiteralPath $Path
    return $true
}

function Restore-LegacyDoctorTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TransactionId,
        [switch]$Force,
        [string]$StateRoot
    )

    $transaction = Load-Transaction -TransactionId $TransactionId -StateRoot $StateRoot
    if ($transaction.Status -eq 'restored') {
        return $transaction
    }

    $operations = @($transaction.Operations)
    [array]::Reverse($operations)

    foreach ($operation in $operations) {
        if ($operation.Status -ne 'completed') {
            continue
        }

        switch ($operation.Type) {
            'CreateDirectory' {
                [void](Remove-EmptyDoctorDirectory `
                    -Path $operation.Data.Path `
                    -Transaction $transaction `
                    -StateRoot $StateRoot)
            }

            'RewriteTextFile' {
                $path = $operation.Data.Path
                $backupPath = $operation.Data.BackupPath
                if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
                    Add-TransactionWarning -Transaction $transaction -Message "Restore backup is missing: $backupPath" -StateRoot $StateRoot
                    continue
                }

                $currentHash = Get-FileSha256 -Path $path
                if (-not $Force -and $currentHash -and $operation.Data.ResultHash -and $currentHash -ne $operation.Data.ResultHash) {
                    Add-TransactionWarning -Transaction $transaction -Message "Restore skipped modified file to avoid overwriting newer changes: $path" -StateRoot $StateRoot
                    continue
                }

                Copy-Item -LiteralPath $backupPath -Destination $path -Force
            }

            'CopyPath' {
                $destinationPath = $operation.Data.DestinationPath
                if (Test-Path -LiteralPath $destinationPath) {
                    Remove-Item -LiteralPath $destinationPath -Force
                }
            }

            'MovePath' {
                $sourcePath = $operation.Data.SourcePath
                $destinationPath = $operation.Data.DestinationPath

                if (-not (Test-Path -LiteralPath $destinationPath)) {
                    Add-TransactionWarning -Transaction $transaction -Message "Restore source is missing: $destinationPath" -StateRoot $StateRoot
                    continue
                }

                if (Test-Path -LiteralPath $sourcePath) {
                    Add-TransactionWarning -Transaction $transaction -Message "Restore skipped because the destination already exists: $sourcePath" -StateRoot $StateRoot
                    continue
                }

                Ensure-Directory -Path (Split-Path -Parent $sourcePath)
                Move-Item -LiteralPath $destinationPath -Destination $sourcePath
            }
        }
    }

    $transaction.Status = 'restored'
    $transaction.RestoredAtUtc = Get-UtcNowString
    Save-Transaction -Transaction $transaction -StateRoot $StateRoot
    return $transaction
}

function Copy-BundleConfigFile {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationRoot,
        [Parameter(Mandatory)][System.Collections.Generic.List[string]]$IncludedFiles
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        return
    }

    $destinationPath = Join-Path $DestinationRoot (Split-Path -Leaf $SourcePath)
    Copy-Item -LiteralPath $SourcePath -Destination $destinationPath -Force
    $IncludedFiles.Add($destinationPath)
}

function Export-LegacySupportBundle {
    [CmdletBinding()]
    param(
        [string]$GameRoot,
        [string]$DestinationPath,
        [switch]$IncludeRecentLogs,
        [switch]$IncludeConfigSnapshots,
        [string]$StateRoot
    )

    $resolvedGameRoot = Resolve-ConanGameRoot -GameRoot $GameRoot
    $doctorStateRoot = Get-DoctorStateRoot -StateRoot $StateRoot
    $scan = Get-LegacyDoctorScan -GameRoot $resolvedGameRoot
    $actions = Get-LegacyDoctorActions -StateRoot $StateRoot
    $includedFiles = New-Object System.Collections.Generic.List[string]

    if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
        $bundleRoot = Join-Path $doctorStateRoot 'support-bundles'
        Ensure-Directory -Path $bundleRoot
        $DestinationPath = Join-Path $bundleRoot ("ConanLegacyDoctor-{0}.zip" -f [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
    }
    else {
        $DestinationPath = Resolve-FullPath -Path $DestinationPath
        Ensure-Directory -Path (Split-Path -Parent $DestinationPath)
    }

    $stagingRoot = Join-Path (Join-Path $doctorStateRoot 'bundle-staging') ([Guid]::NewGuid().ToString('N'))
    Ensure-Directory -Path $stagingRoot

    try {
        $metadata = [pscustomobject]@{
            SchemaVersion          = $script:SchemaVersion
            Tool                   = $script:ToolName
            CreatedAtUtc           = Get-UtcNowString
            GameRoot               = $resolvedGameRoot
            DestinationPath        = $DestinationPath
            IncludeRecentLogs      = [bool]$IncludeRecentLogs
            IncludeConfigSnapshots = [bool]$IncludeConfigSnapshots
            Notes                  = @(
                'No save database files are included.',
                'Recent logs and config snapshots are opt-in because they may contain player-local or server-specific details.'
            )
        }

        $metadataPath = Join-Path $stagingRoot 'bundle-metadata.json'
        $scanPath = Join-Path $stagingRoot 'scan.json'
        $actionsPath = Join-Path $stagingRoot 'actions.json'
        $metadata | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $metadataPath -Encoding UTF8
        $scan | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $scanPath -Encoding UTF8
        (ConvertTo-Json -InputObject @($actions) -Depth 12) | Set-Content -LiteralPath $actionsPath -Encoding UTF8
        $includedFiles.Add($metadataPath)
        $includedFiles.Add($scanPath)
        $includedFiles.Add($actionsPath)

        if ($IncludeRecentLogs) {
            $logsRoot = Join-Path $resolvedGameRoot 'ConanSandbox\Saved\Logs'
            if (Test-Path -LiteralPath $logsRoot -PathType Container) {
                $bundleLogsRoot = Join-Path $stagingRoot 'logs'
                Ensure-Directory -Path $bundleLogsRoot

                $recentLogs = Get-ChildItem -LiteralPath $logsRoot -Filter '*.log' -File |
                    Sort-Object LastWriteTimeUtc -Descending |
                    Select-Object -First 3

                foreach ($log in $recentLogs) {
                    $tailPath = Join-Path $bundleLogsRoot ("{0}.tail.txt" -f $log.BaseName)
                    Get-Content -LiteralPath $log.FullName -Tail 1500 -ErrorAction SilentlyContinue |
                        Set-Content -LiteralPath $tailPath -Encoding UTF8
                    $includedFiles.Add($tailPath)
                }
            }
        }

        if ($IncludeConfigSnapshots) {
            $bundleConfigRoot = Join-Path $stagingRoot 'config'
            Ensure-Directory -Path $bundleConfigRoot
            Copy-BundleConfigFile `
                -SourcePath (Join-Path $resolvedGameRoot 'ConanSandbox\Saved\Config\WindowsNoEditor\Engine.ini') `
                -DestinationRoot $bundleConfigRoot `
                -IncludedFiles $includedFiles
            Copy-BundleConfigFile `
                -SourcePath (Join-Path $resolvedGameRoot 'ConanSandbox\Saved\Config\WindowsNoEditor\Game.ini') `
                -DestinationRoot $bundleConfigRoot `
                -IncludedFiles $includedFiles
            Copy-BundleConfigFile `
                -SourcePath (Join-Path $resolvedGameRoot 'ConanSandbox\Mods\modlist.txt') `
                -DestinationRoot $bundleConfigRoot `
                -IncludedFiles $includedFiles
        }

        if (Test-Path -LiteralPath $DestinationPath -PathType Leaf) {
            Remove-Item -LiteralPath $DestinationPath
        }

        Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $DestinationPath -Force

        return [pscustomobject]@{
            SchemaVersion          = $script:SchemaVersion
            Tool                   = $script:ToolName
            CreatedAtUtc           = Get-UtcNowString
            GameRoot               = $resolvedGameRoot
            DestinationPath        = $DestinationPath
            IncludeRecentLogs      = [bool]$IncludeRecentLogs
            IncludeConfigSnapshots = [bool]$IncludeConfigSnapshots
            StagedFileCount        = $includedFiles.Count
        }
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
    }
}

function Get-LegacyDoctorTransactions {
    [CmdletBinding()]
    param([string]$StateRoot)

    $transactionsRoot = Join-Path (Get-DoctorStateRoot -StateRoot $StateRoot) 'transactions'
    if (-not (Test-Path -LiteralPath $transactionsRoot -PathType Container)) {
        return @()
    }

    $transactions = New-Object System.Collections.Generic.List[object]
    foreach ($folder in Get-ChildItem -LiteralPath $transactionsRoot -Directory | Sort-Object Name -Descending) {
        $transactionPath = Join-Path $folder.FullName 'transaction.json'
        if (Test-Path -LiteralPath $transactionPath -PathType Leaf) {
            $transactions.Add((Get-Content -LiteralPath $transactionPath -Raw | ConvertFrom-Json))
        }
    }

    return $transactions.ToArray()
}

function Get-OperationActionText {
    param([Parameter(Mandatory)]$Operation)

    switch ($Operation.Type) {
        'MovePath' {
            return ("Moved '{0}' to '{1}'." -f $Operation.Data.SourcePath, $Operation.Data.DestinationPath)
        }

        'CreateDirectory' {
            return ("Created an empty placeholder folder at '{0}'." -f $Operation.Data.Path)
        }

        'RewriteTextFile' {
            return ("Backed up and cleaned '{0}' by removing stale build override lines." -f $Operation.Data.Path)
        }

        'CopyPath' {
            return ("Created a safety copy of '{0}' at '{1}'." -f $Operation.Data.SourcePath, $Operation.Data.DestinationPath)
        }

        'CreateBackupArchive' {
            return ("Created a rotating TotCustom backup from '{0}' at '{1}'." -f $Operation.Data.SourcePath, $Operation.Data.BackupPath)
        }

        default {
            return $Operation.Reason
        }
    }
}

function Get-LegacyDoctorActions {
    [CmdletBinding()]
    param([string]$StateRoot)

    $transactions = @(Get-LegacyDoctorTransactions -StateRoot $StateRoot)
    $actions = New-Object System.Collections.Generic.List[object]

    foreach ($transaction in $transactions) {
        $details = New-Object System.Collections.Generic.List[string]
        foreach ($operation in @($transaction.Operations)) {
            if ($operation.Status -eq 'completed') {
                $details.Add((Get-OperationActionText -Operation $operation))
            }
        }

        $summary = if ($details.Count -eq 0) {
            'No completed file actions were recorded.'
        }
        else {
            $details[0]
        }

        if ($details.Count -gt 1) {
            $summary = "{0} (+{1} more)" -f $summary.TrimEnd('.'), ($details.Count - 1)
        }

        $actions.Add([pscustomobject]@{
            Id           = $transaction.Id
            Status       = $transaction.Status
            Action       = $transaction.Action
            CreatedAtUtc = $transaction.CreatedAtUtc
            GameRoot     = $transaction.GameRoot
            Summary      = $summary
            Details      = $details.ToArray()
            Warnings     = @($transaction.Warnings)
        })
    }

    return $actions.ToArray()
}

function Get-LegacyDoctorVanillaLaunchPlan {
    [CmdletBinding()]
    param([string]$GameRoot)

    $resolvedGameRoot = Resolve-ConanGameRoot -GameRoot $GameRoot
    $branch = Get-SteamManifestInfo -GameRoot $resolvedGameRoot
    $warnings = New-Object System.Collections.Generic.List[string]

    $modListPath = Join-Path $resolvedGameRoot 'ConanSandbox\Mods\modlist.txt'
    $modListPresent = Test-Path -LiteralPath $modListPath -PathType Leaf
    if ($modListPresent) {
        $warnings.Add("Active mod list detected at '$modListPath'. Run Prepare Legacy first so the doctor can move it aside reversibly.")
    }

    $preferredExecutables = switch ($branch.BranchMode) {
        'EnhancedOrDefault' { @('ConanSandbox-Win64-Shipping.exe', 'ConanSandbox.exe') }
        default { @('ConanSandbox.exe', 'ConanSandbox-Win64-Shipping.exe') }
    }

    $binaryRoot = Join-Path $resolvedGameRoot 'ConanSandbox\Binaries\Win64'
    $executablePath = $null
    foreach ($executableName in $preferredExecutables) {
        $candidatePath = Join-Path $binaryRoot $executableName
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            $executablePath = Resolve-FullPath -Path $candidatePath
            break
        }
    }

    $steamFallbackAllowed = ($branch.InstallDirMatchesGameRoot -eq $true)
    $launchStrategy = if ($executablePath) {
        'DirectExecutable'
    }
    elseif ($steamFallbackAllowed) {
        'SteamUri'
    }
    else {
        'Unavailable'
    }

    if ($launchStrategy -eq 'Unavailable') {
        $warnings.Add('No verified executable was found for the selected install. Because this install is not confirmed as the Steam-managed target, the doctor will not fall back to Steam and risk launching the wrong branch.')
    }

    [pscustomobject]@{
        SchemaVersion        = $script:SchemaVersion
        Tool                 = $script:ToolName
        GameRoot             = $resolvedGameRoot
        BranchMode           = $branch.BranchMode
        BranchConfidence     = $branch.Confidence
        VanillaReady         = (-not $modListPresent)
        ModListPath          = $modListPath
        LaunchStrategy       = $launchStrategy
        ExecutablePath       = $executablePath
        SteamUri             = 'steam://run/440900/'
        SteamFallbackAllowed = $steamFallbackAllowed
        Warnings             = $warnings.ToArray()
    }
}

function Start-LegacyDoctorVanillaLaunch {
    [CmdletBinding()]
    param([string]$GameRoot)

    $plan = Get-LegacyDoctorVanillaLaunchPlan -GameRoot $GameRoot
    if (-not $plan.VanillaReady) {
        throw 'Vanilla launch is blocked because modlist.txt is still active. Run Prepare Legacy first, then try the vanilla launch again.'
    }

    switch ($plan.LaunchStrategy) {
        'DirectExecutable' {
            Start-Process `
                -FilePath $plan.ExecutablePath `
                -WorkingDirectory (Split-Path -Parent $plan.ExecutablePath) | Out-Null
        }

        'SteamUri' {
            Start-Process -FilePath $plan.SteamUri | Out-Null
        }

        default {
            $detail = if (@($plan.Warnings).Count -gt 0) {
                $plan.Warnings[-1]
            }
            else {
                'No safe launch route was found for the selected install.'
            }

            throw $detail
        }
    }

    [pscustomobject]@{
        SchemaVersion  = $script:SchemaVersion
        Tool           = $script:ToolName
        GameRoot       = $plan.GameRoot
        BranchMode     = $plan.BranchMode
        LaunchStrategy = $plan.LaunchStrategy
        ExecutablePath = $plan.ExecutablePath
        SteamUri       = $plan.SteamUri
        StartedAtUtc   = Get-UtcNowString
    }
}

Export-ModuleMember -Function @(
    'Get-LegacyDoctorInstallCandidates',
    'Get-LegacyDoctorScan',
    'Invoke-LegacyPreparation',
    'Restore-LegacyDoctorTransaction',
    'Get-LegacyDoctorTransactions',
    'Get-LegacyDoctorActions',
    'Export-LegacySupportBundle',
    'Get-LegacyDoctorVanillaLaunchPlan',
    'Start-LegacyDoctorVanillaLaunch'
)
