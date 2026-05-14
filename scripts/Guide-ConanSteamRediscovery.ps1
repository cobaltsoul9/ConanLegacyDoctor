[CmdletBinding()]
param(
    [ValidateSet('Status', 'PrepareUninstall', 'ConfirmUninstalled', 'ExposeTargetForInstall', 'CheckAfterInstall')]
    [string]$Step = 'Status',
    [ValidateSet('Enhanced', 'Legacy')]
    [string]$TargetBranch = 'Enhanced',
    [string]$SteamAppsRoot = 'C:\Program Files (x86)\Steam\steamapps',
    [string]$ManagedFolderName = 'Conan Exiles',
    [string]$EnhancedFolderName = 'Conan Exiles Enhanced',
    [string]$LegacyFolderName = 'Conan Exiles Legacy',
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    [System.IO.Path]::GetFullPath($Path)
}

function Get-ManifestValue {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Pattern
    )

    if ($Content -match $Pattern) {
        return $Matches[1]
    }

    return $null
}

function Get-ConanManifestState {
    param([Parameter(Mandatory)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return [pscustomobject]@{
            Exists = $false
            InstallDir = $null
            BuildId = $null
            TargetBuildId = $null
            UserBeta = $null
            MountedBeta = $null
            StateFlags = $null
            BytesToDownload = $null
            BytesDownloaded = $null
        }
    }

    $content = Get-Content -LiteralPath $ManifestPath -Raw
    [pscustomobject]@{
        Exists = $true
        InstallDir = Get-ManifestValue -Content $content -Pattern '"installdir"\s+"([^"]+)"'
        BuildId = Get-ManifestValue -Content $content -Pattern '"buildid"\s+"([^"]+)"'
        TargetBuildId = Get-ManifestValue -Content $content -Pattern '"TargetBuildID"\s+"([^"]+)"'
        UserBeta = Get-ManifestValue -Content $content -Pattern '"UserConfig"[\s\S]*?"BetaKey"\s+"([^"]+)"'
        MountedBeta = Get-ManifestValue -Content $content -Pattern '"MountedConfig"[\s\S]*?"BetaKey"\s+"([^"]+)"'
        StateFlags = Get-ManifestValue -Content $content -Pattern '"StateFlags"\s+"([^"]+)"'
        BytesToDownload = [int64](Get-ManifestValue -Content $content -Pattern '"BytesToDownload"\s+"([^"]+)"')
        BytesDownloaded = [int64](Get-ManifestValue -Content $content -Pattern '"BytesDownloaded"\s+"([^"]+)"')
    }
}

function Get-FolderState {
    param([Parameter(Mandatory)][string]$Path)

    $legacyExe = Join-Path $Path 'ConanSandbox\Binaries\Win64\ConanSandbox.exe'
    $enhancedExe = Join-Path $Path 'ConanSandbox\Binaries\Win64\ConanSandbox-Win64-Shipping.exe'
    [pscustomobject]@{
        Path = $Path
        Exists = Test-Path -LiteralPath $Path -PathType Container
        LegacyShape = Test-Path -LiteralPath $legacyExe -PathType Leaf
        EnhancedShape = Test-Path -LiteralPath $enhancedExe -PathType Leaf
    }
}

function Write-Status {
    param(
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)]$Managed,
        [Parameter(Mandatory)]$Enhanced,
        [Parameter(Mandatory)]$Legacy,
        [Parameter(Mandatory)]$Workshop
    )

    Write-Output 'Conan Steam rediscovery guide'
    Write-Output ''
    Write-Output ("Manifest present: {0}" -f $Manifest.Exists)
    Write-Output ("Manifest install dir: {0}" -f ($(if ($Manifest.InstallDir) { $Manifest.InstallDir } else { '<none>' })))
    Write-Output ("Manifest mounted branch: {0}" -f ($(if ($Manifest.MountedBeta) { $Manifest.MountedBeta } else { '<default/public or none>' })))
    Write-Output ("Manifest requested branch: {0}" -f ($(if ($Manifest.UserBeta) { $Manifest.UserBeta } else { '<default/public or none>' })))
    Write-Output ("Manifest build id: {0}" -f ($(if ($Manifest.BuildId) { $Manifest.BuildId } else { '<none>' })))
    Write-Output ("Manifest target build id: {0}" -f ($(if ($Manifest.TargetBuildId) { $Manifest.TargetBuildId } else { '<none>' })))
    Write-Output ("Manifest queued bytes: {0}/{1}" -f $Manifest.BytesDownloaded, $Manifest.BytesToDownload)
    Write-Output ''
    Write-Output ("Managed folder:  {0} | exists={1} | enhanced={2} | legacy={3}" -f $Managed.Path, $Managed.Exists, $Managed.EnhancedShape, $Managed.LegacyShape)
    Write-Output ("Enhanced folder: {0} | exists={1} | enhanced={2} | legacy={3}" -f $Enhanced.Path, $Enhanced.Exists, $Enhanced.EnhancedShape, $Enhanced.LegacyShape)
    Write-Output ("Legacy folder:   {0} | exists={1} | enhanced={2} | legacy={3}" -f $Legacy.Path, $Legacy.Exists, $Legacy.EnhancedShape, $Legacy.LegacyShape)
    Write-Output ("Workshop mods:   live={0} | parked={1}" -f $Workshop.ContentExists, $Workshop.ParkedContentExists)
    Write-Output ("Workshop meta:   live={0} | parked={1}" -f $Workshop.ManifestExists, $Workshop.ParkedManifestExists)
}

