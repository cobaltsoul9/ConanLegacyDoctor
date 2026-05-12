using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ConanLegacyDoctor.Core;

public sealed class LegacyDoctorService
{
    public const int SchemaVersion = 1;
    public const string ToolName = "ConanLegacyDoctor";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

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
                    RemoveDoctorDirectoryIfEmpty(transaction, operation.Data["Path"]!);
                    break;

                case "RewriteTextFile":
                    RestoreTextFile(transaction, operation, force);
                    break;

                case "CopyPath":
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

            transactions.Add(ReadJson<DoctorTransaction>(transactionPath));
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

    public VanillaLaunchPlan GetVanillaLaunchPlan(string? gameRoot)
    {
        var resolvedGameRoot = ResolveConanGameRoot(gameRoot);
        var branch = GetSteamManifestInfo(resolvedGameRoot);
        var warnings = new List<string>();
        var modListPath = Path.Combine(resolvedGameRoot, "ConanSandbox", "Mods", "modlist.txt");
        var modListPresent = File.Exists(modListPath);

        if (modListPresent)
        {
            warnings.Add($"Active mod list detected at '{modListPath}'. Run Prepare Legacy first so the doctor can move it aside reversibly.");
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
            throw new InvalidOperationException("Vanilla launch is blocked because modlist.txt is still active. Run Prepare Legacy first, then try the vanilla launch again.");
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
                "ModControllerCache.json is present. Prepare Legacy can move it aside reversibly during cleanup.",
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
            "TotCustom save data is present. Prepare Legacy will create a rotating backup before cleanup.",
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

        var slot1 = Path.Combine(backupFolder, "TotCustom_1.zip");
        var slot2 = Path.Combine(backupFolder, "TotCustom_2.zip");
        var slot3 = Path.Combine(backupFolder, "TotCustom_3.zip");
        var tempZip = Path.Combine(backupFolder, $"TotCustom_{Guid.NewGuid():N}.tmp.zip");

        try
        {
            ZipFile.CreateFromDirectory(sourcePath, tempZip);
            if (File.Exists(slot2))
            {
                File.Move(slot2, slot3, true);
            }

            if (File.Exists(slot1))
            {
                File.Move(slot1, slot2, true);
            }

            File.Move(tempZip, slot1, true);
            AddCompletedOperation(
                transaction,
                "CreateBackupArchive",
                "Create a rotating backup of TotCustom save files before Legacy cleanup.",
                new Dictionary<string, string?>
                {
                    ["SourcePath"] = sourcePath,
                    ["BackupPath"] = slot1,
                    ["RotationSlots"] = string.Join("|", slot1, slot2, slot3)
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

    private static string GetOperationActionText(DoctorOperation operation) =>
        operation.Type switch
        {
            "MovePath" => $"Moved '{operation.Data["SourcePath"]}' to '{operation.Data["DestinationPath"]}'.",
            "CreateDirectory" => $"Created an empty placeholder folder at '{operation.Data["Path"]}'.",
            "RewriteTextFile" => $"Backed up and cleaned '{operation.Data["Path"]}' by removing stale build override lines.",
            "CopyPath" => $"Created a safety copy of '{operation.Data["SourcePath"]}' at '{operation.Data["DestinationPath"]}'.",
            "CreateBackupArchive" => $"Created a rotating TotCustom backup from '{operation.Data["SourcePath"]}' at '{operation.Data["BackupPath"]}'.",
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

    private static void AssertInsideRoot(string path, string root)
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
            throw new InvalidOperationException($"Refusing to operate outside the Conan game root: {path}");
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
}
