Describe 'Conan Legacy Doctor' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '..\src\ConanLegacyDoctor.psm1') -Force
    }

    It 'detects a modlist and Engine.ini build overrides during scan' {
        $root = Join-Path $TestDrive 'Conan Exiles Enhanced'
        $mods = Join-Path $root 'ConanSandbox\Mods'
        $config = Join-Path $root 'ConanSandbox\Saved\Config\WindowsNoEditor'
        New-Item -ItemType Directory -Path $mods -Force | Out-Null
        New-Item -ItemType Directory -Path $config -Force | Out-Null

        Set-Content -LiteralPath (Join-Path $mods 'modlist.txt') -Value @('mod-a.pak', 'mod-b.pak')
        Set-Content -LiteralPath (Join-Path $config 'Engine.ini') -Value @(
            '[OnlineSubsystem]',
            'bUseBuildIdOverride=True',
            'BuildIdOverride=460304166'
        )

        $scan = Get-LegacyDoctorScan -GameRoot $root
        @($scan.Findings | Where-Object Id -eq 'mods.modlist').Count | Should Be 1
        @($scan.Findings | Where-Object Id -eq 'config.engine-build-override').Count | Should Be 1
    }

    It 'prepares and restores the baseline reversible cleanup' {
        $root = Join-Path $TestDrive 'Conan Exiles Enhanced'
        $mods = Join-Path $root 'ConanSandbox\Mods'
        $config = Join-Path $root 'ConanSandbox\Saved\Config\WindowsNoEditor'
        $stateRoot = Join-Path $TestDrive 'state'
        New-Item -ItemType Directory -Path $mods -Force | Out-Null
        New-Item -ItemType Directory -Path $config -Force | Out-Null

        $modlistPath = Join-Path $mods 'modlist.txt'
        $enginePath = Join-Path $config 'Engine.ini'
        Set-Content -LiteralPath $modlistPath -Value 'mod-a.pak'
        Set-Content -LiteralPath $enginePath -Value @(
            '[OnlineSubsystem]',
            'bUseBuildIdOverride=True',
            'BuildIdOverride=460304166',
            'SomeOtherSetting=True'
        )

        $transaction = Invoke-LegacyPreparation -GameRoot $root -StateRoot $stateRoot
        Test-Path -LiteralPath $modlistPath | Should Be $false
        (Get-Content -LiteralPath $enginePath -Raw) | Should Not Match 'BuildIdOverride'
        (Get-Content -LiteralPath $enginePath -Raw) | Should Match 'SomeOtherSetting=True'

        $restored = Restore-LegacyDoctorTransaction -TransactionId $transaction.Id -StateRoot $stateRoot
        $restored.Status | Should Be 'restored'
        Test-Path -LiteralPath $modlistPath | Should Be $true
        (Get-Content -LiteralPath $enginePath -Raw) | Should Match 'BuildIdOverride=460304166'
    }

    It 'classifies the Steam legacy beta branch from the app manifest' {
        $steamRoot = Join-Path $TestDrive 'SteamLibrary'
        $steamApps = Join-Path $steamRoot 'steamapps'
        $root = Join-Path $steamApps 'common\Conan Exiles Enhanced'
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        New-Item -ItemType Directory -Path $steamApps -Force | Out-Null

        Set-Content -LiteralPath (Join-Path $steamApps 'appmanifest_440900.acf') -Value @(
            '"AppState"',
            '{',
            '    "appid" "440900"',
            '    "installdir" "Conan Exiles Enhanced"',
            '    "buildid" "123456"',
            '    "UserConfig"',
            '    {',
            '        "betakey" "conan-exiles-legacy"',
            '    }',
            '}'
        )

        $scan = Get-LegacyDoctorScan -GameRoot $root
        $scan.Branch.BranchMode | Should Be 'Legacy'
        $scan.Branch.Confidence | Should Be 'high'
    }

    It 'does not mislabel a Conay-style side-by-side Legacy folder from the nearby Enhanced manifest' {
        $steamRoot = Join-Path $TestDrive 'SteamLibraryDetached'
        $steamApps = Join-Path $steamRoot 'steamapps'
        $root = Join-Path $steamApps 'common\Conan Exiles Legacy'
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        New-Item -ItemType Directory -Path $steamApps -Force | Out-Null

        Set-Content -LiteralPath (Join-Path $steamApps 'appmanifest_440900.acf') -Value @(
            '"AppState"',
            '{',
            '    "appid" "440900"',
            '    "installdir" "Conan Exiles Enhanced"',
            '    "buildid" "123456"',
            '}'
        )

        $scan = Get-LegacyDoctorScan -GameRoot $root
        $scan.Branch.BranchMode | Should Be 'LegacySideBySideCopy'
        $scan.Branch.Confidence | Should Be 'medium'
    }

    It 'backs up TotCustom, moves ModControllerCache aside, and exposes readable action details' {
        $root = Join-Path $TestDrive 'Conan Exiles Enhanced'
        $tot = Join-Path $root 'ConanSandbox\Saved\SaveGames\TotCustom'
        $saved = Join-Path $root 'ConanSandbox\Saved'
        $stateRoot = Join-Path $TestDrive 'state-totcustom'
        New-Item -ItemType Directory -Path $tot -Force | Out-Null
        New-Item -ItemType Directory -Path $saved -Force | Out-Null

        $totFile = Join-Path $tot 'player.json'
        $cachePath = Join-Path $saved 'ModControllerCache.json'
        Set-Content -LiteralPath $totFile -Value '{"looks":"important"}'
        Set-Content -LiteralPath $cachePath -Value '{"cache":true}'

        $transaction = Invoke-LegacyPreparation -GameRoot $root -StateRoot $stateRoot
        Test-Path -LiteralPath $cachePath | Should Be $false

        $backupRoot = Join-Path $stateRoot 'backups\TotCustom'
        @(
            Get-ChildItem -LiteralPath $backupRoot -Recurse -Filter 'TotCustom_1.zip' -File -ErrorAction SilentlyContinue
        ).Count | Should Be 1

        $actions = @(Get-LegacyDoctorActions -StateRoot $stateRoot)
        $matching = @($actions | Where-Object Id -eq $transaction.Id)
        $matching.Count | Should Be 1
        ($matching[0].Details -join "`n") | Should Match 'Created a rotating TotCustom backup'
        ($matching[0].Details -join "`n") | Should Match 'ModControllerCache'

        $restored = Restore-LegacyDoctorTransaction -TransactionId $transaction.Id -StateRoot $stateRoot
        $restored.Status | Should Be 'restored'
        Test-Path -LiteralPath $cachePath | Should Be $true
    }

    It 'quarantines save databases reversibly after making safety copies' {
        $root = Join-Path $TestDrive 'Conan Exiles Enhanced'
        $saved = Join-Path $root 'ConanSandbox\Saved'
        $stateRoot = Join-Path $TestDrive 'state-save-quarantine'
        New-Item -ItemType Directory -Path $saved -Force | Out-Null

        $savePaths = @(
            (Join-Path $saved 'game.db'),
            (Join-Path $saved 'game_0.db'),
            (Join-Path $saved 'dlc_siptah.db')
        )

        foreach ($savePath in $savePaths) {
            Set-Content -LiteralPath $savePath -Value ("payload:{0}" -f (Split-Path -Leaf $savePath))
        }

        $transaction = Invoke-LegacyPreparation `
            -GameRoot $root `
            -QuarantineSaveDatabases `
            -StateRoot $stateRoot

        foreach ($savePath in $savePaths) {
            Test-Path -LiteralPath $savePath | Should Be $false
        }

        $saveQuarantine = Join-Path (Join-Path $stateRoot ("transactions\{0}\quarantine" -f $transaction.Id)) 'SaveDatabases'
        Test-Path -LiteralPath (Join-Path $saveQuarantine 'game.db') | Should Be $true
        Test-Path -LiteralPath (Join-Path $saveQuarantine 'game.db.backup') | Should Be $true

        $actions = @(Get-LegacyDoctorActions -StateRoot $stateRoot)
        $details = (@($actions | Where-Object Id -eq $transaction.Id)[0].Details -join "`n")
        $details | Should Match 'Created a safety copy'
        $details | Should Match 'Moved'

        $restored = Restore-LegacyDoctorTransaction -TransactionId $transaction.Id -StateRoot $stateRoot
        $restored.Status | Should Be 'restored'
        foreach ($savePath in $savePaths) {
            Test-Path -LiteralPath $savePath | Should Be $true
        }
    }

    It 'builds a safe direct vanilla launch plan and blocks active modlists' {
        $root = Join-Path $TestDrive 'SteamLibraryLaunch\steamapps\common\Conan Exiles Legacy'
        $steamApps = Split-Path -Parent (Split-Path -Parent $root)
        $binaryRoot = Join-Path $root 'ConanSandbox\Binaries\Win64'
        $mods = Join-Path $root 'ConanSandbox\Mods'
        New-Item -ItemType Directory -Path $binaryRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $mods -Force | Out-Null
        New-Item -ItemType Directory -Path $steamApps -Force | Out-Null

        Set-Content -LiteralPath (Join-Path $binaryRoot 'ConanSandbox.exe') -Value 'stub'
        Set-Content -LiteralPath (Join-Path $mods 'modlist.txt') -Value 'mod-a.pak'
        Set-Content -LiteralPath (Join-Path $steamApps 'appmanifest_440900.acf') -Value @(
            '"AppState"',
            '{',
            '    "appid" "440900"',
            '    "installdir" "Conan Exiles Legacy"',
            '    "UserConfig"',
            '    {',
            '        "betakey" "conan-exiles-legacy"',
            '    }',
            '}'
        )

        $plan = Get-LegacyDoctorVanillaLaunchPlan -GameRoot $root
        $plan.LaunchStrategy | Should Be 'DirectExecutable'
        $plan.ExecutablePath | Should Match 'ConanSandbox\.exe$'
        $plan.VanillaReady | Should Be $false
        (@($plan.Warnings) -join "`n") | Should Match 'Active mod list detected'
    }

    It 'only allows Steam fallback when the manifest confirms the selected install' {
        $steamRoot = Join-Path $TestDrive 'SteamLibraryFallback'
        $steamApps = Join-Path $steamRoot 'steamapps'
        $managedRoot = Join-Path $steamApps 'common\Conan Exiles Enhanced'
        $detachedRoot = Join-Path $steamApps 'common\Conan Exiles Legacy'
        New-Item -ItemType Directory -Path $managedRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $detachedRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $steamApps -Force | Out-Null

        Set-Content -LiteralPath (Join-Path $steamApps 'appmanifest_440900.acf') -Value @(
            '"AppState"',
            '{',
            '    "appid" "440900"',
            '    "installdir" "Conan Exiles Enhanced"',
            '}'
        )

        $managedPlan = Get-LegacyDoctorVanillaLaunchPlan -GameRoot $managedRoot
        $managedPlan.LaunchStrategy | Should Be 'SteamUri'
        $managedPlan.SteamFallbackAllowed | Should Be $true

        $detachedPlan = Get-LegacyDoctorVanillaLaunchPlan -GameRoot $detachedRoot
        $detachedPlan.LaunchStrategy | Should Be 'Unavailable'
        $detachedPlan.SteamFallbackAllowed | Should Be $false
    }

    It 'exports a support bundle without save databases' {
        Add-Type -AssemblyName System.IO.Compression.FileSystem

        $root = Join-Path $TestDrive 'Conan Exiles Enhanced'
        $logs = Join-Path $root 'ConanSandbox\Saved\Logs'
        $config = Join-Path $root 'ConanSandbox\Saved\Config\WindowsNoEditor'
        $mods = Join-Path $root 'ConanSandbox\Mods'
        $stateRoot = Join-Path $TestDrive 'state-bundle'
        $destination = Join-Path $TestDrive 'support.zip'
        New-Item -ItemType Directory -Path $logs -Force | Out-Null
        New-Item -ItemType Directory -Path $config -Force | Out-Null
        New-Item -ItemType Directory -Path $mods -Force | Out-Null

        Set-Content -LiteralPath (Join-Path $logs 'ConanSandbox.log') -Value 'D3D12 driver version test'
        Set-Content -LiteralPath (Join-Path $config 'Engine.ini') -Value '[OnlineSubsystem]'
        Set-Content -LiteralPath (Join-Path $config 'Game.ini') -Value '[Game]'
        Set-Content -LiteralPath (Join-Path $mods 'modlist.txt') -Value 'mod-a.pak'
        Set-Content -LiteralPath (Join-Path $root 'ConanSandbox\Saved\game.db') -Value 'should-not-ship'

        $bundle = Export-LegacySupportBundle `
            -GameRoot $root `
            -DestinationPath $destination `
            -IncludeRecentLogs `
            -IncludeConfigSnapshots `
            -StateRoot $stateRoot

        Test-Path -LiteralPath $bundle.DestinationPath | Should Be $true

        $archive = [System.IO.Compression.ZipFile]::OpenRead($bundle.DestinationPath)
        try {
            $entries = @($archive.Entries | ForEach-Object { $_.FullName })
            @($entries | Where-Object { $_ -eq 'bundle-metadata.json' }).Count | Should Be 1
            @($entries | Where-Object { $_ -eq 'scan.json' }).Count | Should Be 1
            @($entries | Where-Object { $_ -eq 'actions.json' }).Count | Should Be 1
            @($entries | Where-Object { $_ -match '^logs[\\/]' }).Count | Should Be 1
            @($entries | Where-Object { $_ -match '^config[\\/]Engine\.ini$' }).Count | Should Be 1
            @($entries | Where-Object { $_ -like '*game.db*' }).Count | Should Be 0
        }
        finally {
            $archive.Dispose()
        }
    }
}
