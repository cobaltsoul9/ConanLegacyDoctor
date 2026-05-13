using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using ConanLegacyDoctor.Core;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ConanLegacyDoctor.App;

public partial class MainWindow : Window
{
    private readonly LegacyDoctorService _doctor = new();
    private readonly ObservableCollection<Finding> _findings = [];
    private readonly ObservableCollection<DoctorAction> _actions = [];
    private readonly ObservableCollection<TotCustomSource> _totCustomSources = [];

    private enum BranchSwitchWizardStage
    {
        Starting,
        AwaitingSteamUninstall,
        AwaitingBranchSelection,
        AwaitingSteamInstall,
        InstallDetected,
        Completed,
        Blocked
    }

    private sealed record BranchSwitchOption(string Target, int Priority, string Recommendation);

    private sealed record WizardStatusCard(
        System.Windows.Controls.Border Root,
        System.Windows.Controls.TextBlock Header,
        System.Windows.Controls.TextBlock Detail);

    public MainWindow()
    {
        InitializeComponent();
        FindingsGrid.ItemsSource = _findings;
        ActionsGrid.ItemsSource = _actions;
        TotCustomSourcesGrid.ItemsSource = _totCustomSources;
        Loaded += (_, _) =>
        {
            AddActivity("Ready. If both Enhanced and Legacy are installed, the doctor will ask which install to inspect before it scans or changes anything.");
            AddActivity("Use Prepare first. Vanilla launch stays blocked while an active modlist.txt is still present.");
            RefreshActions();
            RefreshTotCustomSources();
            RefreshAssistantAssessment();
        };
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select the Conan Exiles install folder.",
            ShowNewFolderButton = false,
            SelectedPath = string.IsNullOrWhiteSpace(GameRootText.Text) ? string.Empty : GameRootText.Text
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            GameRootText.Text = dialog.SelectedPath;
            AddActivity($"Selected install folder: {dialog.SelectedPath}");
        }
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) => RunScan();

    private void PrepareButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = ResolveInteractiveGameRoot();
            var transaction = _doctor.PrepareLegacy(
                root,
                new PreparationOptions(
                    QuarantineModsCheck.IsChecked == true,
                    ResetConfigCheck.IsChecked == true,
                    QuarantineSavesCheck.IsChecked == true));

            AddActivity($"Prepared reversible action {transaction.Id}.");
            RefreshActions();
            RefreshTotCustomSources();
            RunScan();
            System.Windows.MessageBox.Show($"Preparation action created:{Environment.NewLine}{transaction.Id}", "Preparation complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Preparation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            AddActivity($"Preparation failed: {ex.Message}");
        }
    }

    private void LaunchVanillaButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = ResolveInteractiveGameRoot();
            var launch = _doctor.StartVanillaLaunch(root);
            AddActivity($"Vanilla test launch started through {launch.LaunchStrategy} for {launch.GameRoot}.");
            System.Windows.MessageBox.Show(
                "Vanilla launch started." + Environment.NewLine + Environment.NewLine +
                "If you quarantined the local save DBs, let the game reach a stable menu or create a fresh local save before closing it. The original DBs remain recoverable from the recorded action.",
                "Vanilla launch started",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Vanilla launch blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            AddActivity($"Vanilla launch blocked: {ex.Message}");
        }
    }

    private void VerifySteamFilesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = ResolveInteractiveGameRoot();
            var validation = _doctor.StartSteamValidation(root);
            AddActivity($"Steam file validation opened for the Steam-managed Conan target tied to {validation.GameRoot}.");
            System.Windows.MessageBox.Show(
                "Steam's Conan Exiles file validation was opened." + Environment.NewLine + Environment.NewLine +
                "Let Steam finish completely before launching the game again.",
                "Steam file check opened",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Steam file check not opened", MessageBoxButton.OK, MessageBoxImage.Warning);
            AddActivity($"Steam file check not opened: {ex.Message}");
        }
    }

    private void RefreshAssessmentButton_Click(object sender, RoutedEventArgs e) => RefreshAssistantAssessment();

    private void OpenRepairAssistantButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = DiagnosticsTab;
        RunScan();
    }

    private void OpenTotSavesButton_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedItem = TotSavesTab;

    private void OpenEnhancedSwitchWizardButton_Click(object sender, RoutedEventArgs e) => ShowBranchSwitchWizard("Enhanced");

    private void OpenLegacySwitchWizardButton_Click(object sender, RoutedEventArgs e) => ShowBranchSwitchWizard("Legacy");

    private void RefreshActionsButton_Click(object sender, RoutedEventArgs e) => RefreshActions();

    private void ActionsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ActionsGrid.SelectedItem is not DoctorAction selected)
        {
            ActionDetailsText.Text = "Select an action above to see exactly what the doctor did.";
            return;
        }

        var lines = selected.Details.Select(detail => $"- {detail}")
            .Concat(selected.Warnings.Select(warning => $"- Warning: {warning}"))
            .ToList();

        ActionDetailsText.Text = lines.Count == 0
            ? "No completed file actions were recorded for this entry."
            : string.Join(Environment.NewLine, lines);
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ActionsGrid.SelectedItem is not DoctorAction selected)
            {
                throw new InvalidOperationException("Select an action to undo.");
            }

            var transaction = _doctor.Restore(selected.Id, ForceRestoreCheck.IsChecked == true);
            AddActivity($"Undo processed for action {transaction.Id}. Status: {transaction.Status}.");
            RefreshActions();
            RefreshTotCustomSources();
            RunScan();
            System.Windows.MessageBox.Show($"Undo processed for action:{Environment.NewLine}{transaction.Id}", "Undo complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Undo failed", MessageBoxButton.OK, MessageBoxImage.Error);
            AddActivity($"Undo failed: {ex.Message}");
        }
    }

    private void ExportBundleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "ZIP archive (*.zip)|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                FileName = "ConanLegacyDoctor-support.zip"
            };

            if (dialog.ShowDialog(this) == true)
            {
                var bundle = _doctor.ExportSupportBundle(
                    ResolveInteractiveGameRoot(),
                    new SupportBundleOptions(
                        dialog.FileName,
                        IncludeLogsCheck.IsChecked == true,
                        IncludeConfigCheck.IsChecked == true));

                AddActivity($"Support bundle exported: {bundle.DestinationPath}");
                System.Windows.MessageBox.Show($"Support bundle created:{Environment.NewLine}{bundle.DestinationPath}", "Bundle exported", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Bundle export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            AddActivity($"Bundle export failed: {ex.Message}");
        }
    }

    private void RefreshTotCustomSourcesButton_Click(object sender, RoutedEventArgs e) => RefreshTotCustomSources();

    private void TotCustomSourcesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TotCustomSourcesGrid.SelectedItem is not TotCustomSource selected)
        {
            TotCustomSourceDetailsText.Text = "Select a dated TotCustom source to see where it came from.";
            return;
        }

        var sizeText = selected.SizeBytes is null
            ? "Size not available."
            : $"{selected.SizeBytes:N0} bytes";
        TotCustomSourceDetailsText.Text =
            $"{selected.DisplayName}{Environment.NewLine}" +
            $"{selected.Detail}{Environment.NewLine}" +
            $"Captured: {selected.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"Size: {sizeText}{Environment.NewLine}" +
            $"Source path: {selected.SourcePath}";
    }

    private void RestoreTotCustomButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TotCustomSourcesGrid.SelectedItem is not TotCustomSource selected)
            {
                throw new InvalidOperationException("Select a TotCustom backup or Enhanced source first.");
            }

            var transaction = _doctor.RestoreTotCustomSource(ResolveInteractiveGameRoot(), selected.Id);
            AddActivity($"Loaded TotCustom source through reversible action {transaction.Id}.");
            RefreshActions();
            RefreshTotCustomSources();
            RunScan();
            System.Windows.MessageBox.Show(
                "TotCustom was loaded into the selected install." + Environment.NewLine + Environment.NewLine +
                "The previous TotCustom folder was moved aside inside a recorded action first, so you can undo this from the Actions tab if needed.",
                "TotCustom loaded",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "TotCustom load failed", MessageBoxButton.OK, MessageBoxImage.Error);
            AddActivity($"TotCustom load failed: {ex.Message}");
        }
    }

    private void RunScan()
    {
        try
        {
            var root = ResolveInteractiveGameRoot();
            var scan = _doctor.Scan(root);
            _findings.Clear();
            foreach (var finding in scan.Findings)
            {
                _findings.Add(finding);
            }

            BranchStatusText.Text = $"{GetFriendlyBranchLabel(scan.Branch.BranchMode)} / {scan.Branch.Confidence}";
            ApplyInstallRole(scan.Branch);
            GameRootText.Text = scan.GameRoot;
            AddActivity($"Scan complete for {scan.GameRoot}. {scan.Findings.Count} finding(s).");
            RefreshTotCustomSources();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            AddActivity($"Scan failed: {ex.Message}");
        }
    }

    private string ResolveInteractiveGameRoot()
    {
        if (!string.IsNullOrWhiteSpace(GameRootText.Text))
        {
            return GameRootText.Text.Trim();
        }

        var candidates = _doctor.GetInstallCandidates();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No Conan install was discovered automatically. Browse to the install folder first.");
        }

        if (candidates.Count == 1)
        {
            GameRootText.Text = candidates[0].Path;
            AddActivity($"Detected Conan install automatically: {candidates[0].Path}");
            return candidates[0].Path;
        }

        var choice = ShowInstallSelectionDialog(candidates);
        if (choice is null)
        {
            throw new InvalidOperationException("Choose a Conan install to continue.");
        }

        GameRootText.Text = choice.Path;
        AddActivity($"Selected Conan install: {choice.Path}");
        return choice.Path;
    }

    private InstallCandidate? ShowInstallSelectionDialog(IReadOnlyList<InstallCandidate> candidates)
    {
        var dialog = new Window
        {
            Title = "Choose Conan Install",
            Owner = this,
            Width = 780,
            Height = 440,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White
        };

        var root = new System.Windows.Controls.Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var header = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "More than one Conan install was found.",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Choose the install you want to inspect or change. The doctor labels Legacy and Enhanced differently so you can see which one is selected before pressing repair or ToT restore actions.",
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        System.Windows.Controls.Grid.SetRow(header, 0);
        root.Children.Add(header);

        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = candidates,
            DisplayMemberPath = nameof(InstallCandidate.DisplayName),
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Controls.Grid.SetRow(list, 1);
        root.Children.Add(list);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 96,
            Height = 32,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var chooseButton = new System.Windows.Controls.Button
        {
            Content = "Use This Install",
            Width = 150,
            Height = 32
        };

        InstallCandidate? selected = null;
        chooseButton.Click += (_, _) =>
        {
            selected = list.SelectedItem as InstallCandidate;
            dialog.DialogResult = selected is not null;
        };
        cancelButton.Click += (_, _) => dialog.DialogResult = false;

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(chooseButton);
        System.Windows.Controls.Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;
        dialog.ShowDialog();
        return selected;
    }

    private void RefreshActions()
    {
        _actions.Clear();
        foreach (var action in _doctor.GetActions())
        {
            _actions.Add(action);
        }

        ActionDetailsText.Text = _actions.Count == 0
            ? "No recorded actions yet."
            : "Select an action above to see exactly what the doctor did.";
        AddActivity($"Loaded {_actions.Count} action record(s).");
    }

    private void RefreshTotCustomSources()
    {
        _totCustomSources.Clear();
        foreach (var source in _doctor.GetTotCustomSources())
        {
            _totCustomSources.Add(source);
        }

        TotCustomSourceDetailsText.Text = _totCustomSources.Count == 0
            ? "No Doctor TotCustom backups or detected live Enhanced TotCustom folders are available yet."
            : "Select a dated TotCustom source to see where it came from.";
        AddActivity($"Loaded {_totCustomSources.Count} TotCustom source(s).");
    }

    private void RefreshAssistantAssessment()
    {
        try
        {
            var installs = _doctor.GetInstallCandidates();
            var state = _doctor.GetSteamRediscoveryState();
            var switchOptions = BuildBranchSwitchOptions(state);
            var lines = new List<string>
            {
                installs.Count switch
                {
                    0 => "No Conan install folder was detected automatically.",
                    1 => $"Detected one Conan install candidate: {installs[0].FolderName}.",
                    _ => $"Detected {installs.Count} Conan install folders."
                },
                $"Steam currently sees: {DescribeManagedFolder(state)}",
                $"Parked Enhanced folder: {(state.EnhancedParked.Exists ? "found" : "not found")}. Parked Legacy folder: {(state.LegacyParked.Exists ? "found" : "not found")}.",
                $"Steam branch request: {FormatBranchValue(state.RequestedBetaKey)}. Mounted branch: {FormatBranchValue(state.MountedBetaKey)}."
            };

            if (!state.EnhancedParked.Exists || !state.LegacyParked.Exists)
            {
                lines.Add("Only one branch copy may be available. The branch assistant can still guide you, but Steam may need to download the missing branch.");
            }

            AssistantAssessmentText.Text = string.Join(Environment.NewLine, lines);
            AssistantRecommendationText.Text = BuildAssistantRecommendation(switchOptions);
            SwitchToEnhancedButton.Visibility = switchOptions.Any(option => option.Target == "Enhanced")
                ? Visibility.Visible
                : Visibility.Collapsed;
            SwitchToLegacyButton.Visibility = switchOptions.Any(option => option.Target == "Legacy")
                ? Visibility.Visible
                : Visibility.Collapsed;
            AddActivity("Updated assistant assessment.");
        }
        catch (Exception ex)
        {
            AssistantAssessmentText.Text = $"The doctor could not complete automatic Steam assessment: {ex.Message}";
            AssistantRecommendationText.Text = "The branch assistant needs a valid Conan Steam library before it can recommend a switch.";
            SwitchToEnhancedButton.Visibility = Visibility.Collapsed;
            SwitchToLegacyButton.Visibility = Visibility.Collapsed;
            AddActivity($"Assistant assessment unavailable: {ex.Message}");
        }
    }

    private void ShowBranchSwitchWizard(string target)
    {
        target = NormalizeWizardTarget(target);
        var state = _doctor.GetSteamRediscoveryState();
        var stage = BranchSwitchWizardStage.Starting;
        var manualBranchConfirmed = false;
        var targetFolderRevealed = false;
        var blockedReason = string.Empty;
        var stableUninstallPolls = 0;
        var stableBranchPolls = 0;
        var stableInstallPolls = 0;
        var pollingPausedUntil = DateTimeOffset.MinValue;

        var dialog = new Window
        {
            Title = "Branch Switch Assistant",
            Owner = this,
            Width = 920,
            Height = 700,
            MinWidth = 820,
            MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White
        };

        var root = new System.Windows.Controls.Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var header = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = new System.Windows.Controls.TextBlock { Text = "Branch Switch Assistant", FontSize = 22, FontWeight = FontWeights.SemiBold };
        var targetText = new System.Windows.Controls.TextBlock
        {
            Text = $"Target branch: {target}. The doctor handles the reversible folder moves and watches Steam once per second.",
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray
        };
        header.Children.Add(title);
        header.Children.Add(targetText);
        System.Windows.Controls.Grid.SetRow(header, 0);
        root.Children.Add(header);

        var statusGrid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 14) };
        statusGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var liveCard = CreateWizardStatusCard("Live in Steam");
        var enhancedCard = CreateWizardStatusCard("Enhanced copy");
        var legacyCard = CreateWizardStatusCard("Legacy copy");
        statusGrid.Children.Add(liveCard.Root);
        statusGrid.Children.Add(enhancedCard.Root);
        statusGrid.Children.Add(legacyCard.Root);
        System.Windows.Controls.Grid.SetColumn(enhancedCard.Root, 1);
        System.Windows.Controls.Grid.SetColumn(legacyCard.Root, 2);
        System.Windows.Controls.Grid.SetRow(statusGrid, 1);
        root.Children.Add(statusGrid);

        var body = new System.Windows.Controls.Grid();
        body.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(340) });

        var assistantPanel = new System.Windows.Controls.Grid();
        assistantPanel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        assistantPanel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var manifestStatusText = new System.Windows.Controls.TextBlock
        {
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        assistantPanel.Children.Add(manifestStatusText);

        var instructionText = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Padding = new Thickness(12),
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1)
        };
        System.Windows.Controls.Grid.SetRow(instructionText, 1);
        assistantPanel.Children.Add(instructionText);
        body.Children.Add(assistantPanel);

        var imagePanel = new System.Windows.Controls.StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        var previewCaption = new System.Windows.Controls.TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        var preview = new System.Windows.Controls.Image
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
            MaxHeight = 340
        };
        var previewFrame = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F7F9FB")!,
            BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#D2D9E1")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = preview
        };
        var workshopQueueNote = new System.Windows.Controls.TextBlock
        {
            Text = "Workshop queue note",
            Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#2A6FDB")!,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
            Cursor = System.Windows.Input.Cursors.Help,
            Visibility = Visibility.Collapsed,
            ToolTip = "After the branch verification finishes, Steam may queue an unusually large Workshop download around 70 GB. If Conan itself has already verified correctly, you can stop that Workshop queue and ignore it."
        };
        imagePanel.Children.Add(previewCaption);
        imagePanel.Children.Add(previewFrame);
        imagePanel.Children.Add(workshopQueueNote);
        System.Windows.Controls.Grid.SetColumn(imagePanel, 1);
        body.Children.Add(imagePanel);
        System.Windows.Controls.Grid.SetRow(body, 2);
        root.Children.Add(body);

        var buttons = new System.Windows.Controls.WrapPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var manualBranchButton = CreateWizardButton($"I selected {target} in Steam", "#5D5BCE", 220);
        var closeButton = CreateWizardButton("Close", "#6A7178", 90);
        buttons.Children.Add(manualBranchButton);
        buttons.Children.Add(closeButton);
        System.Windows.Controls.Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        void PausePolling(int seconds)
        {
            pollingPausedUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        void RefreshWizardView()
        {
            manifestStatusText.Text = BuildWizardManifestSummary(state);
            instructionText.Text = BuildWizardInstruction(stage, state, target, targetFolderRevealed, blockedReason);
            UpdateWizardStatusCard(
                liveCard,
                state.Managed.Exists ? DescribeManagedFolder(state) : "No live folder exposed",
                state.Managed.Exists ? "#EAF2FB" : "#F3F5F7",
                state.Managed.Exists ? "#C7D8EE" : "#D2D9E1");
            UpdateWizardStatusCard(
                enhancedCard,
                state.EnhancedParked.Exists ? "Parked safely" : state.Managed.LooksEnhanced ? "Currently live" : "Not detected",
                state.EnhancedParked.Exists || state.Managed.LooksEnhanced ? "#EEF7F0" : "#F3F5F7",
                state.EnhancedParked.Exists || state.Managed.LooksEnhanced ? "#C9DEC8" : "#D2D9E1");
            UpdateWizardStatusCard(
                legacyCard,
                state.LegacyParked.Exists ? "Parked safely" : state.Managed.LooksLegacy ? "Currently live" : "Not detected",
                state.LegacyParked.Exists || state.Managed.LooksLegacy ? "#EEF7F0" : "#F3F5F7",
                state.LegacyParked.Exists || state.Managed.LooksLegacy ? "#C9DEC8" : "#D2D9E1");

            manualBranchButton.Visibility = stage == BranchSwitchWizardStage.AwaitingBranchSelection
                && !RequestedBranchMatchesTarget(state, target)
                ? Visibility.Visible
                : Visibility.Collapsed;

            preview.Source = stage switch
            {
                BranchSwitchWizardStage.AwaitingSteamUninstall => LoadWizardImage("UninstallConanExiles.png"),
                BranchSwitchWizardStage.AwaitingBranchSelection => LoadWizardImage(target == "Enhanced" ? "BranchSwitchToEnhanced.png" : "BranchSwitchToLegacy.png"),
                _ => null
            };
            previewCaption.Text = stage switch
            {
                BranchSwitchWizardStage.AwaitingSteamUninstall => "Steam screen to use now: Uninstall Conan Exiles.",
                BranchSwitchWizardStage.AwaitingBranchSelection => $"Steam screen to use now: right-click Conan Exiles, open Properties, then choose the {target} branch in the branch menu shown here.",
                _ => string.Empty
            };
            previewCaption.Visibility = preview.Source is null ? Visibility.Collapsed : Visibility.Visible;
            previewFrame.Visibility = preview.Source is null ? Visibility.Collapsed : Visibility.Visible;
            workshopQueueNote.Visibility = stage is BranchSwitchWizardStage.AwaitingSteamInstall or BranchSwitchWizardStage.InstallDetected
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        void ContinueAfterBranchSelection()
        {
            try
            {
                if (TargetParkedFolderExists(state, target))
                {
                    var transaction = _doctor.ExposeSteamRediscoveryTarget(target);
                    RefreshActions();
                    state = _doctor.GetSteamRediscoveryState();
                    targetFolderRevealed = true;
                    AddActivity($"Branch assistant exposed the parked {target} folder in reversible action {transaction.Id}.");
                }

                stage = BranchSwitchWizardStage.AwaitingSteamInstall;
                stableInstallPolls = 0;
                PausePolling(2);
            }
            catch (Exception ex)
            {
                stage = BranchSwitchWizardStage.Blocked;
                blockedReason = ex.Message;
            }

            RefreshWizardView();
        }

        void PollWizard()
        {
            if (DateTimeOffset.UtcNow < pollingPausedUntil)
            {
                return;
            }

            try
            {
                state = _doctor.GetSteamRediscoveryState();
                switch (stage)
                {
                    case BranchSwitchWizardStage.AwaitingSteamUninstall:
                        stableUninstallPolls = IsSteamUninstalledForWizard(state)
                            ? stableUninstallPolls + 1
                            : 0;

                        if (stableUninstallPolls >= 2)
                        {
                            stage = BranchSwitchWizardStage.AwaitingBranchSelection;
                            stableUninstallPolls = 0;
                            AddActivity("Branch assistant detected that Steam no longer reports Conan as installed.");
                            PausePolling(1);
                        }
                        break;

                    case BranchSwitchWizardStage.AwaitingBranchSelection:
                        stableBranchPolls = RequestedBranchMatchesTarget(state, target) || manualBranchConfirmed
                            ? stableBranchPolls + 1
                            : 0;

                        if (stableBranchPolls >= (manualBranchConfirmed ? 1 : 2))
                        {
                            stableBranchPolls = 0;
                            manualBranchConfirmed = false;
                            AddActivity($"Branch assistant accepted the {target} branch selection step.");
                            ContinueAfterBranchSelection();
                            return;
                        }
                        break;

                    case BranchSwitchWizardStage.AwaitingSteamInstall:
                        if (state.ManifestExists && !ManifestLooksTargeted(state, target))
                        {
                            stage = BranchSwitchWizardStage.Blocked;
                            blockedReason = $"Steam resumed Conan on '{FormatBranchValue(state.RequestedBetaKey)}' instead of the intended {target} branch. Reopen the assistant after correcting the Steam branch choice.";
                            break;
                        }

                        stableInstallPolls = state.ManifestExists
                            ? stableInstallPolls + 1
                            : 0;

                        if (stableInstallPolls >= 2)
                        {
                            stage = BranchSwitchWizardStage.InstallDetected;
                            stableInstallPolls = 0;
                            AddActivity("Branch assistant detected Steam's install or rediscovery handoff.");
                        }
                        break;

                    case BranchSwitchWizardStage.InstallDetected:
                        if (state.ManifestExists && !ManifestLooksTargeted(state, target))
                        {
                            stage = BranchSwitchWizardStage.Blocked;
                            blockedReason = $"Steam now shows a different branch than {target}. Correct the branch in Steam before continuing.";
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                stage = BranchSwitchWizardStage.Blocked;
                blockedReason = ex.Message;
            }

            RefreshWizardView();
        }

        void StartAssistant()
        {
            try
            {
                state = _doctor.GetSteamRediscoveryState();
                if (TargetAlreadyManaged(state, target))
                {
                    stage = BranchSwitchWizardStage.Completed;
                    RefreshWizardView();
                    return;
                }

                if (state.Managed.Exists)
                {
                    var transaction = _doctor.PrepareSteamRediscovery(target);
                    RefreshActions();
                    state = _doctor.GetSteamRediscoveryState();
                    stage = BranchSwitchWizardStage.AwaitingSteamUninstall;
                    AddActivity($"Branch assistant parked the live Conan folder in reversible action {transaction.Id}.");
                    PausePolling(2);
                }
                else
                {
                    stage = BranchSwitchWizardStage.AwaitingBranchSelection;
                }
            }
            catch (Exception ex)
            {
                stage = BranchSwitchWizardStage.Blocked;
                blockedReason = ex.Message;
            }

            RefreshWizardView();
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => PollWizard();

        manualBranchButton.Click += (_, _) =>
        {
            manualBranchConfirmed = true;
            stableBranchPolls = 0;
            PollWizard();
        };

        closeButton.Click += (_, _) => dialog.Close();
        dialog.Loaded += (_, _) =>
        {
            StartAssistant();
            timer.Start();
        };
        dialog.Closed += (_, _) =>
        {
            timer.Stop();
            RefreshAssistantAssessment();
        };

        dialog.Content = root;
        dialog.ShowDialog();
    }

    private static IReadOnlyList<BranchSwitchOption> BuildBranchSwitchOptions(SteamRediscoveryState state)
    {
        var options = new List<BranchSwitchOption>();
        if (state.Managed.LooksEnhanced)
        {
            options.Add(new BranchSwitchOption(
                "Legacy",
                100,
                state.LegacyParked.Exists
                    ? "Legacy is already parked safely. The assistant can preserve Enhanced, move Steam onto the Legacy copy, and avoid a full redownload."
                    : "Enhanced is live and no parked Legacy copy was found. The assistant can preserve Enhanced and guide Steam through a clean Legacy install."));
        }
        else if (state.Managed.LooksLegacy)
        {
            options.Add(new BranchSwitchOption(
                "Enhanced",
                100,
                state.EnhancedParked.Exists
                    ? "Enhanced is already parked safely. The assistant can preserve Legacy, move Steam onto the Enhanced copy, and avoid a full redownload."
                    : "Legacy is live and no parked Enhanced copy was found. The assistant can preserve Legacy and guide Steam through a clean Enhanced install."));
        }
        else if (!state.Managed.Exists)
        {
            if (state.LegacyParked.Exists)
            {
                options.Add(new BranchSwitchOption(
                    "Legacy",
                    90,
                    "Steam has no live Conan folder right now, but a Legacy copy is parked safely. The assistant can reveal it after Steam is pointed at Legacy."));
            }

            if (state.EnhancedParked.Exists)
            {
                options.Add(new BranchSwitchOption(
                    "Enhanced",
                    90,
                    "Steam has no live Conan folder right now, but an Enhanced copy is parked safely. The assistant can reveal it after Steam is pointed at Enhanced."));
            }
        }
        else
        {
            if (state.LegacyParked.Exists)
            {
                options.Add(new BranchSwitchOption(
                    "Legacy",
                    70,
                    "A parked Legacy copy exists, but the currently live Conan folder is not fully classified. The assistant can still guide the swap cautiously."));
            }

            if (state.EnhancedParked.Exists)
            {
                options.Add(new BranchSwitchOption(
                    "Enhanced",
                    70,
                    "A parked Enhanced copy exists, but the currently live Conan folder is not fully classified. The assistant can still guide the swap cautiously."));
            }
        }

        return options
            .OrderByDescending(option => option.Priority)
            .ThenBy(option => option.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildAssistantRecommendation(IReadOnlyList<BranchSwitchOption> options)
    {
        return options.Count switch
        {
            0 => "No branch swap stands out from the current scan. Use Troubleshooting if the active Conan install is the one that needs repair.",
            1 => options[0].Recommendation,
            _ => $"{options[0].Recommendation} A second branch path is also available below if that is the one you meant to reach."
        };
    }

    private static string BuildWizardManifestSummary(SteamRediscoveryState state) =>
        $"Steam manifest: {(state.ManifestExists ? "present" : "not present")}. " +
        $"Requested branch: {FormatBranchValue(state.RequestedBetaKey)}. " +
        $"Mounted branch: {FormatBranchValue(state.MountedBetaKey)}.";

    private static string BuildWizardInstruction(
        BranchSwitchWizardStage stage,
        SteamRediscoveryState state,
        string target,
        bool targetFolderRevealed,
        string blockedReason)
    {
        return stage switch
        {
            BranchSwitchWizardStage.Starting =>
                "Preparing the branch assistant.",

            BranchSwitchWizardStage.AwaitingSteamUninstall =>
                "The doctor moved the currently live Conan folder aside in a reversible action. In Steam, press Uninstall for Conan Exiles and wait for Steam to finish. The assistant checks once per second and continues automatically after Steam no longer reports Conan as installed.",

            BranchSwitchWizardStage.AwaitingBranchSelection =>
                RequestedBranchMatchesTarget(state, target)
                    ? $"Steam now reports the {target} branch choice. The assistant is confirming that state before moving to the next step."
                    : $"Choose {target} in Steam while Conan is uninstalled. Right-click Conan Exiles in Steam, open Properties, then use the branch menu shown in the image. The assistant watches the manifest once per second. If Steam does not expose that choice while uninstalled, press the confirmation button below after you make the selection.",

            BranchSwitchWizardStage.AwaitingSteamInstall =>
                targetFolderRevealed
                    ? $"The parked {target} folder is live again at Steam's Conan path. Press Install in Steam and keep the same library. Steam should discover or verify the existing files instead of downloading the full game."
                    : $"No parked {target} folder was available. Press Install in Steam to install {target} normally.",

            BranchSwitchWizardStage.InstallDetected =>
                $"Steam picked up the {target} install step. {BuildSteamProgressText(state)} Let Steam finish completely before launching Conan. Hover the Workshop queue note if Steam starts an odd large Workshop transfer after verification.",

            BranchSwitchWizardStage.Completed =>
                $"{target} already appears to be the live Steam-managed Conan copy. No branch swap is needed right now.",

            BranchSwitchWizardStage.Blocked =>
                $"The assistant stopped because it could not make a safe next move.{Environment.NewLine}{Environment.NewLine}{blockedReason}",

            _ => "The assistant is waiting for Steam."
        };
    }

    private static string BuildSteamProgressText(SteamRediscoveryState state)
    {
        if (state.BytesToDownload is > 0 && state.BytesDownloaded is >= 0)
        {
            var total = state.BytesToDownload.Value;
            var current = Math.Min(state.BytesDownloaded.Value, total);
            var percentage = Math.Round((double)current / total * 100, 1);
            return $"Steam reports {percentage:0.0}% of the current rediscovery or download work processed.";
        }

        return "Steam has the branch handoff in progress.";
    }

    private static System.Windows.Controls.Button CreateWizardButton(string text, string backgroundHex, double width)
    {
        return new System.Windows.Controls.Button
        {
            Content = text,
            Width = width,
            Height = 34,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = System.Windows.Media.Brushes.White,
            Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(backgroundHex)!
        };
    }

    private static WizardStatusCard CreateWizardStatusCard(string header)
    {
        var title = new System.Windows.Controls.TextBlock
        {
            Text = header,
            Foreground = System.Windows.Media.Brushes.DimGray,
            FontSize = 12
        };
        var detail = new System.Windows.Controls.TextBlock
        {
            Text = "Checking...",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(detail);

        return new WizardStatusCard(
            new System.Windows.Controls.Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 10, 0),
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F3F5F7")!,
                BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#D2D9E1")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = stack
            },
            title,
            detail);
    }

    private static void UpdateWizardStatusCard(WizardStatusCard card, string detail, string backgroundHex, string borderHex)
    {
        card.Detail.Text = detail;
        card.Root.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(backgroundHex)!;
        card.Root.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(borderHex)!;
    }

    private static System.Windows.Media.ImageSource LoadWizardImage(string name) =>
        new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute));

    private static bool IsSteamUninstalledForWizard(SteamRediscoveryState state) =>
        !state.ManifestExists && !state.Managed.Exists;

    private static bool TargetAlreadyManaged(SteamRediscoveryState state, string target)
    {
        var folderLooksRight = target == "Enhanced"
            ? state.TargetEnhancedAlreadyManaged
            : state.TargetLegacyAlreadyManaged;

        return folderLooksRight && (!state.ManifestExists || ManifestLooksTargeted(state, target));
    }

    private static bool TargetParkedFolderExists(SteamRediscoveryState state, string target) =>
        target == "Enhanced" ? state.EnhancedParked.Exists : state.LegacyParked.Exists;

    private static bool RequestedBranchMatchesTarget(SteamRediscoveryState state, string target) =>
        state.ManifestExists && BranchKeyMatchesTarget(state.RequestedBetaKey, target);

    private static bool ManifestLooksTargeted(SteamRediscoveryState state, string target) =>
        state.ManifestExists
        && (BranchKeyMatchesTarget(state.RequestedBetaKey, target)
            || BranchKeyMatchesTarget(state.MountedBetaKey, target));

    private static bool BranchKeyMatchesTarget(string? branchKey, string target)
    {
        return target == "Enhanced"
            ? string.IsNullOrWhiteSpace(branchKey) || string.Equals(branchKey, "public", StringComparison.OrdinalIgnoreCase)
            : string.Equals(branchKey, "conan-exiles-legacy", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWizardTarget(string target) =>
        target.Equals("Enhanced", StringComparison.OrdinalIgnoreCase)
            ? "Enhanced"
            : target.Equals("Legacy", StringComparison.OrdinalIgnoreCase)
                ? "Legacy"
                : throw new InvalidOperationException("Choose Enhanced or Legacy as the branch-switch target.");

    private static string DescribeManagedFolder(SteamRediscoveryState state) =>
        state.Managed.LooksEnhanced ? "Enhanced-shaped folder is exposed to Steam"
        : state.Managed.LooksLegacy ? "Legacy-shaped folder is exposed to Steam"
        : state.Managed.Exists ? "an unclassified Conan folder is exposed to Steam"
        : "no Conan folder is exposed to Steam";

    private static string FormatBranchValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<default/public or none>" : value;

    private void ApplyInstallRole(SteamBranchInfo branch)
    {
        BranchRoleText.Text = branch.BranchMode switch
        {
            "Legacy" => "Selected install: Legacy Steam branch.",
            "LegacySideBySideCopy" => "Selected install: likely side-by-side Legacy copy.",
            "EnhancedOrDefault" => "Selected install: default/Enhanced branch.",
            _ => "Selected install role is not fully confirmed."
        };
    }

    private void AddActivity(string message)
    {
        var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss");
        ActivityText.AppendText($"[{timestamp}] {message}{Environment.NewLine}");
        ActivityText.ScrollToEnd();
    }

    private static string GetFriendlyBranchLabel(string branchMode) =>
        branchMode switch
        {
            "Legacy" => "Steam Legacy branch",
            "EnhancedOrDefault" => "Steam default / Enhanced branch",
            "LegacySideBySideCopy" => "Likely side-by-side Legacy copy",
            "DetachedSideBySideCopy" => "Detached side-by-side copy",
            _ => branchMode
        };
}
