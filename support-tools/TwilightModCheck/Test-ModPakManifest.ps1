[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [string]$WorkshopRoot,
    [string]$ReportPath,
    [ValidateSet('Text', 'Json')][string]$OutputFormat = 'Text'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WorkshopRootCandidates {
    @(
        'C:\Program Files (x86)\Steam\steamapps\workshop\content\440900',
        'C:\Program Files\Steam\steamapps\workshop\content\440900',
        'D:\SteamLibrary\steamapps\workshop\content\440900',
        'E:\SteamLibrary\steamapps\workshop\content\440900',
        'F:\SteamLibrary\steamapps\workshop\content\440900',
        'N:\SteamLibrary\steamapps\workshop\content\440900'
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Container }
}

function Resolve-WorkshopRoot {
    param([string]$RequestedRoot)

    if (-not [string]::IsNullOrWhiteSpace($RequestedRoot)) {
        $resolved = [System.IO.Path]::GetFullPath($RequestedRoot)
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "Workshop root does not exist: $resolved"
        }

        return $resolved
    }

    $candidates = @(Get-WorkshopRootCandidates)
    if ($candidates.Count -eq 1) {
        return $candidates[0]
    }

    if ($candidates.Count -eq 0) {
        throw 'No Conan Exiles Workshop root was discovered automatically. Pass -WorkshopRoot.'
    }

    throw ("More than one Conan Exiles Workshop root was found. Pass -WorkshopRoot explicitly:`n{0}" -f ($candidates -join "`n"))
}

$resolvedManifest = [System.IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $resolvedManifest -PathType Leaf)) {
    throw "Manifest does not exist: $resolvedManifest"
}

$manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
$resolvedWorkshopRoot = Resolve-WorkshopRoot -RequestedRoot $WorkshopRoot

$results = foreach ($entry in @($manifest.Entries)) {
    $expectedPath = if ($entry.WorkshopId) {
        Join-Path (Join-Path $resolvedWorkshopRoot $entry.WorkshopId) $entry.PakName
    }
    else {
        $null
    }

    $candidate = if ($expectedPath -and (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
        Get-Item -LiteralPath $expectedPath
    }
    elseif ($entry.WorkshopId) {
        Get-ChildItem -LiteralPath (Join-Path $resolvedWorkshopRoot $entry.WorkshopId) -Filter $entry.PakName -File -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    else {
        $null
    }

    $actualHash = if ($candidate) { (Get-FileHash -LiteralPath $candidate.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    $actualLength = if ($candidate) { $candidate.Length } else { $null }
    $status = if (-not $candidate) {
        'Missing'
    }
    elseif ($entry.Sha256 -and $actualHash -ne $entry.Sha256) {
        'HashMismatch'
    }
    elseif ($entry.LengthBytes -and [int64]$actualLength -ne [int64]$entry.LengthBytes) {
        'SizeMismatch'
    }
    else {
        'Match'
    }

    [pscustomobject]@{
        Order          = $entry.Order
        Status         = $status
        WorkshopId     = $entry.WorkshopId
        PakName        = $entry.PakName
        ExpectedSha256 = $entry.Sha256
        ActualSha256   = $actualHash
        ExpectedLength = $entry.LengthBytes
        ActualLength   = $actualLength
        Path           = if ($candidate) { $candidate.FullName } else { $expectedPath }
    }
}

$summary = [pscustomobject]@{
    Tool             = 'ConanLegacyDoctor.ModPakManifestChecker'
    CheckedAtUtc     = [DateTimeOffset]::UtcNow.ToString('O')
    ManifestPath     = $resolvedManifest
    WorkshopRoot     = $resolvedWorkshopRoot
    TotalEntries     = @($results).Count
    MatchCount       = @($results | Where-Object Status -eq 'Match').Count
    MissingCount     = @($results | Where-Object Status -eq 'Missing').Count
    HashMismatchCount = @($results | Where-Object Status -eq 'HashMismatch').Count
    SizeMismatchCount = @($results | Where-Object Status -eq 'SizeMismatch').Count
    Results          = @($results)
}

if ($OutputFormat -eq 'Json') {
    $json = $summary | ConvertTo-Json -Depth 8
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        $resolvedReportPath = [System.IO.Path]::GetFullPath($ReportPath)
        $reportDirectory = Split-Path -Parent $resolvedReportPath
        if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
            New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
        }

        Set-Content -LiteralPath $resolvedReportPath -Value $json -Encoding UTF8
    }

    Write-Output $json
    return
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('Twilight Mire Legacy Mod File Check')
$lines.Add(('Generated: {0}' -f ([DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz'))))
$lines.Add('')
$lines.Add(('Workshop folder checked: {0}' -f $summary.WorkshopRoot))
$lines.Add(('Entries checked: {0}' -f $summary.TotalEntries))
$lines.Add(('Matches: {0}' -f $summary.MatchCount))
$lines.Add(('Missing files: {0}' -f $summary.MissingCount))
$lines.Add(('Different file hashes: {0}' -f $summary.HashMismatchCount))
$lines.Add(('Different file sizes: {0}' -f $summary.SizeMismatchCount))
$lines.Add('')

$problems = @($results | Where-Object Status -ne 'Match')
if ($problems.Count -eq 0) {
    $lines.Add('Result: all checked mod files match the reference set.')
}
else {
    $lines.Add('Result: one or more checked mod files differ from the reference set.')
    $lines.Add('')
    $lines.Add('Items to review:')
    foreach ($problem in $problems) {
        $lines.Add(('[{0}] #{1} {2} ({3})' -f $problem.Status, $problem.Order, $problem.PakName, $problem.WorkshopId))
        $lines.Add(('  File path: {0}' -f $problem.Path))
    }
}

$text = $lines -join [Environment]::NewLine
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = Split-Path -Parent $resolvedReportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    }

    Set-Content -LiteralPath $resolvedReportPath -Value $text -Encoding UTF8
}

Write-Output $text
