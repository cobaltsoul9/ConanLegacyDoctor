using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ConanLegacyDoctor.Core;

public sealed class LegacyDoctorService
{
    public const int SchemaVersion = 1;
    public const string ToolName = "ConanLegacyDoctor";
    private const int TotCustomRollingBackupCount = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    static LegacyDoctorService()
    {
        JsonOptions.Converters.Add(new OperationDataConverter());
    }

    private readonly string _stateRoot;

    public LegacyDoctorService(string? stateRoot = null)
    {
        _stateRoot = string.IsNullOrWhiteSpace(stateRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ToolName)
            : Path.GetFullPath(stateRoot);
    }

    public string StateRoot => _stateRoot;

    public IReadOnlyList<InstallCandidate> GetInstallCandidates()
    {
        var candidates = new List<InstallCandidate>();
        foreach (var path in GetConanInstallCandidates())
        {
            var branch = GetSteamManifestInfo(path);
            var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var label = branch.BranchMode switch
            {
                "LegacySideBySideCopy" => $"{folderName} - likely Conay side-by-side Legacy copy",
                "Legacy" => $"{folderName} - Steam Legacy branch",
                "EnhancedOrDefault" => $"{folderName} - Steam default/Enhanced branch",
                _ => $"{folderName} - branch not confirmed"
            };

            candidates.Add(new InstallCandidate(
                $"{label} | {path}",
                path,
                folderName,
                branch.BranchMode,
                branch.Confidence,
                branch.Message));
        }

        return candidates;
    }

    public ScanResult Scan(string? gameRoot)
    {
        var resolvedGameRoot = ResolveConanGameRoot(gameRoot);
        var findings = new List<Finding>();
        var branch = GetSteamManifestInfo(resolvedGameRoot);

        findings.Add(new Finding(
            "steam.manifest",
            "info",
            branch.Detected
                ? (string.IsNullOrWhiteSpace(branch.BetaKey)
                    ? "Steam manifest found; no beta key was detected."
                    : $"Steam manifest beta key detected: {branch.BetaKey}")
                : branch.Message,
            branch.ManifestPath,
            new Dictionary<string, object?>
            {
                ["BranchMode"] = branch.BranchMode,
                ["Confidence"] = branch.Confidence,
                ["BetaKey"] = branch.BetaKey,
                ["InstallDir"] = branch.InstallDir,
                ["InstallDirMatchesGameRoot"] = branch.InstallDirMatchesGameRoot,
                ["BuildId"] = branch.BuildId
            }));

        if (branch.Detected)
        {
            findings.Add(new Finding(
                "steam.branch",
                "info",
                branch.Message,
                branch.ManifestPath,
                new Dictionary<string, object?>
                {
                    ["BranchMode"] = branch.BranchMode,
                    ["Confidence"] = branch.Confidence
                }));
        }

        findings.AddRange(GetModFindings(resolvedGameRoot));
        AddEngineIniFinding(resolvedGameRoot, findings);
        AddModControllerFinding(resolvedGameRoot, findings);
        AddTotCustomFinding(resolvedGameRoot, findings);
        AddSaveDatabaseFindings(resolvedGameRoot, findings);
        findings.AddRange(GetLogSignalFindings(resolvedGameRoot));

        return new ScanResult(
            SchemaVersion,
            ToolName,
            resolvedGameRoot,
            DateTimeOffset.UtcNow,
            branch,
            findings);
    }

    public DoctorTransaction PrepareLegacy(string? gameRoot, PreparationOptions options)
    {
        var resolvedGameRoot = ResolveConanGameRoot(gameRoot);
        var transaction = NewTransaction(resolvedGameRoot, "prepare-legacy");
        var quarantineFolder = Path.Combine(GetTransactionFolder(transaction.Id), "quarantine");
        Directory.CreateDirectory(quarantineFolder);

        BackupTotCustomIfPresent(transaction);

        var modListPath = Path.Combine(resolvedGameRoot, "ConanSandbox", "Mods", "modlist.txt");
        if (File.Exists(modListPath))
        {
            MovePathTransactionally(
                transaction,
                modListPath,
                Path.Combine(quarantineFolder, "modlist.txt"),
                "Temporarily remove active mod list for Legacy startup triage.");
        }

        var engineIniPath = Path.Combine(resolvedGameRoot, "ConanSandbox", "Saved", "Config", "WindowsNoEditor", "Engine.ini");
        if (File.Exists(engineIniPath))
        {
            RewriteEngineIniTransactionally(transaction, engineIniPath);
        }

        var modControllerCachePath = Path.Combine(resolvedGameRoot, "ConanSandbox", "Saved", "ModControllerCache.json");
        if (File.Exists(modControllerCachePath))
        {
            MovePathTransactionally(
                transaction,
                modControllerCachePath,
                Path.Combine(quarantineFolder, "ModControllerCache.json"),
                "Temporarily move ModControllerCache.json aside before Legacy triage.");
        }

        if (options.QuarantineSaveDatabases)
        {
            QuarantineSaveDatabases(transaction, quarantineFolder);
        }

        if (options.QuarantineModsDirectory)
        {
            var modsPath = Path.Combine(resolvedGameRoot, "ConanSandbox", "Mods");
            if (Directory.Exists(modsPath))
            {
                MovePathTransactionally(
                    transaction,
                    modsPath,
                    Path.Combine(quarantineFolder, "Mods"),
                    "Temporarily isolate the Mods directory for a clean-room Legacy startup test.");

                CreateDirectoryTransactionally(
                    transaction,
                    modsPath,
                    "Create an empty placeholder Mods directory after quarantine.");
            }
        }

        if (options.ResetClientConfig)
        {
            var configPath = Path.Combine(resolvedGameRoot, "ConanSandbox", "Saved", "Config", "WindowsNoEditor");
            if (Directory.Exists(configPath))
            {
                MovePathTransactionally(
                    transaction,
                    configPath,
                    Path.Combine(quarantineFolder, "WindowsNoEditor"),
                    "Temporarily isolate WindowsNoEditor client config for a clean-room Legacy startup test.");

                CreateDirectoryTransactionally(
                    transaction,
                    configPath,
                    "Create an empty placeholder client config directory after quarantine.");
            }
        }

        transaction.Status = "completed";
        transaction.CompletedAtUtc = DateTimeOffset.UtcNow;
        SaveTransaction(transaction);
        return transaction;
    }

