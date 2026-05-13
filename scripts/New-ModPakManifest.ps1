[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModListPath,
    [Parameter(Mandatory)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ResolvedPakEntry {
    param(
        [Parameter(Mandatory)][string]$ModListRoot,
        [Parameter(Mandatory)][string]$RawEntry,
        [Parameter(Mandatory)][int]$Order
    )

    $trimmed = $RawEntry.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        return $null
    }

    $relativeEntry = $trimmed.TrimStart('*').Trim()
    $resolvedPath = if ([System.IO.Path]::IsPathRooted($relativeEntry)) {
        [System.IO.Path]::GetFullPath($relativeEntry)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $ModListRoot $relativeEntry))
    }

    $workshopId = $null
    if ($relativeEntry -match '[\\/](?<id>\d+)[\\/][^\\/]+\.pak$') {
        $workshopId = $Matches.id
    }

    $file = Get-Item -LiteralPath $resolvedPath -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Order         = $Order
        Entry         = $relativeEntry
        WorkshopId    = $workshopId
        PakName       = [System.IO.Path]::GetFileName($relativeEntry)
        ResolvedPath  = $resolvedPath
        Exists        = $null -ne $file
        LengthBytes   = if ($file) { $file.Length } else { $null }
        LastWriteUtc  = if ($file) { $file.LastWriteTimeUtc.ToString('O') } else { $null }
        Sha256        = if ($file) { (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    }
}

$resolvedModList = [System.IO.Path]::GetFullPath($ModListPath)
if (-not (Test-Path -LiteralPath $resolvedModList -PathType Leaf)) {
    throw "Mod list does not exist: $resolvedModList"
}

$modListRoot = Split-Path -Parent $resolvedModList
$entries = New-Object System.Collections.Generic.List[object]
$order = 0
foreach ($line in Get-Content -LiteralPath $resolvedModList) {
    $order++
    $entry = Get-ResolvedPakEntry -ModListRoot $modListRoot -RawEntry $line -Order $order
    if ($null -ne $entry) {
        $entries.Add($entry)
    }
}

$manifest = [pscustomobject]@{
    SchemaVersion     = 1
    Tool              = 'ConanLegacyDoctor.ModPakManifest'
    CreatedAtUtc      = [DateTimeOffset]::UtcNow.ToString('O')
    SourceModListPath = $resolvedModList
    EntryCount        = $entries.Count
    MissingCount      = @($entries | Where-Object Exists -eq $false).Count
    Entries           = $entries.ToArray()
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputFolder = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputFolder)) {
    New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
}

$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
Write-Output "Wrote mod pak manifest: $resolvedOutput"
Write-Output ("Entries: {0}; missing files: {1}" -f $manifest.EntryCount, $manifest.MissingCount)
