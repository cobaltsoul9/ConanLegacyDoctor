using ConanLegacyDoctor.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

var tempRoot = Path.Combine(Path.GetTempPath(), "ConanLegacyDoctor-CoreSmoke", Guid.NewGuid().ToString("N"));
var gameRoot = Path.Combine(tempRoot, "SteamLibrary", "steamapps", "common", "Conan Exiles Legacy");
var steamAppsRoot = Path.Combine(tempRoot, "SteamLibrary", "steamapps");
var stateRoot = Path.Combine(tempRoot, "state");

Directory.CreateDirectory(Path.Combine(gameRoot, "ConanSandbox", "Mods"));
Directory.CreateDirectory(Path.Combine(gameRoot, "ConanSandbox", "Saved", "Config", "WindowsNoEditor"));
Directory.CreateDirectory(Path.Combine(gameRoot, "ConanSandbox", "Saved", "SaveGames", "TotCustom"));
Directory.CreateDirectory(Path.Combine(gameRoot, "ConanSandbox", "Binaries", "Win64"));
Directory.CreateDirectory(steamAppsRoot);

File.WriteAllText(Path.Combine(gameRoot, "ConanSandbox", "Mods", "modlist.txt"), "mod-a.pak");
File.WriteAllText(
    Path.Combine(gameRoot, "ConanSandbox", "Saved", "Config", "WindowsNoEditor", "Engine.ini"),
    "[OnlineSubsystem]\n" +
    "bUseBuildIdOverride=True\n" +
    "BuildIdOverride=460304166\n" +
    "SomeOtherSetting=True\n");
File.WriteAllText(Path.Combine(gameRoot, "ConanSandbox", "Saved", "ModControllerCache.json"), "{\"cache\":true}");
File.WriteAllText(Path.Combine(gameRoot, "ConanSandbox", "Saved", "game.db"), "legacy-save");
File.WriteAllText(Path.Combine(gameRoot, "ConanSandbox", "Saved", "SaveGames", "TotCustom", "player.json"), "{\"looks\":\"important\"}");
File.WriteAllText(Path.Combine(gameRoot, "ConanSandbox", "Binaries", "Win64", "ConanSandbox.exe"), "stub");
File.WriteAllText(
    Path.Combine(steamAppsRoot, "appmanifest_440900.acf"),
    "\"AppState\"\n{\n    \"appid\" \"440900\"\n    \"installdir\" \"Conan Exiles Legacy\"\n    \"UserConfig\"\n    {\n        \"betakey\" \"conan-exiles-legacy\"\n    }\n}\n");

