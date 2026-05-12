[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot 'artifacts\publish'
}
else {
    $OutputRoot
}
$projectPath = Join-Path $repoRoot 'src\ConanLegacyDoctor.App\ConanLegacyDoctor.App.csproj'
$publishDir = Join-Path ([System.IO.Path]::GetFullPath($OutputRoot)) 'win-x64'

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:DebugType=embedded `
    -o $publishDir

$exePath = Join-Path $publishDir 'ConanLegacyDoctor.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Expected executable was not produced: $exePath"
}

$hashPath = Join-Path $publishDir 'SHA256SUMS.txt'
$hash = Get-FileHash -LiteralPath $exePath -Algorithm SHA256
"{0}  {1}" -f $hash.Hash.ToLowerInvariant(), (Split-Path -Leaf $exePath) |
    Set-Content -LiteralPath $hashPath -Encoding ASCII

Write-Output "Published executable: $exePath"
Write-Output "Checksum file: $hashPath"
