namespace ConanLegacyDoctor.Core;

public sealed record Finding(
    string Id,
    string Severity,
    string Message,
    string? Path,
    IReadOnlyDictionary<string, object?>? Details = null);

public sealed record SteamBranchInfo(
    bool Detected,
    string? ManifestPath,
    string? BetaKey,
    string? InstallDir,
    bool? InstallDirMatchesGameRoot,
    string BranchMode,
    string Confidence,
    string Message,
    string? BuildId = null);

public sealed record InstallCandidate(
    string DisplayName,
    string Path,
    string FolderName,
    string Branch,
    string Confidence,
    string Message);

public sealed record ScanResult(
    int SchemaVersion,
    string Tool,
    string GameRoot,
    DateTimeOffset ScannedAtUtc,
    SteamBranchInfo Branch,
    IReadOnlyList<Finding> Findings);

public sealed record PreparationOptions(
    bool QuarantineModsDirectory,
    bool ResetClientConfig,
    bool QuarantineSaveDatabases);

public sealed class DoctorTransaction
{
    public int SchemaVersion { get; init; } = 1;
    public string Tool { get; init; } = LegacyDoctorService.ToolName;
    public string Id { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string GameRoot { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? RestoredAtUtc { get; set; }
    public string Status { get; set; } = "pending";
    public List<DoctorOperation> Operations { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class DoctorOperation
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public Dictionary<string, string?> Data { get; init; } = [];
}

public sealed record DoctorAction(
    string Id,
    string Status,
    string Action,
    DateTimeOffset CreatedAtUtc,
    string GameRoot,
    string Summary,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> Warnings);

public sealed record VanillaLaunchPlan(
    int SchemaVersion,
    string Tool,
    string GameRoot,
    string BranchMode,
    string BranchConfidence,
    bool VanillaReady,
    string ModListPath,
    string LaunchStrategy,
    string? ExecutablePath,
    string SteamUri,
    bool SteamFallbackAllowed,
    IReadOnlyList<string> Warnings);

public sealed record VanillaLaunchResult(
    int SchemaVersion,
    string Tool,
    string GameRoot,
    string BranchMode,
    string LaunchStrategy,
    string? ExecutablePath,
    string SteamUri,
    DateTimeOffset StartedAtUtc);

public sealed record SteamValidationPlan(
    int SchemaVersion,
    string Tool,
    string GameRoot,
    string BranchMode,
    string BranchConfidence,
    bool SteamManagedTarget,
    string SteamUri,
    IReadOnlyList<string> Warnings);

public sealed record SteamValidationResult(
    int SchemaVersion,
    string Tool,
    string GameRoot,
    string BranchMode,
    string SteamUri,
    DateTimeOffset StartedAtUtc);

public sealed record SupportBundleOptions(
    string? DestinationPath,
    bool IncludeRecentLogs,
    bool IncludeConfigSnapshots);

public sealed record SupportBundleResult(
    int SchemaVersion,
    string Tool,
    DateTimeOffset CreatedAtUtc,
    string GameRoot,
    string DestinationPath,
    bool IncludeRecentLogs,
    bool IncludeConfigSnapshots,
    int StagedFileCount);

public sealed record TotCustomSource(
    string Id,
    string DisplayName,
    string SourceKind,
    string SourcePath,
    string? SourceGameRoot,
    DateTimeOffset CapturedAtUtc,
    long? SizeBytes,
    string Detail);

public sealed record SteamRediscoveryFolder(
    string Label,
    string Path,
    bool Exists,
    bool LooksEnhanced,
    bool LooksLegacy);

public sealed record SteamRediscoveryState(
    int SchemaVersion,
    string Tool,
    string SteamAppsRoot,
    string ManifestPath,
    bool ManifestExists,
    string? RequestedBetaKey,
    string? MountedBetaKey,
    string? BuildId,
    string? TargetBuildId,
    long? BytesToDownload,
    long? BytesDownloaded,
    SteamRediscoveryFolder Managed,
    SteamRediscoveryFolder EnhancedParked,
    SteamRediscoveryFolder LegacyParked,
    bool TargetEnhancedAlreadyManaged,
    bool TargetLegacyAlreadyManaged,
    bool CanParkManagedForEnhancedSwitch,
    bool CanParkManagedForLegacySwitch,
    bool CanExposeEnhancedForInstall,
    bool CanExposeLegacyForInstall,
    IReadOnlyList<string> Guidance);