try
{
    var doctor = new LegacyDoctorService(stateRoot);
    var scan = doctor.Scan(gameRoot);
    Assert(scan.Branch.BranchMode == "Legacy", "Expected Legacy branch classification.");
    Assert(scan.Findings.Any(finding => finding.Id == "mods.modlist"), "Expected modlist finding.");
    Assert(scan.Findings.Any(finding => finding.Id == "config.engine-build-override"), "Expected build override finding.");

    var transaction = doctor.PrepareLegacy(
        gameRoot,
        new PreparationOptions(
            QuarantineModsDirectory: false,
            ResetClientConfig: false,
            QuarantineSaveDatabases: true));

    Assert(!File.Exists(Path.Combine(gameRoot, "ConanSandbox", "Mods", "modlist.txt")), "Expected modlist quarantine.");
    Assert(!File.Exists(Path.Combine(gameRoot, "ConanSandbox", "Saved", "game.db")), "Expected save DB quarantine.");
    Assert(File.Exists(Path.Combine(stateRoot, "transactions", transaction.Id, "quarantine", "SaveDatabases", "game.db")), "Expected quarantined save DB.");
    Assert(File.Exists(Path.Combine(stateRoot, "transactions", transaction.Id, "quarantine", "SaveDatabases", "game.db.backup")), "Expected safety copy.");

    RewriteRotationSlotsAsLegacyArray(stateRoot, transaction.Id);

    var actions = doctor.GetActions();
    Assert(actions.Any(action => action.Id == transaction.Id), "Expected action record.");
    Assert(actions.Single(action => action.Id == transaction.Id).Details.Any(detail => detail.Contains("safety copy", StringComparison.OrdinalIgnoreCase)), "Expected safety-copy action detail.");

    for (var backupIndex = 0; backupIndex < 8; backupIndex++)
    {
        File.WriteAllText(
            Path.Combine(gameRoot, "ConanSandbox", "Saved", "SaveGames", "TotCustom", $"snapshot-{backupIndex}.json"),
            $"{{\"snapshot\":{backupIndex}}}");
        doctor.PrepareLegacy(
            gameRoot,
            new PreparationOptions(
                QuarantineModsDirectory: false,
                ResetClientConfig: false,
                QuarantineSaveDatabases: false));
    }

    var totCustomSources = doctor.GetTotCustomSources();
    var archivedTotCustom = totCustomSources.FirstOrDefault(source => source.SourceKind == "Doctor Backup" && source.DisplayName.StartsWith("TotCustom 1", StringComparison.Ordinal));
    Assert(archivedTotCustom is not null, "Expected a TotCustom backup source.");
    Assert(totCustomSources.Any(source => source.DisplayName.StartsWith("Preserved first backup", StringComparison.Ordinal)), "Expected oldest preserved TotCustom backup source.");
    Assert(totCustomSources.Count(source => source.SourceKind == "Doctor Backup") >= 9, "Expected preserved first backup plus expanded rolling backup set.");

    var totCustomFile = Path.Combine(gameRoot, "ConanSandbox", "Saved", "SaveGames", "TotCustom", "player.json");
    File.WriteAllText(totCustomFile, "{\"looks\":\"changed\"}");
    var totRestore = doctor.RestoreTotCustomSource(gameRoot, archivedTotCustom!.Id);
    Assert(File.ReadAllText(totCustomFile).Contains("important", StringComparison.Ordinal), "Expected TotCustom archive restore to replace current contents.");
    Assert(doctor.GetActions().Any(action => action.Id == totRestore.Id && action.Action == "restore-totcustom"), "Expected TotCustom restore action.");

    doctor.Restore(totRestore.Id, force: false);
    Assert(File.ReadAllText(totCustomFile).Contains("changed", StringComparison.Ordinal), "Expected TotCustom restore undo to bring back previous folder.");

    var launchPlan = doctor.GetVanillaLaunchPlan(gameRoot);
    Assert(launchPlan.VanillaReady, "Expected vanilla launch readiness after modlist quarantine.");
    Assert(launchPlan.LaunchStrategy == "DirectExecutable", "Expected direct executable launch strategy.");

    var restored = doctor.Restore(transaction.Id, force: false);
    Assert(restored.Status == "restored", "Expected restored transaction.");
    Assert(File.Exists(Path.Combine(gameRoot, "ConanSandbox", "Mods", "modlist.txt")), "Expected modlist restore.");
    Assert(File.Exists(Path.Combine(gameRoot, "ConanSandbox", "Saved", "game.db")), "Expected save DB restore.");

    Console.WriteLine("Core smoke test passed.");
}
finally
{
    if (Directory.Exists(tempRoot))
    {
        Directory.Delete(tempRoot, true);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void RewriteRotationSlotsAsLegacyArray(string stateRoot, string transactionId)
{
    var transactionPath = Path.Combine(stateRoot, "transactions", transactionId, "transaction.json");
    var root = JsonNode.Parse(File.ReadAllText(transactionPath))
        ?? throw new InvalidOperationException("Expected transaction JSON.");
    var operations = root["Operations"]?.AsArray()
        ?? throw new InvalidOperationException("Expected operation array.");

    foreach (var operation in operations)
    {
        if (!string.Equals(operation?["Type"]?.GetValue<string>(), "CreateBackupArchive", StringComparison.Ordinal))
        {
            continue;
        }

        operation!["Data"]!["RotationSlots"] = new JsonArray("slot1", "slot2", "slot3");
        File.WriteAllText(transactionPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return;
    }

    throw new InvalidOperationException("Expected TotCustom backup operation.");
}
