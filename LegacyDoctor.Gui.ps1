[CmdletBinding()]
param(
    [string]$GameRoot,
    [string]$StateRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms

$modulePath = Join-Path $PSScriptRoot 'src\ConanLegacyDoctor.psm1'
Import-Module $modulePath -Force

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Conan Legacy Doctor"
        Width="1220"
        Height="820"
        MinWidth="980"
        MinHeight="680"
        WindowStartupLocation="CenterScreen"
        Background="#F4F6F8"
        FontFamily="Segoe UI"
        FontSize="13">
    <Grid Margin="18">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="210"/>
        </Grid.RowDefinitions>

        <DockPanel Grid.Row="0" LastChildFill="True" Margin="0,0,0,14">
            <StackPanel DockPanel.Dock="Left">
                <TextBlock Text="Conan Legacy Doctor" FontSize="24" FontWeight="SemiBold" Foreground="#18202A"/>
                <TextBlock Text="Native branch triage, reversible cleanup, and support bundle export." Foreground="#53606F" Margin="0,4,0,0"/>
            </StackPanel>
            <Border DockPanel.Dock="Right" Padding="12,8" CornerRadius="6" Background="#E7EDF4" Margin="16,0,0,0">
                <StackPanel>
                    <TextBlock Text="Detected branch" Foreground="#526170" FontSize="12"/>
                    <TextBlock x:Name="BranchStatusText" Text="Not scanned" FontWeight="SemiBold" Foreground="#18202A" Margin="0,3,0,0"/>
                </StackPanel>
            </Border>
        </DockPanel>

        <Grid Grid.Row="1" Margin="0,0,0,14">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="Conan install" VerticalAlignment="Center" Margin="0,0,10,0" FontWeight="SemiBold"/>
            <TextBox x:Name="GameRootText" Grid.Column="1" Height="32" VerticalContentAlignment="Center" Padding="8,0"/>
            <Button x:Name="BrowseButton" Grid.Column="2" Content="Browse" Width="92" Height="32" Margin="10,0,0,0"/>
            <Button x:Name="ScanButton" Grid.Column="3" Content="Scan" Width="92" Height="32" Margin="10,0,0,0" Background="#2A6FDB" Foreground="White"/>
        </Grid>

        <TabControl Grid.Row="2" x:Name="MainTabs">
            <TabItem Header="Diagnostics">
                <Grid Margin="12">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>

                    <Grid Grid.Row="0" Margin="0,0,0,12">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <WrapPanel VerticalAlignment="Center">
                            <CheckBox x:Name="QuarantineModsCheck" Content="Quarantine whole Mods directory" Margin="0,0,18,8"/>
                            <CheckBox x:Name="ResetConfigCheck" Content="Reset client config folder for clean test" Margin="0,0,18,8"/>
                            <CheckBox x:Name="QuarantineSavesCheck" Content="Quarantine local save DBs for fresh vanilla boot" Margin="0,0,18,8"/>
                        </WrapPanel>
                        <StackPanel Grid.Column="1" Orientation="Horizontal">
                            <Button x:Name="PrepareButton" Content="Prepare" Width="150" Height="34" Margin="0,0,10,0" Background="#2C8A57" Foreground="White"/>
                            <Button x:Name="LaunchVanillaButton" Content="Launch Vanilla Test" Width="170" Height="34" Background="#2A6FDB" Foreground="White"/>
                        </StackPanel>
                    </Grid>

                    <DataGrid x:Name="FindingsGrid"
                              Grid.Row="1"
                              IsReadOnly="True"
                              AutoGenerateColumns="False"
                              HeadersVisibility="Column"
                              GridLinesVisibility="Horizontal"
                              CanUserAddRows="False"
                              CanUserDeleteRows="False"
                              SelectionMode="Single"
                              AlternationCount="2">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="Severity" Binding="{Binding Severity}" Width="90"/>
                            <DataGridTextColumn Header="Id" Binding="{Binding Id}" Width="220"/>
                            <DataGridTextColumn Header="Message" Binding="{Binding Message}" Width="*"/>
                            <DataGridTextColumn Header="Path" Binding="{Binding Path}" Width="330"/>
                        </DataGrid.Columns>
                    </DataGrid>
                </Grid>
            </TabItem>

            <TabItem Header="Actions">
                <Grid Margin="12">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="120"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    <DataGrid x:Name="ActionsGrid"
                              Grid.Row="0"
                              IsReadOnly="True"
                              AutoGenerateColumns="False"
                              HeadersVisibility="Column"
                              GridLinesVisibility="Horizontal"
                              CanUserAddRows="False"
                              CanUserDeleteRows="False"
                              SelectionMode="Single"
                              AlternationCount="2">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="Action Id" Binding="{Binding Id}" Width="240"/>
                            <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="110"/>
                            <DataGridTextColumn Header="What the doctor did" Binding="{Binding Summary}" Width="*"/>
                            <DataGridTextColumn Header="Created" Binding="{Binding CreatedAtUtc}" Width="210"/>
                            <DataGridTextColumn Header="Game Root" Binding="{Binding GameRoot}" Width="*"/>
                        </DataGrid.Columns>
                    </DataGrid>
                    <Border Grid.Row="1" Background="White" BorderBrush="#D2D9E1" BorderThickness="1" Padding="10" Margin="0,12,0,0">
                        <TextBox x:Name="ActionDetailsText"
                                 AcceptsReturn="True"
                                 IsReadOnly="True"
                                 TextWrapping="Wrap"
                                 VerticalScrollBarVisibility="Auto"
                                 BorderThickness="0"
                                 Background="Transparent"/>
                    </Border>
                    <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
                        <CheckBox x:Name="ForceRestoreCheck" Content="Force overwrite changed restore targets" VerticalAlignment="Center" Margin="0,0,16,0"/>
                        <Button x:Name="RefreshActionsButton" Content="Refresh" Width="100" Height="32" Margin="0,0,10,0"/>
                        <Button x:Name="RestoreButton" Content="Undo Selected" Width="140" Height="32" Background="#8A4B2C" Foreground="White"/>
                    </StackPanel>
                </Grid>
            </TabItem>

            <TabItem Header="Support Bundle">
                <Grid Margin="12">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <TextBlock Grid.Row="0"
                               Text="The bundle always includes scan and transaction summaries. Logs and config snapshots are explicit opt-ins."
                               Foreground="#53606F"
                               TextWrapping="Wrap"/>
                    <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,16,0,16">
                        <CheckBox x:Name="IncludeLogsCheck" Content="Include recent log tails" Margin="0,0,18,0"/>
                        <CheckBox x:Name="IncludeConfigCheck" Content="Include Engine.ini, Game.ini, and modlist.txt snapshots"/>
                    </StackPanel>
                    <StackPanel Grid.Row="2" Orientation="Horizontal" VerticalAlignment="Top">
                        <Button x:Name="ExportBundleButton" Content="Export ZIP Bundle" Width="170" Height="36" Background="#5D5BCE" Foreground="White"/>
                    </StackPanel>
                </Grid>
            </TabItem>
        </TabControl>

        <Grid Grid.Row="3" Margin="0,14,0,0">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="Activity" FontWeight="SemiBold" Foreground="#18202A" Margin="0,0,0,8"/>
            <TextBox x:Name="ActivityText"
                     Grid.Row="1"
                     AcceptsReturn="True"
                     AcceptsTab="False"
                     IsReadOnly="True"
                     TextWrapping="Wrap"
                     VerticalScrollBarVisibility="Auto"
                     HorizontalScrollBarVisibility="Disabled"
                     Padding="10"
                     Background="White"/>
        </Grid>
    </Grid>
</Window>
'@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [Windows.Markup.XamlReader]::Load($reader)

function Get-Control {
    param([Parameter(Mandatory)][string]$Name)
    $window.FindName($Name)
}

$gameRootText = Get-Control -Name 'GameRootText'
$branchStatusText = Get-Control -Name 'BranchStatusText'
$activityText = Get-Control -Name 'ActivityText'
$findingsGrid = Get-Control -Name 'FindingsGrid'
$actionsGrid = Get-Control -Name 'ActionsGrid'
$actionDetailsText = Get-Control -Name 'ActionDetailsText'
$quarantineModsCheck = Get-Control -Name 'QuarantineModsCheck'
$resetConfigCheck = Get-Control -Name 'ResetConfigCheck'
$quarantineSavesCheck = Get-Control -Name 'QuarantineSavesCheck'
$forceRestoreCheck = Get-Control -Name 'ForceRestoreCheck'
$includeLogsCheck = Get-Control -Name 'IncludeLogsCheck'
$includeConfigCheck = Get-Control -Name 'IncludeConfigCheck'

if (-not [string]::IsNullOrWhiteSpace($GameRoot)) {
    $gameRootText.Text = $GameRoot
}

function Add-Activity {
    param([Parameter(Mandatory)][string]$Message)

    $timestamp = [DateTimeOffset]::Now.ToString('HH:mm:ss')
    $activityText.AppendText("[$timestamp] $Message`r`n")
    $activityText.ScrollToEnd()
}

function Get-SelectedGameRoot {
    $value = $gameRootText.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    return $value
}

function Show-InstallSelectionDialog {
    param([Parameter(Mandatory)][object[]]$Candidates)

    [xml]$dialogXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Choose Conan Install"
        Width="760"
        Height="420"
        ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner"
        Background="#F4F6F8"
        FontFamily="Segoe UI"
        FontSize="13">
    <Grid Margin="18">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <StackPanel Grid.Row="0" Margin="0,0,0,12">
            <TextBlock Text="More than one Conan install was found." FontSize="20" FontWeight="SemiBold" Foreground="#18202A"/>
            <TextBlock Text="This is normal for side-by-side Enhanced and Legacy setups. Choose the install you want the doctor to inspect or repair."
                       Foreground="#53606F"
                       TextWrapping="Wrap"
                       Margin="0,6,0,0"/>
        </StackPanel>
        <ListBox x:Name="CandidateList" Grid.Row="1" DisplayMemberPath="DisplayName" Margin="0,0,0,12"/>
        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
            <Button x:Name="CancelButton" Content="Cancel" Width="96" Height="32" Margin="0,0,10,0"/>
            <Button x:Name="ChooseButton" Content="Use This Install" Width="150" Height="32" Background="#2A6FDB" Foreground="White"/>
        </StackPanel>
    </Grid>
</Window>
'@

    $reader = New-Object System.Xml.XmlNodeReader $dialogXaml
    $dialog = [Windows.Markup.XamlReader]::Load($reader)
    $dialog.Owner = $window
    $candidateList = $dialog.FindName('CandidateList')
    $candidateList.ItemsSource = $Candidates
    $candidateList.SelectedIndex = 0

    $selectedPath = $null
    $dialog.FindName('ChooseButton').Add_Click({
        if ($candidateList.SelectedItem) {
            $script:InstallChoiceDialogSelection = $candidateList.SelectedItem.Path
            $dialog.DialogResult = $true
        }
    })
    $dialog.FindName('CancelButton').Add_Click({
        $dialog.DialogResult = $false
    })

    $script:InstallChoiceDialogSelection = $null
    [void]$dialog.ShowDialog()
    $selectedPath = $script:InstallChoiceDialogSelection
    Remove-Variable -Name InstallChoiceDialogSelection -Scope Script -ErrorAction SilentlyContinue
    return $selectedPath
}

function Resolve-InteractiveGameRoot {
    $selected = Get-SelectedGameRoot
    if (-not [string]::IsNullOrWhiteSpace($selected)) {
        return $selected
    }

    $candidates = @(Get-LegacyDoctorInstallCandidates)
    if ($candidates.Count -eq 0) {
        return $null
    }

    if ($candidates.Count -eq 1) {
        $gameRootText.Text = $candidates[0].Path
        Add-Activity -Message ("Detected Conan install automatically: {0}" -f $candidates[0].Path)
        return $candidates[0].Path
    }

    $choice = Show-InstallSelectionDialog -Candidates $candidates
    if (-not [string]::IsNullOrWhiteSpace($choice)) {
        $gameRootText.Text = $choice
        Add-Activity -Message ("Selected Conan install: {0}" -f $choice)
    }

    return $choice
}

function Refresh-Actions {
    $actions = @(Get-LegacyDoctorActions -StateRoot $StateRoot)
    $actionsGrid.ItemsSource = $actions
    $actionDetailsText.Text = if ($actions.Count -eq 0) {
        'No recorded actions yet.'
    }
    else {
        'Select an action above to see exactly what the doctor did.'
    }
    Add-Activity -Message ("Loaded {0} action record(s)." -f $actions.Count)
}

function Get-FriendlyBranchLabel {
    param([Parameter(Mandatory)][string]$BranchMode)

    switch ($BranchMode) {
        'Legacy' { return 'Steam Legacy branch' }
        'EnhancedOrDefault' { return 'Steam default / Enhanced branch' }
        'LegacySideBySideCopy' { return 'Likely side-by-side Legacy copy' }
        'DetachedSideBySideCopy' { return 'Detached side-by-side copy' }
        default { return $BranchMode }
    }
}

function Run-Scan {
    try {
        $gameRoot = Resolve-InteractiveGameRoot
        $scan = Get-LegacyDoctorScan -GameRoot $gameRoot
        $script:CurrentScan = $scan
        $findingsGrid.ItemsSource = @($scan.Findings)
        $branchStatusText.Text = "{0} / {1}" -f (Get-FriendlyBranchLabel -BranchMode $scan.Branch.BranchMode), $scan.Branch.Confidence
        $gameRootText.Text = $scan.GameRoot
        Add-Activity -Message ("Scan complete for {0}. {1} finding(s)." -f $scan.GameRoot, @($scan.Findings).Count)
    }
    catch {
        [System.Windows.MessageBox]::Show($_.Exception.Message, 'Scan failed', 'OK', 'Error') | Out-Null
        Add-Activity -Message ("Scan failed: {0}" -f $_.Exception.Message)
    }
}

(Get-Control -Name 'BrowseButton').Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the Conan Exiles or Conan Exiles Enhanced install folder.'
    $dialog.ShowNewFolderButton = $false
    if (-not [string]::IsNullOrWhiteSpace($gameRootText.Text)) {
        $dialog.SelectedPath = $gameRootText.Text
    }

    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $gameRootText.Text = $dialog.SelectedPath
        Add-Activity -Message ("Selected install folder: {0}" -f $dialog.SelectedPath)
    }
})