function Assert-FolderExists {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label folder is missing: $Path"
    }
}

function Assert-FolderMissing {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )

    if (Test-Path -LiteralPath $Path) {
        throw "$Label path must be absent for this step: $Path"
    }
}

function Move-Folder {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][bool]$ShouldApply
    )

    Write-Output ("Move: {0}" -f $Source)
    Write-Output ("  To: {0}" -f $Destination)
    if ($ShouldApply) {
        $destinationParent = Split-Path -Parent $Destination
        if ($destinationParent) {
            New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        }

        Move-Item -LiteralPath $Source -Destination $Destination
    }
}

function Move-PathIfPresent {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][bool]$ShouldApply
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        return
    }

    Write-Output ("Move: {0}" -f $Source)
    Write-Output ("  To: {0}" -f $Destination)
    if ($ShouldApply) {
        Move-Item -LiteralPath $Source -Destination $Destination
    }
}

$steamAppsRootFull = Get-FullPath -Path $SteamAppsRoot
$commonRoot = Join-Path $steamAppsRootFull 'common'
$manifestPath = Join-Path $steamAppsRootFull 'appmanifest_440900.acf'
$managedPath = Join-Path $commonRoot $ManagedFolderName
$enhancedPath = Join-Path $commonRoot $EnhancedFolderName
$legacyPath = Join-Path $commonRoot $LegacyFolderName
$workshopRoot = Join-Path $steamAppsRootFull 'workshop'
$workshopContentPath = Join-Path (Join-Path $workshopRoot 'content') '440900'
$parkedWorkshopRoot = Join-Path $workshopRoot 'ConanLegacyDoctorParked'
$parkedWorkshopContentPath = Join-Path $parkedWorkshopRoot 'content_440900'
$workshopManifestPath = Join-Path $workshopRoot 'appworkshop_440900.acf'
$parkedWorkshopManifestPath = Join-Path $parkedWorkshopRoot 'appworkshop_440900.acf'

$manifest = Get-ConanManifestState -ManifestPath $manifestPath
$managed = Get-FolderState -Path $managedPath
$enhanced = Get-FolderState -Path $enhancedPath
$legacy = Get-FolderState -Path $legacyPath
$workshop = [pscustomobject]@{
    ContentExists = Test-Path -LiteralPath $workshopContentPath -PathType Container
    ParkedContentExists = Test-Path -LiteralPath $parkedWorkshopContentPath -PathType Container
    ManifestExists = Test-Path -LiteralPath $workshopManifestPath -PathType Leaf
    ParkedManifestExists = Test-Path -LiteralPath $parkedWorkshopManifestPath -PathType Leaf
}

Write-Status -Manifest $manifest -Managed $managed -Enhanced $enhanced -Legacy $legacy -Workshop $workshop
Write-Output ''
Write-Output ("Requested guide step: {0}" -f $Step)
Write-Output ("Target branch: {0}" -f $TargetBranch)
Write-Output ("Execution mode: {0}" -f ($(if ($Apply) { 'APPLY' } else { 'DRY RUN' })))
Write-Output ''