    public DoctorTransaction Restore(string transactionId, bool force)
    {
        var transaction = LoadTransaction(transactionId);
        if (transaction.Status.Equals("restored", StringComparison.OrdinalIgnoreCase))
        {
            return transaction;
        }

        foreach (var operation in transaction.Operations.AsEnumerable().Reverse())
        {
            if (!operation.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (operation.Type)
            {
                case "CreateDirectory":
                    AssertInsideRoot(operation.Data["Path"]!, transaction.GameRoot);
                    RemoveDoctorDirectoryIfEmpty(transaction, operation.Data["Path"]!);
                    break;

                case "RewriteTextFile":
                    RestoreTextFile(transaction, operation, force);
                    break;

                case "CopyPath":
                    AssertInsideTransaction(transaction, operation.Data["DestinationPath"]!);
                    DeletePathIfPresent(operation.Data["DestinationPath"]!);
                    break;

                case "InstallTotCustomFromArchive":
                case "InstallTotCustomFromFolder":
                    AssertInsideRoot(operation.Data["DestinationPath"]!, transaction.GameRoot);
                    DeletePathIfPresent(operation.Data["DestinationPath"]!);
                    break;

                case "MovePath":
                    RestoreMovedPath(transaction, operation);
                    break;
            }
        }

        transaction.Status = "restored";
        transaction.RestoredAtUtc = DateTimeOffset.UtcNow;
        SaveTransaction(transaction);
        return transaction;
    }

    public IReadOnlyList<DoctorTransaction> GetTransactions()
    {
        var transactionsRoot = Path.Combine(_stateRoot, "transactions");
        if (!Directory.Exists(transactionsRoot))
        {
            return [];
        }

        var transactions = new List<DoctorTransaction>();
        foreach (var folder in Directory.EnumerateDirectories(transactionsRoot).OrderByDescending(Path.GetFileName))
        {
            var transactionPath = Path.Combine(folder, "transaction.json");
            if (!File.Exists(transactionPath))
            {
                continue;
            }

            try
            {
                transactions.Add(ReadJson<DoctorTransaction>(transactionPath));
            }
            catch (JsonException)
            {
                // A malformed legacy ledger should not stop the desktop app from opening.
            }
            catch (InvalidOperationException)
            {
                // Keep the action view available even if one saved record is unreadable.
            }
        }

        return transactions;
    }

    public IReadOnlyList<DoctorAction> GetActions()
    {
        return GetTransactions()
            .Select(transaction =>
            {
                var details = transaction.Operations
                    .Where(operation => operation.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                    .Select(GetOperationActionText)
                    .ToList();

                var summary = details.Count == 0
                    ? "No completed file actions were recorded."
                    : details[0].TrimEnd('.');

                if (details.Count > 1)
                {
                    summary = $"{summary} (+{details.Count - 1} more)";
                }

                return new DoctorAction(
                    transaction.Id,
                    transaction.Status,
                    transaction.Action,
                    transaction.CreatedAtUtc,
                    transaction.GameRoot,
                    summary,
                    details,
                    transaction.Warnings);
            })
            .ToList();
    }

    public IReadOnlyList<TotCustomSource> GetTotCustomSources()
    {
        var sources = new List<TotCustomSource>();
        var backupsRoot = Path.Combine(_stateRoot, "backups", "TotCustom");
        if (Directory.Exists(backupsRoot))
        {
            foreach (var archivePath in Directory.EnumerateFiles(backupsRoot, "TotCustom_*.zip", SearchOption.AllDirectories)
                         .Where(path => !path.EndsWith(".tmp.zip", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var archive = new FileInfo(archivePath);
                var archiveBaseName = Path.GetFileNameWithoutExtension(archive.Name);
                var slotName = archiveBaseName.Equals("TotCustom_First", StringComparison.OrdinalIgnoreCase)
                    ? "Preserved first backup"
                    : archiveBaseName.Replace('_', ' ');
                var sourceLabel = Path.GetFileName(Path.GetDirectoryName(archivePath)!);
                sources.Add(new TotCustomSource(
                    $"archive:{archive.FullName}",
                    $"{slotName} - {archive.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}",
                    "Doctor Backup",
                    archive.FullName,
                    null,
                    archive.LastWriteTimeUtc,
                    archive.Length,
                    archiveBaseName.Equals("TotCustom_First", StringComparison.OrdinalIgnoreCase)
                        ? $"Oldest preserved Doctor backup from {sourceLabel}."
                        : $"Recent Doctor backup from {sourceLabel}."));
            }
        }

        foreach (var candidate in GetInstallCandidates()
                     .Where(candidate => candidate.Branch.Equals("EnhancedOrDefault", StringComparison.OrdinalIgnoreCase)))
        {
            var liveFolder = Path.Combine(candidate.Path, "ConanSandbox", "Saved", "SaveGames", "TotCustom");
            if (!Directory.Exists(liveFolder))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(liveFolder, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToList();
            var latestWrite = files.Count == 0
                ? Directory.GetLastWriteTimeUtc(liveFolder)
                : files.Max(file => file.LastWriteTimeUtc);
            var totalBytes = files.Sum(file => file.Length);

            sources.Add(new TotCustomSource(
                $"enhanced-live:{candidate.Path}",
                $"Enhanced TotCustom source - {latestWrite.ToLocalTime():yyyy-MM-dd HH:mm}",
                "Enhanced Source",
                liveFolder,
                candidate.Path,
                latestWrite,
                totalBytes,
                $"Read-only source pulled from detected Enhanced install '{candidate.FolderName}'."));
        }

        return sources
            .OrderByDescending(source => source.CapturedAtUtc)
            .ThenBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public DoctorTransaction RestoreTotCustomSource(string? gameRoot, string sourceId)
    {
        var resolvedGameRoot = ResolveConanGameRoot(gameRoot);
        var source = GetTotCustomSources()
            .SingleOrDefault(candidate => candidate.Id.Equals(sourceId, StringComparison.Ordinal));
        if (source is null)
        {
            throw new InvalidOperationException("Choose a TotCustom backup or Enhanced source that is still available.");
        }

        var transaction = NewTransaction(resolvedGameRoot, "restore-totcustom");
        var targetPath = Path.Combine(resolvedGameRoot, "ConanSandbox", "Saved", "SaveGames", "TotCustom");
        var quarantineFolder = Path.Combine(GetTransactionFolder(transaction.Id), "quarantine", "TotCustomRestore");
        Directory.CreateDirectory(quarantineFolder);

        if (Directory.Exists(targetPath))
        {
            MovePathTransactionally(
                transaction,
                targetPath,
                Path.Combine(quarantineFolder, "PreviousTotCustom"),
                "Move the current TotCustom folder aside before restoring a selected source.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        switch (source.SourceKind)
        {
            case "Doctor Backup":
                InstallTotCustomFromArchive(transaction, source, targetPath);
                break;

            case "Enhanced Source":
                InstallTotCustomFromFolder(transaction, source, targetPath);
                break;

            default:
                throw new InvalidOperationException($"Unsupported TotCustom source kind: {source.SourceKind}");
        }

        transaction.Status = "completed";
        transaction.CompletedAtUtc = DateTimeOffset.UtcNow;
        SaveTransaction(transaction);
        return transaction;
    }

    public VanillaLaunchPlan GetVanillaLaunchPlan(string? gameRoot)
    {
        var resolvedGameRoot = ResolveConanGameRoot(gameRoot);
        var branch = GetSteamManifestInfo(resolvedGameRoot);
        var warnings = new List<string>();
        var modListPath = Path.Combine(resolvedGameRoot, "ConanSandbox", "Mods", "modlist.txt");
        var modListPresent = File.Exists(modListPath);

        if (modListPresent)
        {
            warnings.Add($"Active mod list detected at '{modListPath}'. Run Prepare first so the doctor can move it aside reversibly.");
        }

        var executableCandidates = branch.BranchMode == "EnhancedOrDefault"
            ? new[] { "ConanSandbox-Win64-Shipping.exe", "ConanSandbox.exe" }
            : new[] { "ConanSandbox.exe", "ConanSandbox-Win64-Shipping.exe" };

        string? executablePath = null;
        foreach (var executableName in executableCandidates)
        {
            var candidate = Path.Combine(resolvedGameRoot, "ConanSandbox", "Binaries", "Win64", executableName);
            if (File.Exists(candidate))
            {
                executablePath = Path.GetFullPath(candidate);
                break;
            }
        }

        var steamFallbackAllowed = branch.InstallDirMatchesGameRoot == true;
        var launchStrategy = executablePath is not null
            ? "DirectExecutable"
            : steamFallbackAllowed
                ? "SteamUri"
                : "Unavailable";

        if (launchStrategy == "Unavailable")
        {
            warnings.Add("No verified executable was found for the selected install. Because this install is not confirmed as the Steam-managed target, the doctor will not fall back to Steam and risk launching the wrong branch.");
        }

        return new VanillaLaunchPlan(
            SchemaVersion,
            ToolName,
            resolvedGameRoot,
            branch.BranchMode,
            branch.Confidence,
            !modListPresent,
            modListPath,
            launchStrategy,
            executablePath,
            "steam://run/440900/",
            steamFallbackAllowed,
            warnings);
    }

    public VanillaLaunchResult StartVanillaLaunch(string? gameRoot)
    {
        var plan = GetVanillaLaunchPlan(gameRoot);
        if (!plan.VanillaReady)
        {
            throw new InvalidOperationException("Vanilla launch is blocked because modlist.txt is still active. Run Prepare first, then try the vanilla launch again.");
        }

        switch (plan.LaunchStrategy)
        {
            case "DirectExecutable":
                Process.Start(new ProcessStartInfo
                {
                    FileName = plan.ExecutablePath!,
                    WorkingDirectory = Path.GetDirectoryName(plan.ExecutablePath)!,
                    UseShellExecute = true
                });
                break;

            case "SteamUri":
                Process.Start(new ProcessStartInfo
                {
                    FileName = plan.SteamUri,
                    UseShellExecute = true
                });
                break;

            default:
                throw new InvalidOperationException(plan.Warnings.LastOrDefault() ?? "No safe launch route was found for the selected install.");
        }

        return new VanillaLaunchResult(
            SchemaVersion,
            ToolName,
            plan.GameRoot,
            plan.BranchMode,
            plan.LaunchStrategy,
            plan.ExecutablePath,
            plan.SteamUri,
            DateTimeOffset.UtcNow);
    }

    public SteamValidationPlan GetSteamValidationPlan(string? gameRoot)
    {
        var resolvedGameRoot = ResolveConanGameRoot(gameRoot);
        var branch = GetSteamManifestInfo(resolvedGameRoot);
        var warnings = new List<string>();
        var steamManagedTarget = branch.InstallDirMatchesGameRoot == true;

        if (!steamManagedTarget)
        {
            warnings.Add(
                "Steam can only validate the currently Steam-managed Conan install. The selected folder is not confirmed as that target, so the doctor will not launch verification from this selection.");
        }

        return new SteamValidationPlan(
            SchemaVersion,
            ToolName,
            resolvedGameRoot,
            branch.BranchMode,
            branch.Confidence,
            steamManagedTarget,
            "steam://validate/440900",
            warnings);
    }

    public SteamValidationResult StartSteamValidation(string? gameRoot)
    {
        var plan = GetSteamValidationPlan(gameRoot);
        if (!plan.SteamManagedTarget)
        {
            throw new InvalidOperationException(plan.Warnings.Last());
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = plan.SteamUri,
            UseShellExecute = true
        });

        return new SteamValidationResult(
            SchemaVersion,
            ToolName,
            plan.GameRoot,
            plan.BranchMode,
            plan.SteamUri,
            DateTimeOffset.UtcNow);
    }

    public SteamRediscoveryState GetSteamRediscoveryState()
    {
        var steamAppsRoot = ResolveConanSteamAppsRoot();
        var commonRoot = Path.Combine(steamAppsRoot, "common");
        var manifestPath = Path.Combine(steamAppsRoot, "appmanifest_440900.acf");
        var manifest = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : null;

        var managed = GetSteamRediscoveryFolder("Managed Conan", Path.Combine(commonRoot, "Conan Exiles"));
        var enhanced = GetSteamRediscoveryFolder("Parked Enhanced", Path.Combine(commonRoot, "Conan Exiles Enhanced"));
        var legacy = GetSteamRediscoveryFolder("Parked Legacy", Path.Combine(commonRoot, "Conan Exiles Legacy"));
        var workshop = GetSteamRediscoveryWorkshop(steamAppsRoot);

        var requestedBeta = manifest is null ? null : ReadManifestSectionValue(manifest, "UserConfig", "BetaKey");
        var mountedBeta = manifest is null ? null : ReadManifestSectionValue(manifest, "MountedConfig", "BetaKey");
        var guidance = new List<string>();

        if (!managed.Exists && !enhanced.Exists && !legacy.Exists)
        {
            guidance.Add("No Conan branch folder was found in this Steam library. Choose the branch in Steam and install it normally.");
        }
        else if (managed.LooksEnhanced)
        {
            guidance.Add("Steam currently sees an Enhanced-shaped Conan folder at the managed path.");
        }
        else if (managed.LooksLegacy)
        {
            guidance.Add("Steam currently sees a Legacy-shaped Conan folder at the managed path.");
        }
        else if (managed.Exists)
        {
            guidance.Add("Steam currently has a Conan folder at the managed path, but the doctor cannot classify it confidently.");
        }
        else
        {
            guidance.Add("No managed Conan folder is exposed right now. This is the right moment to reveal the branch folder you want Steam to discover.");
        }

        if (manifest is null)
        {
            guidance.Add("Steam does not currently have Conan's app manifest in this library, which usually means Steam considers the game uninstalled.");
        }
        else if (!string.Equals(requestedBeta, mountedBeta, StringComparison.OrdinalIgnoreCase))
        {
            guidance.Add("Steam shows a branch change in progress or pending: the requested and mounted branches do not match.");
        }

        if (!enhanced.Exists)
        {
            guidance.Add("No parked Enhanced folder is available. Switching to Enhanced can still be guided, but Steam may need to download that branch.");
        }

        if (!legacy.Exists)
        {
            guidance.Add("No parked Legacy folder is available. Switching to Legacy can still be guided, but Steam may need to download that branch.");
        }

        if (workshop.ContentExists)
        {
            guidance.Add("Conan Workshop mod content is live in this Steam library. The branch assistant will park it before Steam uninstall so the uninstall step cannot remove it.");
        }
        else if (workshop.ParkedContentExists)
        {
            guidance.Add("Conan Workshop mod content is already parked safely and will be restored before Steam install/verification.");
        }

        return new SteamRediscoveryState(
            SchemaVersion,
            ToolName,
            steamAppsRoot,
            manifestPath,
            manifest is not null,
            requestedBeta,
            mountedBeta,
            manifest is null ? null : ReadManifestValue(manifest, "buildid"),
            manifest is null ? null : ReadManifestValue(manifest, "TargetBuildID"),
            manifest is null ? null : ReadManifestLong(manifest, "BytesToDownload"),
            manifest is null ? null : ReadManifestLong(manifest, "BytesDownloaded"),
            managed,
            enhanced,
            legacy,
            workshop,
            managed.LooksEnhanced,
            managed.LooksLegacy,
            managed.Exists && !legacy.Exists,
            managed.Exists && !enhanced.Exists,
            !managed.Exists && enhanced.Exists,
            !managed.Exists && legacy.Exists,
            guidance);
    }

    public DoctorTransaction PrepareSteamRediscovery(string targetBranch)
    {
        var state = GetSteamRediscoveryState();
        var transaction = NewTransaction(state.SteamAppsRoot, "steam-rediscovery-prepare");
        ValidateCanParkWorkshopForSteamRediscovery(state);

        switch (NormalizeRediscoveryTarget(targetBranch))
        {
            case "Enhanced":
                if (!state.Managed.Exists)
                {
                    throw new InvalidOperationException("No managed Conan folder is exposed. There is nothing to park before uninstall.");
                }

                if (state.LegacyParked.Exists)
                {
                    throw new InvalidOperationException("A parked Legacy folder already exists. Resolve that before parking the managed folder for an Enhanced switch.");
                }

                ParkWorkshopForSteamRediscovery(transaction, state);

                MovePathTransactionally(
                    transaction,
                    state.Managed.Path,
                    state.LegacyParked.Path,
                    "Park the currently managed Conan folder as Legacy before Steam uninstall/rediscovery.");
                break;

            case "Legacy":
                if (!state.Managed.Exists)
                {
                    throw new InvalidOperationException("No managed Conan folder is exposed. There is nothing to park before uninstall.");
                }

                if (state.EnhancedParked.Exists)
                {
                    throw new InvalidOperationException("A parked Enhanced folder already exists. Resolve that before parking the managed folder for a Legacy switch.");
                }

                ParkWorkshopForSteamRediscovery(transaction, state);

                MovePathTransactionally(
                    transaction,
                    state.Managed.Path,
                    state.EnhancedParked.Path,
                    "Park the currently managed Conan folder as Enhanced before Steam uninstall/rediscovery.");
                break;
        }

        transaction.Status = "completed";
        transaction.CompletedAtUtc = DateTimeOffset.UtcNow;
        SaveTransaction(transaction);
        return transaction;
    }

    public DoctorTransaction ExposeSteamRediscoveryTarget(string targetBranch)
    {
        var state = GetSteamRediscoveryState();
        if (state.Managed.Exists)
        {
            throw new InvalidOperationException("Steam already has a managed Conan folder exposed. Uninstall first or resolve that folder before revealing another branch.");
        }

        ValidateCanRestoreWorkshopForSteamRediscovery(state);
        var transaction = NewTransaction(state.SteamAppsRoot, "steam-rediscovery-expose");
        switch (NormalizeRediscoveryTarget(targetBranch))
        {
            case "Enhanced":
                if (!state.EnhancedParked.Exists)
                {
                    throw new InvalidOperationException("No parked Enhanced folder is available. Press Install in Steam to download Enhanced normally.");
                }

                MovePathTransactionally(
                    transaction,
                    state.EnhancedParked.Path,
                    state.Managed.Path,
                    "Expose the parked Enhanced folder at Steam's managed Conan path so Install can discover existing files.");
                break;

            case "Legacy":
                if (!state.LegacyParked.Exists)
                {
                    throw new InvalidOperationException("No parked Legacy folder is available. Press Install in Steam to download Legacy normally.");
                }

                MovePathTransactionally(
                    transaction,
                    state.LegacyParked.Path,
                    state.Managed.Path,
                    "Expose the parked Legacy folder at Steam's managed Conan path so Install can discover existing files.");
                break;
        }

        RestoreWorkshopForSteamRediscovery(transaction, state);

        transaction.Status = "completed";
        transaction.CompletedAtUtc = DateTimeOffset.UtcNow;
        SaveTransaction(transaction);
        return transaction;
    }

    private void ParkWorkshopForSteamRediscovery(DoctorTransaction transaction, SteamRediscoveryState state)
    {
        if (state.Workshop.ContentExists)
        {
            MovePathTransactionally(
                transaction,
                state.Workshop.ContentPath,
                state.Workshop.ParkedContentPath,
                "Park Conan Workshop mod content before Steam uninstall can remove it.");
        }

        if (state.Workshop.ManifestExists)
        {
            MovePathTransactionally(
                transaction,
                state.Workshop.ManifestPath,
                state.Workshop.ParkedManifestPath,
                "Park Conan Workshop metadata before Steam uninstall can remove it.");
        }
    }

    private void RestoreWorkshopForSteamRediscovery(DoctorTransaction transaction, SteamRediscoveryState state)
    {
        if (state.Workshop.ParkedContentExists)
        {
            MovePathTransactionally(
                transaction,
                state.Workshop.ParkedContentPath,
                state.Workshop.ContentPath,
                "Restore parked Conan Workshop mod content before Steam install/verification.");
        }

        if (state.Workshop.ParkedManifestExists)
        {
            MovePathTransactionally(
                transaction,
                state.Workshop.ParkedManifestPath,
                state.Workshop.ManifestPath,
                "Restore parked Conan Workshop metadata before Steam install/verification.");
        }
    }

    private static void ValidateCanParkWorkshopForSteamRediscovery(SteamRediscoveryState state)
    {
        if (state.Workshop.ContentExists && state.Workshop.ParkedContentExists)
        {
            throw new InvalidOperationException("Both live and parked Conan Workshop mod folders already exist. The doctor will not overwrite either copy. Resolve that Workshop folder collision before uninstalling in Steam.");
        }

        if (state.Workshop.ManifestExists && state.Workshop.ParkedManifestExists)
        {
            throw new InvalidOperationException("Both live and parked Conan Workshop metadata files already exist. The doctor will not overwrite either copy. Resolve that Workshop metadata collision before uninstalling in Steam.");
        }
    }

    private static void ValidateCanRestoreWorkshopForSteamRediscovery(SteamRediscoveryState state)
    {
        if (state.Workshop.ParkedContentExists && state.Workshop.ContentExists)
        {
            throw new InvalidOperationException("Both parked and live Conan Workshop mod folders exist. The doctor will not overwrite the live Workshop folder. Resolve that collision before pressing Install in Steam.");
        }

        if (state.Workshop.ParkedManifestExists && state.Workshop.ManifestExists)
        {
            throw new InvalidOperationException("Both parked and live Conan Workshop metadata files exist. The doctor will not overwrite the live Workshop metadata. Resolve that collision before pressing Install in Steam.");
        }
    }

    public SupportBundleResult ExportSupportBundle(string? gameRoot, SupportBundleOptions options)
    {
        var resolvedGameRoot = ResolveConanGameRoot(gameRoot);
        var createdAtUtc = DateTimeOffset.UtcNow;
        var destinationPath = string.IsNullOrWhiteSpace(options.DestinationPath)
            ? Path.Combine(_stateRoot, "support-bundles", $"ConanLegacyDoctor-{createdAtUtc:yyyyMMddTHHmmssZ}.zip")
            : Path.GetFullPath(options.DestinationPath);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var stagingRoot = Path.Combine(_stateRoot, "bundle-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        var stagedFiles = new List<string>();

        try
        {
            var metadata = new
            {
                SchemaVersion,
                Tool = ToolName,
                CreatedAtUtc = createdAtUtc,
                GameRoot = resolvedGameRoot,
                DestinationPath = destinationPath,
                options.IncludeRecentLogs,
                options.IncludeConfigSnapshots,
                Notes = new[]
                {
                    "No save database files are included.",
                    "Recent logs and config snapshots are opt-in because they may contain player-local or server-specific details."
                }
            };

            WriteBundleJson(stagingRoot, "bundle-metadata.json", metadata, stagedFiles);
            WriteBundleJson(stagingRoot, "scan.json", Scan(resolvedGameRoot), stagedFiles);
            WriteBundleJson(stagingRoot, "actions.json", GetActions(), stagedFiles);

            if (options.IncludeRecentLogs)
            {
                AddRecentLogTails(resolvedGameRoot, stagingRoot, stagedFiles);
            }

            if (options.IncludeConfigSnapshots)
            {
                AddConfigSnapshots(resolvedGameRoot, stagingRoot, stagedFiles);
            }

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            ZipFile.CreateFromDirectory(stagingRoot, destinationPath);
            return new SupportBundleResult(
                SchemaVersion,
                ToolName,
                createdAtUtc,
                resolvedGameRoot,
                destinationPath,
                options.IncludeRecentLogs,
                options.IncludeConfigSnapshots,
                stagedFiles.Count);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, true);
            }
        }
    }

    private static IEnumerable<Finding> GetModFindings(string gameRoot)
    {
        var findings = new List<Finding>();
        var modsRoot = Path.Combine(gameRoot, "ConanSandbox", "Mods");
        if (!Directory.Exists(modsRoot))
        {
            return findings;
        }

        var pakCount = Directory.EnumerateFiles(modsRoot, "*.pak", SearchOption.TopDirectoryOnly).Count();
        findings.Add(new Finding(
            "mods.directory",
            "info",
            $"Mods directory exists with {pakCount} direct .pak file(s).",
            modsRoot,
            new Dictionary<string, object?> { ["DirectPakFileCount"] = pakCount }));

        var modListPath = Path.Combine(modsRoot, "modlist.txt");
        if (!File.Exists(modListPath))
        {
            return findings;
        }

        var entries = File.ReadAllLines(modListPath)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.TrimStart('*').Trim())
            .ToList();

        findings.Add(new Finding(
            "mods.modlist",
            "warning",
            $"modlist.txt exists with {entries.Count} active or staged line(s).",
            modListPath,
            new Dictionary<string, object?> { ["NonEmptyLineCount"] = entries.Count }));

        var missingEntries = new List<string>();
        foreach (var entry in entries)
        {
            var candidate = Path.IsPathRooted(entry)
                ? entry
                : Path.Combine(modsRoot, entry);

            if (!File.Exists(candidate))
            {
                missingEntries.Add(entry);
            }
        }

        if (missingEntries.Count > 0)
        {
            findings.Add(new Finding(
                "mods.modlist-missing-targets",
                "warning",
                $"modlist.txt references {missingEntries.Count} file(s) that are not currently present at their recorded paths.",
                modListPath,
                new Dictionary<string, object?>
                {
                    ["MissingCount"] = missingEntries.Count,
                    ["MissingEntries"] = missingEntries
                }));
        }

        return findings;
    }

    private static IEnumerable<Finding> GetLogSignalFindings(string gameRoot)
    {
        var logsRoot = Path.Combine(gameRoot, "ConanSandbox", "Saved", "Logs");
        if (!Directory.Exists(logsRoot))
        {
            return [];
        }

        var latestLog = Directory.EnumerateFiles(logsRoot, "*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latestLog is null)
        {
            return [];
        }

        var lines = File.ReadLines(latestLog.FullName).TakeLast(1200);
        var text = string.Join(Environment.NewLine, lines);
        var signals = new[]
        {
            ("logs.version", "incompatible version|wrong version|buildidoverride", "warning", "Recent log text mentions version mismatch or build override language."),
            ("logs.mods", "modlist|mounting pak|pak file", "warning", "Recent log text mentions mod list or pak mounting activity."),
            ("logs.memory", "out of memory|ran out of memory", "warning", "Recent log text mentions memory exhaustion."),
            ("logs.graphics", "d3d12|driver version", "info", "Recent log text mentions D3D12 or graphics-driver language.")
        };

        return signals
            .Where(signal => Regex.IsMatch(text, signal.Item2, RegexOptions.IgnoreCase))
            .Select(signal => new Finding(
                signal.Item1,
                signal.Item3,
                signal.Item4,
                latestLog.FullName,
                new Dictionary<string, object?> { ["LogFile"] = latestLog.Name }))
            .ToList();
    }

    private static void AddEngineIniFinding(string gameRoot, ICollection<Finding> findings)
    {
        var engineIniPath = Path.Combine(gameRoot, "ConanSandbox", "Saved", "Config", "WindowsNoEditor", "Engine.ini");
        if (!File.Exists(engineIniPath))
        {
            return;
        }

        var content = File.ReadAllText(engineIniPath);
        var matches = Regex.Matches(content, @"(?im)^[ \t]*(bUseBuildIdOverride|BuildIdOverride)\s*=.*$");
        if (matches.Count == 0)
        {
            return;
        }

        findings.Add(new Finding(
            "config.engine-build-override",
            "warning",
            "Engine.ini contains build override lines that can cause branch or server version trouble.",
            engineIniPath,
            new Dictionary<string, object?> { ["MatchCount"] = matches.Count }));
    }

    private static void AddModControllerFinding(string gameRoot, ICollection<Finding> findings)
    {
        var path = Path.Combine(gameRoot, "ConanSandbox", "Saved", "ModControllerCache.json");
        if (File.Exists(path))
        {
            findings.Add(new Finding(
                "saved.mod-controller-cache",
                "info",
                "ModControllerCache.json is present. Prepare can move it aside reversibly during cleanup.",
                path));
        }
    }

    private static void AddTotCustomFinding(string gameRoot, ICollection<Finding> findings)
    {
        var path = Path.Combine(gameRoot, "ConanSandbox", "Saved", "SaveGames", "TotCustom");
        if (!Directory.Exists(path))
        {
            return;
        }

        var count = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        findings.Add(new Finding(
            "saves.totcustom",
            "info",
            "TotCustom save data is present. Prepare will create preserved-first and rolling recent backups before cleanup.",
            path,
            new Dictionary<string, object?> { ["FileCount"] = count }));
    }

    private static void AddSaveDatabaseFindings(string gameRoot, ICollection<Finding> findings)
    {
        var savedRoot = Path.Combine(gameRoot, "ConanSandbox", "Saved");
        foreach (var saveName in new[] { "game.db", "game_0.db", "dlc_siptah.db" })
        {
            var savePath = Path.Combine(savedRoot, saveName);
            if (!File.Exists(savePath))
            {
                continue;
            }

            var info = new FileInfo(savePath);
            findings.Add(new Finding(
                $"saves.{saveName.ToLowerInvariant().Replace('.', '-')}",
                "info",
                $"{saveName} is present. Legacy Doctor leaves it alone unless the player explicitly chooses save-database quarantine.",
                savePath,
                new Dictionary<string, object?>
                {
                    ["LengthBytes"] = info.Length,
                    ["LastWriteTimeUtc"] = info.LastWriteTimeUtc
                }));
        }
    }

    private void QuarantineSaveDatabases(DoctorTransaction transaction, string quarantineFolder)
    {
        var savedRoot = Path.Combine(transaction.GameRoot, "ConanSandbox", "Saved");
        var saveQuarantineFolder = Path.Combine(quarantineFolder, "SaveDatabases");

        foreach (var saveName in new[] { "game.db", "game_0.db", "dlc_siptah.db" })
        {
            var savePath = Path.Combine(savedRoot, saveName);
            if (!File.Exists(savePath))
            {
                continue;
            }

            CopyPathTransactionally(
                transaction,
                savePath,
                Path.Combine(saveQuarantineFolder, $"{saveName}.backup"),
                $"Create a byte-for-byte safety copy of {saveName} before save quarantine.");

            MovePathTransactionally(
                transaction,
                savePath,
                Path.Combine(saveQuarantineFolder, saveName),
                $"Temporarily quarantine {saveName} so Legacy can start without a possibly UE5-converted database.");
        }
    }

    private void BackupTotCustomIfPresent(DoctorTransaction transaction)
    {
        var sourcePath = Path.Combine(transaction.GameRoot, "ConanSandbox", "Saved", "SaveGames", "TotCustom");
        if (!Directory.Exists(sourcePath))
        {
            return;
        }

        var folderName = SanitizePathSegment(Path.GetFileName(transaction.GameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var backupFolder = Path.Combine(_stateRoot, "backups", "TotCustom", $"{folderName}-{GetBackupPathKey(transaction.GameRoot)}");
        Directory.CreateDirectory(backupFolder);

        var oldestPreserved = Path.Combine(backupFolder, "TotCustom_First.zip");
        var rollingSlots = Enumerable.Range(1, TotCustomRollingBackupCount)
            .Select(slot => Path.Combine(backupFolder, $"TotCustom_{slot}.zip"))
            .ToArray();
        var tempZip = Path.Combine(backupFolder, $"TotCustom_{Guid.NewGuid():N}.tmp.zip");

        try
        {
            ZipFile.CreateFromDirectory(sourcePath, tempZip);
            if (!File.Exists(oldestPreserved))
            {
                File.Copy(tempZip, oldestPreserved, true);
            }

            for (var slot = rollingSlots.Length - 1; slot > 0; slot--)
            {
                if (File.Exists(rollingSlots[slot - 1]))
                {
                    File.Move(rollingSlots[slot - 1], rollingSlots[slot], true);
                }
            }

            File.Move(tempZip, rollingSlots[0], true);
            AddCompletedOperation(
                transaction,
                "CreateBackupArchive",
                "Create a preserved first TotCustom backup plus rolling recent TotCustom backups before Legacy cleanup.",
                new Dictionary<string, string?>
                {
                    ["SourcePath"] = sourcePath,
                    ["BackupPath"] = rollingSlots[0],
                    ["FirstBackupPath"] = oldestPreserved,
                    ["RotationSlots"] = string.Join("|", new[] { oldestPreserved }.Concat(rollingSlots))
                });
        }
        catch (Exception ex)
        {
            if (File.Exists(tempZip))
            {
                File.Delete(tempZip);
            }

            AddWarning(transaction, $"TotCustom backup could not be created: {ex.Message}");
        }
    }

    private void InstallTotCustomFromArchive(DoctorTransaction transaction, TotCustomSource source, string targetPath)
    {
        if (!File.Exists(source.SourcePath))
        {
            throw new FileNotFoundException("The selected TotCustom backup archive is no longer available.", source.SourcePath);
        }

        var stagingFolder = Path.Combine(GetTransactionFolder(transaction.Id), "staging", "TotCustomRestore");
        Directory.CreateDirectory(stagingFolder);
        ExtractZipArchiveSafely(source.SourcePath, stagingFolder);

        AssertInsideRoot(targetPath, transaction.GameRoot);
        CopyPath(stagingFolder, targetPath);
        AddCompletedOperation(
            transaction,
            "InstallTotCustomFromArchive",
            "Restore TotCustom from a doctor backup archive.",
            new Dictionary<string, string?>
            {
                ["SourcePath"] = source.SourcePath,
                ["DestinationPath"] = targetPath,
                ["CapturedAtUtc"] = source.CapturedAtUtc.ToString("O")
            });
    }

    private void InstallTotCustomFromFolder(DoctorTransaction transaction, TotCustomSource source, string targetPath)
    {
        if (!Directory.Exists(source.SourcePath))
        {
            throw new DirectoryNotFoundException($"The selected Enhanced TotCustom folder is no longer available: {source.SourcePath}");
        }

        AssertInsideRoot(targetPath, transaction.GameRoot);
        CopyPath(source.SourcePath, targetPath);
        AddCompletedOperation(
            transaction,
            "InstallTotCustomFromFolder",
            "Restore TotCustom from a detected Enhanced install folder.",
            new Dictionary<string, string?>
            {
                ["SourcePath"] = source.SourcePath,
                ["DestinationPath"] = targetPath,
                ["CapturedAtUtc"] = source.CapturedAtUtc.ToString("O")
            });
    }

    private void RewriteEngineIniTransactionally(DoctorTransaction transaction, string engineIniPath)
    {
        AssertInsideRoot(engineIniPath, transaction.GameRoot);
        var raw = File.ReadAllText(engineIniPath);
        var rewritten = Regex.Replace(
            raw,
            @"(?im)^[ \t]*(bUseBuildIdOverride|BuildIdOverride)\s*=.*(?:\r?\n|$)",
            string.Empty);

        if (rewritten == raw)
        {
            return;
        }

        var backupFolder = Path.Combine(GetTransactionFolder(transaction.Id), "backups");
        Directory.CreateDirectory(backupFolder);
        var backupPath = Path.Combine(backupFolder, "Engine.ini.original");
        File.Copy(engineIniPath, backupPath, true);

        var operation = AddPendingOperation(
            transaction,
            "RewriteTextFile",
            "Remove stale Engine.ini build override lines.",
            new Dictionary<string, string?>
            {
                ["Path"] = engineIniPath,
                ["BackupPath"] = backupPath,
                ["OriginalHash"] = ComputeFileHash(engineIniPath),
                ["ResultHash"] = null
            });

        File.WriteAllText(engineIniPath, rewritten, new UTF8Encoding(false));
        operation.Data["ResultHash"] = ComputeFileHash(engineIniPath);
        CompleteOperation(transaction, operation);
    }

    private void MovePathTransactionally(DoctorTransaction transaction, string sourcePath, string destinationPath, string reason)
    {
        AssertInsideRoot(sourcePath, transaction.GameRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var operation = AddPendingOperation(
            transaction,
            "MovePath",
            reason,
            new Dictionary<string, string?>
            {
                ["SourcePath"] = sourcePath,
                ["DestinationPath"] = destinationPath
            });

        MovePath(sourcePath, destinationPath);
        CompleteOperation(transaction, operation);
    }

    private void CopyPathTransactionally(DoctorTransaction transaction, string sourcePath, string destinationPath, string reason)
    {
        AssertInsideRoot(sourcePath, transaction.GameRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var operation = AddPendingOperation(
            transaction,
            "CopyPath",
            reason,
            new Dictionary<string, string?>
            {
                ["SourcePath"] = sourcePath,
                ["DestinationPath"] = destinationPath
            });

        CopyPath(sourcePath, destinationPath);
        CompleteOperation(transaction, operation);
    }

    private void CreateDirectoryTransactionally(DoctorTransaction transaction, string path, string reason)
    {
        AssertInsideRoot(path, transaction.GameRoot);
        var operation = AddPendingOperation(
            transaction,
            "CreateDirectory",
            reason,
            new Dictionary<string, string?> { ["Path"] = path });

        Directory.CreateDirectory(path);
        CompleteOperation(transaction, operation);
    }

    private void RestoreTextFile(DoctorTransaction transaction, DoctorOperation operation, bool force)
    {
        var path = operation.Data["Path"]!;
        var backupPath = operation.Data["BackupPath"]!;
        AssertInsideRoot(path, transaction.GameRoot);
        AssertInsideTransaction(transaction, backupPath);
        if (!File.Exists(backupPath))
        {
            AddWarning(transaction, $"Restore backup is missing: {backupPath}");
            return;
        }

        var currentHash = File.Exists(path) ? ComputeFileHash(path) : null;
        var resultHash = operation.Data.GetValueOrDefault("ResultHash");
        if (!force &&
            currentHash is not null &&
            !string.IsNullOrWhiteSpace(resultHash) &&
            !string.Equals(currentHash, resultHash, StringComparison.OrdinalIgnoreCase))
        {
            AddWarning(transaction, $"Restore skipped modified file to avoid overwriting newer changes: {path}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(backupPath, path, true);
    }

    private void RestoreMovedPath(DoctorTransaction transaction, DoctorOperation operation)
    {
        var sourcePath = operation.Data["SourcePath"]!;
        var destinationPath = operation.Data["DestinationPath"]!;
        AssertInsideRoot(sourcePath, transaction.GameRoot);
        AssertInsideTransaction(transaction, destinationPath);
        if (!PathExists(destinationPath))
        {
            AddWarning(transaction, $"Restore source is missing: {destinationPath}");
            return;
        }

        if (PathExists(sourcePath))
        {
            AddWarning(transaction, $"Restore skipped because the destination already exists: {sourcePath}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        MovePath(destinationPath, sourcePath);
    }

    private void RemoveDoctorDirectoryIfEmpty(DoctorTransaction transaction, string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            AddWarning(transaction, $"Restore skipped removing non-empty doctor-created directory: {path}");
            return;
        }

        Directory.Delete(path);
    }

    private void DeletePathIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private DoctorTransaction NewTransaction(string gameRoot, string action)
    {
        var transaction = new DoctorTransaction
        {
            Id = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            Action = action,
            GameRoot = gameRoot,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        SaveTransaction(transaction);
        return transaction;
    }

    private DoctorOperation AddPendingOperation(DoctorTransaction transaction, string type, string reason, Dictionary<string, string?> data)
    {
        var operation = new DoctorOperation
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            Reason = reason,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Data = data
        };

        transaction.Operations.Add(operation);
        SaveTransaction(transaction);
        return operation;
    }

    private void AddCompletedOperation(DoctorTransaction transaction, string type, string reason, Dictionary<string, string?> data)
    {
        var operation = AddPendingOperation(transaction, type, reason, data);
        CompleteOperation(transaction, operation);
    }

    private void CompleteOperation(DoctorTransaction transaction, DoctorOperation operation)
    {
        operation.Status = "completed";
        operation.CompletedAtUtc = DateTimeOffset.UtcNow;
        SaveTransaction(transaction);
    }

    private void AddWarning(DoctorTransaction transaction, string warning)
    {
        transaction.Warnings.Add(warning);
        SaveTransaction(transaction);
    }

    private void SaveTransaction(DoctorTransaction transaction)
    {
        var folder = GetTransactionFolder(transaction.Id);
        Directory.CreateDirectory(folder);
        WriteJson(Path.Combine(folder, "transaction.json"), transaction);
    }

    private DoctorTransaction LoadTransaction(string transactionId)
    {
        var path = Path.Combine(GetTransactionFolder(transactionId), "transaction.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Transaction not found: {transactionId}", path);
        }

        return ReadJson<DoctorTransaction>(path);
    }

    private string GetTransactionFolder(string transactionId) => Path.Combine(_stateRoot, "transactions", transactionId);

    private void AssertInsideTransaction(DoctorTransaction transaction, string path) =>
        AssertInsideBoundary(path, GetTransactionFolder(transaction.Id), "recorded transaction folder");

    private static string GetOperationActionText(DoctorOperation operation) =>
        operation.Type switch
        {
            "MovePath" => $"Moved '{operation.Data["SourcePath"]}' to '{operation.Data["DestinationPath"]}'.",
            "CreateDirectory" => $"Created an empty placeholder folder at '{operation.Data["Path"]}'.",
            "RewriteTextFile" => $"Backed up and cleaned '{operation.Data["Path"]}' by removing stale build override lines.",
            "CopyPath" => $"Created a safety copy of '{operation.Data["SourcePath"]}' at '{operation.Data["DestinationPath"]}'.",
            "CreateBackupArchive" => $"Created TotCustom backups from '{operation.Data["SourcePath"]}' with the newest archive at '{operation.Data["BackupPath"]}'.",
            "InstallTotCustomFromArchive" => $"Loaded TotCustom backup '{operation.Data["SourcePath"]}' into '{operation.Data["DestinationPath"]}'.",
            "InstallTotCustomFromFolder" => $"Copied TotCustom from '{operation.Data["SourcePath"]}' into '{operation.Data["DestinationPath"]}'.",
            _ => operation.Reason
        };

    private string ResolveConanGameRoot(string? gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            var resolved = Path.GetFullPath(gameRoot);
            if (!Directory.Exists(resolved))
            {
                throw new DirectoryNotFoundException($"The supplied Conan game root does not exist: {resolved}");
            }

            return resolved;
        }

        var candidates = GetConanInstallCandidates().ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new DirectoryNotFoundException("No Conan install was discovered automatically. Choose a Conan install folder."),
            _ => throw new InvalidOperationException("Multiple Conan installs were discovered. Choose the install you want to inspect or repair.")
        };
    }

    private IEnumerable<string> GetConanInstallCandidates()
    {
        var installNames = new[] { "Conan Exiles", "Conan Exiles Enhanced", "Conan Exiles Legacy" };
        var results = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var libraryRoot in GetSteamLibraryRoots())
        {
            foreach (var installName in installNames)
            {
                var candidate = Path.Combine(libraryRoot, "steamapps", "common", installName);
                if (Directory.Exists(candidate))
                {
                    results.Add(Path.GetFullPath(candidate));
                }
            }
        }

        return results;
    }

    private string ResolveConanSteamAppsRoot()
    {
        var candidates = GetSteamLibraryRoots()
            .Select(root => Path.Combine(root, "steamapps"))
            .Where(Directory.Exists)
            .Select(steamApps => new
            {
                Path = steamApps,
                Score = ScoreConanSteamAppsRoot(steamApps)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.Count switch
        {
            1 => candidates[0].Path,
            0 => throw new DirectoryNotFoundException("No Steam library containing Conan Exiles folders or manifest data was found."),
            _ when candidates[0].Score > candidates[1].Score => candidates[0].Path,
            _ => throw new InvalidOperationException("More than one Steam library looks equally likely for Conan Exiles. Keep only the active Conan copy exposed in one library, then refresh the assistant.")
        };
    }

    private static int ScoreConanSteamAppsRoot(string steamAppsRoot)
    {
        var commonRoot = Path.Combine(steamAppsRoot, "common");
        var manifestPath = Path.Combine(steamAppsRoot, "appmanifest_440900.acf");
        var managedPath = Path.Combine(commonRoot, "Conan Exiles");
        var enhancedPath = Path.Combine(commonRoot, "Conan Exiles Enhanced");
        var legacyPath = Path.Combine(commonRoot, "Conan Exiles Legacy");
        var score = 0;

        if (File.Exists(manifestPath))
        {
            score += 20;
        }

        if (Directory.Exists(managedPath))
        {
            score += 10;
        }

        if (Directory.Exists(enhancedPath))
        {
            score += 6;
        }

        if (Directory.Exists(legacyPath))
        {
            score += 6;
        }

        if (Directory.Exists(managedPath) && (Directory.Exists(enhancedPath) || Directory.Exists(legacyPath)))
        {
            score += 12;
        }

        if (Directory.Exists(enhancedPath) && Directory.Exists(legacyPath))
        {
            score += 8;
        }

        return score;
    }

    private static SteamRediscoveryFolder GetSteamRediscoveryFolder(string label, string path)
    {
        var exists = Directory.Exists(path);
        return new SteamRediscoveryFolder(
            label,
            path,
            exists,
            exists && File.Exists(Path.Combine(path, "ConanSandbox", "Binaries", "Win64", "ConanSandbox-Win64-Shipping.exe")),
            exists && File.Exists(Path.Combine(path, "ConanSandbox", "Binaries", "Win64", "ConanSandbox.exe")));
    }

    private static SteamRediscoveryWorkshop GetSteamRediscoveryWorkshop(string steamAppsRoot)
    {
        var workshopRoot = Path.Combine(steamAppsRoot, "workshop");
        var contentPath = Path.Combine(workshopRoot, "content", "440900");
        var parkedRoot = Path.Combine(workshopRoot, "ConanLegacyDoctorParked");
        var parkedContentPath = Path.Combine(parkedRoot, "content_440900");
        var manifestPath = Path.Combine(workshopRoot, "appworkshop_440900.acf");
        var parkedManifestPath = Path.Combine(parkedRoot, "appworkshop_440900.acf");

        return new SteamRediscoveryWorkshop(
            contentPath,
            Directory.Exists(contentPath),
            parkedContentPath,
            Directory.Exists(parkedContentPath),
            manifestPath,
            File.Exists(manifestPath),
            parkedManifestPath,
            File.Exists(parkedManifestPath));
    }

    private static string NormalizeRediscoveryTarget(string targetBranch) =>
        targetBranch.Equals("Legacy", StringComparison.OrdinalIgnoreCase)
            ? "Legacy"
            : targetBranch.Equals("Enhanced", StringComparison.OrdinalIgnoreCase)
                ? "Enhanced"
                : throw new InvalidOperationException("Choose Enhanced or Legacy as the Steam switch target.");

    private static string? ReadManifestSectionValue(string manifest, string sectionName, string key)
    {
        var section = Regex.Match(manifest, $"\"{Regex.Escape(sectionName)}\"\\s*\\{{(?<body>[\\s\\S]*?)\\}}", RegexOptions.IgnoreCase);
        return section.Success ? ReadManifestValue(section.Groups["body"].Value, key) : null;
    }

    private static long? ReadManifestLong(string manifest, string key)
    {
        var value = ReadManifestValue(manifest, key);
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private IEnumerable<string> GetSteamLibraryRoots()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in GetSteamRootCandidates())
        {
            if (Directory.Exists(steamRoot))
            {
                libraries.Add(steamRoot);
            }

            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            var raw = File.ReadAllText(libraryFile);
            var patterns = new[]
            {
                "\"path\"\\s*\"(?<path>[^\"]+)\"",
                "\"\\d+\"\\s*\"(?<path>[^\"]+)\""
            };

            foreach (var pattern in patterns)
            {
                foreach (Match match in Regex.Matches(raw, pattern, RegexOptions.IgnoreCase))
                {
                    var pathValue = match.Groups["path"].Value.Replace("\\\\", "\\");
                    if (!string.IsNullOrWhiteSpace(pathValue))
                    {
                        libraries.Add(Path.GetFullPath(pathValue));
                    }
                }
            }
        }

        return libraries;
    }

    private static IEnumerable<string> GetSteamRootCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return candidates;
        }

        foreach (var registryPath in new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\Software\Valve\Steam"
        })
        {
            foreach (var propertyName in new[] { "SteamPath", "InstallPath" })
            {
                var value = Registry.GetValue(registryPath, propertyName, null) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    candidates.Add(Path.GetFullPath(value));
                }
            }
        }

        foreach (var fallback in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        })
        {
            if (string.IsNullOrWhiteSpace(fallback))
            {
                continue;
            }

            var steamPath = Path.Combine(fallback, "Steam");
            if (Directory.Exists(steamPath))
            {
                candidates.Add(Path.GetFullPath(steamPath));
            }
        }

        return candidates;
    }

    private SteamBranchInfo GetSteamManifestInfo(string gameRoot)
    {
        var commonRoot = Directory.GetParent(gameRoot)?.FullName;
        if (!string.Equals(Path.GetFileName(commonRoot), "common", StringComparison.OrdinalIgnoreCase))
        {
            var selectedFolder = Path.GetFileName(gameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(selectedFolder, "Conan Exiles Legacy", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamBranchInfo(
                    false,
                    null,
                    null,
                    null,
                    null,
                    "LegacySideBySideCopy",
                    "medium",
                    "This folder uses the Conan Exiles Legacy side-by-side name, so it is treated as a Legacy repair target even though Steam manifest lookup is unavailable.");
            }

            if (string.Equals(selectedFolder, "Conan Exiles", StringComparison.OrdinalIgnoreCase)
                || string.Equals(selectedFolder, "Conan Exiles Enhanced", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamBranchInfo(
                    false,
                    null,
                    null,
                    null,
                    null,
                    "EnhancedOrDefault",
                    "medium",
                    "This folder uses the default or Enhanced Conan naming pattern, so it is treated as the default/Enhanced branch when Steam manifest lookup is unavailable.");
            }

            return new SteamBranchInfo(
                false,
                null,
                null,
                null,
                null,
                "Unknown",
                "low",
                "Steam manifest lookup is not available for this folder layout.");
        }

        var steamAppsRoot = Directory.GetParent(commonRoot!)?.FullName;
        var manifestPath = steamAppsRoot is null ? null : Path.Combine(steamAppsRoot, "appmanifest_440900.acf");
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            var selectedFolder = Path.GetFileName(gameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(selectedFolder, "Conan Exiles Legacy", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamBranchInfo(
                    false,
                    manifestPath,
                    null,
                    null,
                    null,
                    "LegacySideBySideCopy",
                    "medium",
                    "This folder uses the Conan Exiles Legacy side-by-side name, so it is treated as a Legacy repair target even though no nearby Steam manifest was found.");
            }

            if (string.Equals(selectedFolder, "Conan Exiles", StringComparison.OrdinalIgnoreCase)
                || string.Equals(selectedFolder, "Conan Exiles Enhanced", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamBranchInfo(
                    false,
                    manifestPath,
                    null,
                    null,
                    null,
                    "EnhancedOrDefault",
                    "medium",
                    "This folder uses the default or Enhanced Conan naming pattern, so it is treated as the default/Enhanced branch when no nearby Steam manifest is available.");
            }

            return new SteamBranchInfo(
                false,
                manifestPath,
                null,
                null,
                null,
                "Unknown",
                "low",
                "Steam manifest was not found next to this Conan install.");
        }

        var manifest = File.ReadAllText(manifestPath);
        var betaKey = ReadManifestValue(manifest, "betakey");
        var installDir = ReadManifestValue(manifest, "installdir");
        var buildId = ReadManifestValue(manifest, "buildid");
        var gameRootLeaf = Path.GetFileName(gameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool? installDirMatches = string.IsNullOrWhiteSpace(installDir)
            ? null
            : string.Equals(installDir, gameRootLeaf, StringComparison.OrdinalIgnoreCase);

        if (installDirMatches == false)
        {
            if (string.Equals(gameRootLeaf, "Conan Exiles Legacy", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamBranchInfo(
                    true,
                    manifestPath,
                    betaKey,
                    installDir,
                    false,
                    "LegacySideBySideCopy",
                    "medium",
                    $"This folder matches Conay's side-by-side Legacy naming convention. The nearby Steam manifest belongs to '{installDir}', not this folder.",
                    buildId);
            }

            return new SteamBranchInfo(
                true,
                manifestPath,
                betaKey,
                installDir,
                false,
                "DetachedSideBySideCopy",
                "low",
                $"The nearby Steam manifest belongs to '{installDir}', not this selected folder, so the selected folder is treated as a detached side-by-side copy.",
                buildId);
        }

        if (string.Equals(betaKey, "conan-exiles-legacy", StringComparison.OrdinalIgnoreCase))
        {
            return new SteamBranchInfo(
                true,
                manifestPath,
                betaKey,
                installDir,
                installDirMatches,
                "Legacy",
                "high",
                "Steam manifest indicates the Conan Exiles Legacy beta branch.",
                buildId);
        }

        if (string.IsNullOrWhiteSpace(betaKey))
        {
            return new SteamBranchInfo(
                true,
                manifestPath,
                betaKey,
                installDir,
                installDirMatches,
                "EnhancedOrDefault",
                "high",
                "Steam manifest has no beta key, which usually means the default Enhanced branch is selected.",
                buildId);
        }

        return new SteamBranchInfo(
            true,
            manifestPath,
            betaKey,
            installDir,
            installDirMatches,
            "OtherBeta",
            "medium",
            $"Steam manifest uses beta key '{betaKey}', which Legacy Doctor does not classify further.",
            buildId);
    }

    private static string? ReadManifestValue(string manifest, string key)
    {
        var match = Regex.Match(manifest, $"\"{Regex.Escape(key)}\"\\s*\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string SanitizePathSegment(string value) =>
        Regex.Replace(value, "[^A-Za-z0-9._-]", "_");

    private static string GetBackupPathKey(string gameRoot)
    {
        var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(gameRoot).ToLowerInvariant());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..10];
    }

    private static string? ComputeFileHash(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AssertInsideRoot(string path, string root) =>
        AssertInsideBoundary(path, root, "Conan game root");

    private static void AssertInsideBoundary(string path, string root, string boundaryName)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var isInside = string.Equals(fullPath, fullRoot, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);

        if (!isInside)
        {
            throw new InvalidOperationException($"Refusing to operate outside the {boundaryName}: {path}");
        }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void MovePath(string sourcePath, string destinationPath)
    {
        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath, true);
        }
    }

    private static void CopyPath(string sourcePath, string destinationPath)
    {
        if (Directory.Exists(sourcePath))
        {
            CopyDirectory(sourcePath, destinationPath);
        }
        else
        {
            File.Copy(sourcePath, destinationPath, true);
        }
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, directory);
            Directory.CreateDirectory(Path.Combine(destinationPath, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            var destinationFile = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, true);
        }
    }

    private static void ExtractZipArchiveSafely(string archivePath, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        var fullDestinationRoot = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(fullDestinationRoot, entry.FullName));
            if (!destinationPath.StartsWith(fullDestinationRoot, comparison))
            {
                throw new InvalidDataException($"TotCustom backup archive contains an unsafe entry path: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static T ReadJson<T>(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Unable to deserialize JSON at {path}");
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    }

    private static void WriteBundleJson<T>(string stagingRoot, string fileName, T value, ICollection<string> stagedFiles)
    {
        var path = Path.Combine(stagingRoot, fileName);
        WriteJson(path, value);
        stagedFiles.Add(path);
    }

    private static void AddRecentLogTails(string gameRoot, string stagingRoot, ICollection<string> stagedFiles)
    {
        var logsRoot = Path.Combine(gameRoot, "ConanSandbox", "Saved", "Logs");
        if (!Directory.Exists(logsRoot))
        {
            return;
        }

        var bundleLogsRoot = Path.Combine(stagingRoot, "logs");
        Directory.CreateDirectory(bundleLogsRoot);

        foreach (var log in Directory.EnumerateFiles(logsRoot, "*.log", SearchOption.TopDirectoryOnly)
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(info => info.LastWriteTimeUtc)
                     .Take(3))
        {
            var target = Path.Combine(bundleLogsRoot, $"{Path.GetFileNameWithoutExtension(log.Name)}.tail.txt");
            File.WriteAllLines(target, File.ReadLines(log.FullName).TakeLast(1500), new UTF8Encoding(false));
            stagedFiles.Add(target);
        }
    }

    private static void AddConfigSnapshots(string gameRoot, string stagingRoot, ICollection<string> stagedFiles)
    {
        var bundleConfigRoot = Path.Combine(stagingRoot, "config");
        Directory.CreateDirectory(bundleConfigRoot);
        foreach (var sourcePath in new[]
        {
            Path.Combine(gameRoot, "ConanSandbox", "Saved", "Config", "WindowsNoEditor", "Engine.ini"),
            Path.Combine(gameRoot, "ConanSandbox", "Saved", "Config", "WindowsNoEditor", "Game.ini"),
            Path.Combine(gameRoot, "ConanSandbox", "Mods", "modlist.txt")
        })
        {
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var destination = Path.Combine(bundleConfigRoot, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, destination, true);
            stagedFiles.Add(destination);
        }
    }

    private sealed class OperationDataConverter : JsonConverter<Dictionary<string, string?>>
    {
        public override Dictionary<string, string?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Operation data must be a JSON object.");
            }

            var data = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                data[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => property.Value.GetString(),
                    _ => property.Value.GetRawText()
                };
            }

            return data;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, string?> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var pair in value)
            {
                if (pair.Value is null)
                {
                    writer.WriteNull(pair.Key);
                }
                else
                {
                    writer.WriteString(pair.Key, pair.Value);
                }
            }

            writer.WriteEndObject();
        }
    }
}