(Get-Control -Name 'ScanButton').Add_Click({
    Run-Scan
})

(Get-Control -Name 'PrepareButton').Add_Click({
    try {
        $transaction = Invoke-LegacyPreparation `
            -GameRoot (Resolve-InteractiveGameRoot) `
            -QuarantineModsDirectory:$quarantineModsCheck.IsChecked `
            -ResetClientConfig:$resetConfigCheck.IsChecked `
            -QuarantineSaveDatabases:$quarantineSavesCheck.IsChecked `
            -StateRoot $StateRoot
        Add-Activity -Message ("Prepared reversible action {0}." -f $transaction.Id)
        Refresh-Actions
        Run-Scan
        [System.Windows.MessageBox]::Show("Preparation action created:`n$($transaction.Id)", 'Preparation complete', 'OK', 'Information') | Out-Null
    }
    catch {
        [System.Windows.MessageBox]::Show($_.Exception.Message, 'Preparation failed', 'OK', 'Error') | Out-Null
        Add-Activity -Message ("Preparation failed: {0}" -f $_.Exception.Message)
    }
})

(Get-Control -Name 'LaunchVanillaButton').Add_Click({
    try {
        $launch = Start-LegacyDoctorVanillaLaunch -GameRoot (Resolve-InteractiveGameRoot)
        Add-Activity -Message ("Vanilla test launch started through {0} for {1}." -f $launch.LaunchStrategy, $launch.GameRoot)
        [System.Windows.MessageBox]::Show(
            "Vanilla launch started.`n`nIf you quarantined the local save DBs, let the game reach a stable menu or create a fresh local save before closing it. The original DBs remain recoverable from the recorded action.",
            'Vanilla launch started',
            'OK',
            'Information'
        ) | Out-Null
    }
    catch {
        [System.Windows.MessageBox]::Show($_.Exception.Message, 'Vanilla launch blocked', 'OK', 'Warning') | Out-Null
        Add-Activity -Message ("Vanilla launch blocked: {0}" -f $_.Exception.Message)
    }
})