switch ($Step) {
    'Status' {
        Write-Output 'No changes made.'
        return
    }

    'PrepareUninstall' {
        Assert-FolderExists -Path $managedPath -Label 'Managed Conan'
        if ($workshop.ContentExists -and $workshop.ParkedContentExists) {
            throw "Both live and parked Workshop content folders exist. Resolve this before uninstalling: $workshopContentPath and $parkedWorkshopContentPath"
        }
        if ($workshop.ManifestExists -and $workshop.ParkedManifestExists) {
            throw "Both live and parked Workshop metadata files exist. Resolve this before uninstalling: $workshopManifestPath and $parkedWorkshopManifestPath"
        }
        if ($TargetBranch -eq 'Enhanced') {
            Assert-FolderExists -Path $enhancedPath -Label 'Enhanced parked'
            Assert-FolderMissing -Path $legacyPath -Label 'Legacy parked'
            Move-PathIfPresent -Source $workshopContentPath -Destination $parkedWorkshopContentPath -ShouldApply:$Apply
            Move-PathIfPresent -Source $workshopManifestPath -Destination $parkedWorkshopManifestPath -ShouldApply:$Apply
            Move-Folder -Source $managedPath -Destination $legacyPath -ShouldApply:$Apply
        }
        else {
            Assert-FolderExists -Path $legacyPath -Label 'Legacy parked'
            Assert-FolderMissing -Path $enhancedPath -Label 'Enhanced parked'
            Move-PathIfPresent -Source $workshopContentPath -Destination $parkedWorkshopContentPath -ShouldApply:$Apply
            Move-PathIfPresent -Source $workshopManifestPath -Destination $parkedWorkshopManifestPath -ShouldApply:$Apply
            Move-Folder -Source $managedPath -Destination $enhancedPath -ShouldApply:$Apply
        }

        Write-Output ''
        Write-Output 'Next user step: in Steam, press Uninstall for Conan Exiles.'
        Write-Output 'Do not press Install yet. After uninstall finishes, rerun this script with -Step ConfirmUninstalled.'
        return
    }

    'ConfirmUninstalled' {
        if ($manifest.Exists) {
            Write-Output 'Steam still has appmanifest_440900.acf. If Steam does not show Conan as uninstalled yet, wait or complete the uninstall first.'
        }
        else {
            Write-Output 'The Conan appmanifest is absent. Steam appears to consider Conan uninstalled.'
        }

        Write-Output ''
        Write-Output ("Next user step: in Steam, choose the {0} branch for Conan Exiles while it is uninstalled." -f $TargetBranch)
        Write-Output 'After the branch is selected, rerun this script with -Step ExposeTargetForInstall.'
        return
    }

    'ExposeTargetForInstall' {
        Assert-FolderMissing -Path $managedPath -Label 'Managed Conan'
        if ($workshop.ParkedContentExists -and $workshop.ContentExists) {
            throw "Both parked and live Workshop content folders exist. Resolve this before pressing Install: $parkedWorkshopContentPath and $workshopContentPath"
        }
        if ($workshop.ParkedManifestExists -and $workshop.ManifestExists) {
            throw "Both parked and live Workshop metadata files exist. Resolve this before pressing Install: $parkedWorkshopManifestPath and $workshopManifestPath"
        }
        if ($TargetBranch -eq 'Enhanced') {
            Assert-FolderExists -Path $enhancedPath -Label 'Enhanced parked'
            Move-Folder -Source $enhancedPath -Destination $managedPath -ShouldApply:$Apply
        }
        else {
            Assert-FolderExists -Path $legacyPath -Label 'Legacy parked'
            Move-Folder -Source $legacyPath -Destination $managedPath -ShouldApply:$Apply
        }
        Move-PathIfPresent -Source $parkedWorkshopContentPath -Destination $workshopContentPath -ShouldApply:$Apply
        Move-PathIfPresent -Source $parkedWorkshopManifestPath -Destination $workshopManifestPath -ShouldApply:$Apply

        Write-Output ''
        Write-Output 'Next user step: in Steam, press Install for Conan Exiles.'
        Write-Output 'Pick the same Steam library. Steam should discover and verify the exposed existing files and restored Workshop content instead of starting from zero.'
        Write-Output 'After Steam starts that discovery/verification step, rerun this script with -Step CheckAfterInstall.'
        return
    }

    'CheckAfterInstall' {
        if (-not $manifest.Exists) {
            Write-Output 'The appmanifest is still absent. Steam may not have started the install recognition step yet.'
            return
        }

        Write-Output 'Steam recreated the Conan appmanifest.'
        Write-Output ("Current mounted branch: {0}" -f ($(if ($manifest.MountedBeta) { $manifest.MountedBeta } else { '<default/public or none>' })))
        Write-Output ("Current requested branch: {0}" -f ($(if ($manifest.UserBeta) { $manifest.UserBeta } else { '<default/public or none>' })))
        Write-Output ("Current queued bytes: {0}/{1}" -f $manifest.BytesDownloaded, $manifest.BytesToDownload)
        Write-Output ''
        Write-Output 'If Steam says it is discovering or validating existing files, let it finish. If it instead starts a very large fresh download, stop there and preserve the folder before proceeding.'
        return
    }
}
