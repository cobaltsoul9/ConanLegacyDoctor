using System.Collections.ObjectModel;
using System.Windows;
using ConanLegacyDoctor.Core;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ConanLegacyDoctor.App;

public partial class MainWindow : Window
{
    private readonly LegacyDoctorService _doctor = new();
    private readonly ObservableCollection<Finding> _findings = [];
    private readonly ObservableCollection<DoctorAction> _actions = [];

    public MainWindow()
    {
        InitializeComponent();
        FindingsGrid.ItemsSource = _findings;
        ActionsGrid.ItemsSource = _actions;
        Loaded += (_, _) =>
        {
            AddActivity("Ready. If both Enhanced and Legacy are installed, the doctor will ask which install to inspect before it scans or changes anything.");
            AddActivity("Use Prepare Legacy first. Vanilla launch stays blocked while an active modlist.txt is still present.");
            RefreshActions();
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

            AddActivity($"Prepared reversible Legacy action {transaction.Id}.");
            RefreshActions();
            RunScan();
            System.Windows.MessageBox.Show($"Legacy preparation action created:{Environment.NewLine}{transaction.Id}", "Preparation complete", MessageBoxButton.OK, MessageBoxImage.Information);
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
            GameRootText.Text = scan.GameRoot;
            AddActivity($"Scan complete for {scan.GameRoot}. {scan.Findings.Count} finding(s).");
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
            Text = "This is normal for side-by-side Enhanced and Legacy setups. Choose the install you want the doctor to inspect or repair.",
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