(Get-Control -Name 'RefreshActionsButton').Add_Click({
    Refresh-Actions
})

$actionsGrid.Add_SelectionChanged({
    $selected = $actionsGrid.SelectedItem
    if (-not $selected) {
        $actionDetailsText.Text = 'Select an action above to see exactly what the doctor did.'
        return
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($detail in @($selected.Details)) {
        $lines.Add(("- {0}" -f $detail))
    }

    foreach ($warning in @($selected.Warnings)) {
        $lines.Add(("- Warning: {0}" -f $warning))
    }

    $actionDetailsText.Text = if ($lines.Count -eq 0) {
        'No completed file actions were recorded for this entry.'
    }
    else {
        $lines -join [Environment]::NewLine
    }
})

(Get-Control -Name 'RestoreButton').Add_Click({
    try {
        $selected = $actionsGrid.SelectedItem
        if (-not $selected) {
            throw 'Select an action to undo.'
        }

        $transaction = Restore-LegacyDoctorTransaction `
            -TransactionId $selected.Id `
            -Force:$forceRestoreCheck.IsChecked `
            -StateRoot $StateRoot
        Add-Activity -Message ("Undo processed for action {0}. Status: {1}." -f $transaction.Id, $transaction.Status)
        Refresh-Actions
        Run-Scan
        [System.Windows.MessageBox]::Show("Undo processed for action:`n$($transaction.Id)", 'Undo complete', 'OK', 'Information') | Out-Null
    }
    catch {
        [System.Windows.MessageBox]::Show($_.Exception.Message, 'Undo failed', 'OK', 'Error') | Out-Null
        Add-Activity -Message ("Undo failed: {0}" -f $_.Exception.Message)
    }
})

(Get-Control -Name 'ExportBundleButton').Add_Click({
    try {
        $dialog = New-Object Microsoft.Win32.SaveFileDialog
        $dialog.Filter = 'ZIP archive (*.zip)|*.zip'
        $dialog.DefaultExt = '.zip'
        $dialog.AddExtension = $true
        $dialog.FileName = 'ConanLegacyDoctor-support.zip'

        if ($dialog.ShowDialog()) {
            $bundle = Export-LegacySupportBundle `
                -GameRoot (Resolve-InteractiveGameRoot) `
                -DestinationPath $dialog.FileName `
                -IncludeRecentLogs:$includeLogsCheck.IsChecked `
                -IncludeConfigSnapshots:$includeConfigCheck.IsChecked `
                -StateRoot $StateRoot
            Add-Activity -Message ("Support bundle exported: {0}" -f $bundle.DestinationPath)
            [System.Windows.MessageBox]::Show("Support bundle created:`n$($bundle.DestinationPath)", 'Bundle exported', 'OK', 'Information') | Out-Null
        }
    }
    catch {
        [System.Windows.MessageBox]::Show($_.Exception.Message, 'Bundle export failed', 'OK', 'Error') | Out-Null
        Add-Activity -Message ("Bundle export failed: {0}" -f $_.Exception.Message)
    }
})

$window.Add_Loaded({
    Add-Activity -Message 'Ready. If both Enhanced and Legacy are installed, the doctor will ask which install to inspect before it scans or changes anything.'
    Add-Activity -Message 'Use Prepare first. Vanilla launch stays blocked while an active modlist.txt is still present.'
    Refresh-Actions
})

[void]$window.ShowDialog()
