using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Backup;

/// <summary>
/// How a restore reconciles the archive with whatever already exists on the destination machine.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupRestoreConflictMode>))]
public enum BackupRestoreConflictMode
{

    /// <summary>Displace the current installation and commit the archive in its place.</summary>
    ReplaceInstallation = 0,

    /// <summary>Materialize the archive under a new, empty profile root; the current installation is untouched.</summary>
    NewProfileRoot = 1,

    /// <summary>Merge selected Sessions into the current installation without replacing anything.</summary>
    ImportSelectedSessions = 2,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupRestoreStatus>))]
public enum BackupRestoreStatus
{

    /// <summary>Every phase committed; the destination holds the restored generation.</summary>
    Completed = 0,

    /// <summary>A read-only rehearsal finished; the destination was never mutated.</summary>
    DryRunCompleted = 1,

    /// <summary>The restore refused before mutating the destination.</summary>
    Rejected = 2,

    /// <summary>Commit failed and the prior installation was returned to its original state.</summary>
    RolledBack = 3,

    /// <summary>The destination is committed but post-commit reconciliation needs an operator.</summary>
    ReconciliationRequired = 4,

}

/// <summary>
/// Ordered phases of a durable restore. Every phase boundary is journaled so a restart can
/// deterministically resume validation, commit, roll back, or demand reconciliation.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupRestorePhase>))]
public enum BackupRestorePhase
{

    Authenticate = 0,

    Inventory = 1,

    Capacity = 2,

    Stage = 3,

    Migrate = 4,

    RemapPaths = 5,

    RewrapSecrets = 6,

    Validate = 7,

    SafetyPoint = 8,

    Commit = 9,

    Reconcile = 10,

    Cleanup = 11,

}

/// <summary>
/// Typed machine-specific root that a restore may rewrite. Arbitrary paths are never remapped:
/// only the declared targets belonging to a kind are rewritten.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupPathMappingKind>))]
public enum BackupPathMappingKind
{

    /// <summary>Rewrites <c>Campaigns.Path</c> and Sanctum allow-list entries beneath it.</summary>
    CampaignRoot = 0,

    /// <summary>Rewrites <c>WorkspaceContexts.RootPath</c>, Sanctum allow-list entries, and the default workspace root.</summary>
    WorkspaceRoot = 1,

    /// <summary>Rewrites Sanctum allow-list entries naming a machine-specific Codex root.</summary>
    CodexRoot = 2,

    /// <summary>Rewrites Sanctum allow-list entries naming a machine-specific Spell root.</summary>
    SpellRoot = 3,

    /// <summary>Rewrites recorded attachment source provenance paths.</summary>
    AttachmentSourceProvenance = 4,

}

/// <summary>One accepted backup format together with the release contract it carries.</summary>
public sealed record BackupRestoreFormatEntry(
    int FormatVersion,
    string MinimumArcanumVersion,
    string SchemaAuthority,
    string Notes);

/// <summary>
/// The explicitly supported-format matrix. A restore consults this before it stages anything, so an
/// archive written by an unsupported newer Arcanum fails while the current installation is intact.
/// </summary>
public static class BackupRestoreFormatCatalog
{

    private static readonly BackupRestoreFormatEntry[] SupportedFormats =
    [
        new BackupRestoreFormatEntry(
            1,
            "0.1.0-beta",
            "GrimoireSchemaInstaller",
            "Initial ARCABACK container: PBKDF2-HMAC-SHA256 key derivation, AES-256-GCM chunked "
            + "payload, and a declarative Grimoire schema identified by its sqlite_master hash."),
    ];

    public static IReadOnlyList<BackupRestoreFormatEntry> Supported => SupportedFormats;

    public static bool IsSupported(int formatVersion) =>
        SupportedFormats.Any(entry => entry.FormatVersion == formatVersion);

    /// <summary>
    /// Returns <see langword="null"/> when the format can be restored, or the typed refusal to
    /// report otherwise. Newer formats are named separately from malformed ones so the operator is
    /// told to upgrade rather than to suspect corruption.
    /// </summary>
    public static BackupVerifyIssue? Classify(int formatVersion)
    {

        if (IsSupported(formatVersion))
        {

            return null;

        }

        int newest = SupportedFormats.Max(static entry => entry.FormatVersion);

        return formatVersion > newest
            ? new BackupVerifyIssue(
                "backup.restore_format_newer",
                $"This archive uses backup format {formatVersion}, but this Arcanum build restores at "
                + $"most format {newest}. Upgrade Arcanum to a release that lists format "
                + $"{formatVersion} as supported, then restore again. The current installation was not "
                + "modified.")
            : new BackupVerifyIssue(
                "backup.restore_format_unsupported",
                $"Backup format {formatVersion} is not a supported archive format. Supported formats: "
                + string.Join(", ", SupportedFormats.Select(static entry => entry.FormatVersion))
                + ". The current installation was not modified.");

    }

}

/// <summary>
/// What a restore is authorized to do with protected state the archive carries.
/// </summary>
/// <remarks>
/// Three explicit choices rather than a Boolean, because "restore it" and "purge it" are opposite
/// destructive acts and the default has to be neither. An archive holding Covenant canonical rows is
/// a standing agreement between an operator and an agent; silently reinstating it on a machine the
/// operator did not intend, or silently discarding it, are both decisions only the operator can make
/// (§10.6).
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupProtectedStateMode>))]
public enum BackupProtectedStateMode
{

    /// <summary>Refuse before staging if the archive carries any protected state.</summary>
    Reject = 0,

    /// <summary>Preserve the archive's Covenant data and labels, only from a clean source.</summary>
    RestoreProtectedState = 1,

    /// <summary>Securely remove the whole Covenant family and every protected artifact in staging.</summary>
    PurgeProtectedState = 2,

}

/// <summary>A single typed root rewrite requested for a restore.</summary>
public sealed record BackupPathMapping(
    BackupPathMappingKind Kind,
    string From,
    string To);

/// <param name="Confirmed">Whether the operator confirmed displacing the current installation.</param>
/// <param name="ProtectedStateConfirmed">
/// Whether the operator separately confirmed the destructive protected-state choice.
/// </param>
/// <remarks>
/// The two confirmations are distinct fields rather than one, because they answer two different
/// questions and the second is not implied by the first. Replacing an installation and reinstating (or
/// securely removing) a standing agreement between an operator and an agent are separate destructive
/// acts, and a single flag would let an operator who answered the first be treated as having answered
/// the second (§10.19.10). Neither reaches the restore effect digest: a confirmation authorizes no
/// effect of its own, so including it would make a rehearsal and the real run produce different owners.
/// </remarks>
public sealed record BackupRestoreRequest(
    string ArchivePath,
    BackupRestoreConflictMode ConflictMode = BackupRestoreConflictMode.ReplaceInstallation,
    string? DestinationRoot = null,
    Guid[]? SessionIds = null,
    BackupPathMapping[]? PathMappings = null,
    bool RestoreMasterApiKey = false,
    bool DryRun = false,
    bool Confirmed = false,
    bool CreateSafetyBackup = true,
    BackupProtectedStateMode ProtectedStateMode = BackupProtectedStateMode.Reject,
    BackupSessionCampaignMapping[]? CampaignMappings = null,
    bool ProtectedStateConfirmed = false);

public sealed record BackupRestorePhaseRecord(
    BackupRestorePhase Phase,
    string Detail);

public sealed record BackupRestoreMappingPlan(
    BackupPathMappingKind Kind,
    string From,
    string To,
    long MatchedTargets);

/// <summary>
/// How much has already left this installation irrevocably, as its own receipts record it.
/// </summary>
/// <remarks>
/// Folded from the destination's nonrevocable <c>external_disclosure_state</c> buckets, which are
/// seeded from physical-attempt receipts and joined forever. It exists so a destructive protected-state
/// choice can be preceded by a true number rather than by a warning that only gestures at one: local
/// removal cannot revoke a disclosure that already happened, and the honest way to say so is to say how
/// many attempts could have carried content out (§10.18).
///
/// <para><see cref="CountKind"/> is carried rather than resolved away because the two semantics are not
/// interchangeable. A joined bucket becomes a lower bound — two installations' counts cannot be added
/// without double-counting the effects they share — and reporting a lower bound as exact would
/// understate exposure in exactly the report an operator uses to decide.</para>
/// </remarks>
public sealed record BackupRestoreDisclosureExposure(
    bool EverOccurred,
    long PossibleAttempts,
    CovenantDisclosureCountKind CountKind)
{

    /// <summary>What a destination with no readable disclosure evidence contributes.</summary>
    public static BackupRestoreDisclosureExposure None { get; } =
        new(false, 0, CovenantDisclosureCountKind.Exact);

}

/// <summary>
/// Everything a restore learned before it was allowed to mutate anything. <c>--dry-run</c> returns
/// exactly this plan, so a rehearsal and a real restore agree on scope, capacity, and refusals.
/// </summary>
public sealed record BackupRestorePlan(
    DateTimeOffset GeneratedAt,
    string ArchivePath,
    int FormatVersion,
    BackupRestoreConflictMode ConflictMode,
    string DestinationRoot,
    BackupComponent[] Components,
    long Entries,
    long RestoredBytes,
    long RequiredBytes,
    long AvailableBytes,
    string SourceSchemaIdentity,
    string DestinationSchemaIdentity,
    bool SchemaMigrationRequired,
    Guid[] SelectedSessionIds,
    BackupRestoreMappingPlan[] PathMappings,
    string[] UnmappedNonportablePaths,
    bool RequiresConfirmation,
    bool SafetyBackupPlanned,
    string[] Warnings,
    BackupVerifyIssue[] Blockers,
    BackupProtectedStateMode ProtectedStateMode = BackupProtectedStateMode.Reject,
    BackupRestoreDisclosureExposure? DestinationDisclosure = null);

/// <summary>Post-commit reconciliation counts. Nothing here is allowed to silently skip a file.</summary>
public sealed record BackupRestoreReconciliation(
    long Attachments,
    long StaleAttachmentSources,
    long UploadedFiles,
    long BatchFiles,
    long EmbeddingsRebuilt,
    long PendingOperationsCleared,
    string[] Issues);

public sealed record BackupRestoreResult(
    BackupRestoreStatus Status,
    string ArchivePath,
    Guid OperationId,
    BackupRestoreConflictMode ConflictMode,
    string DestinationRoot,
    string? SafetyBackupPath,
    BackupRestorePlan Plan,
    BackupManifest? Manifest,
    BackupRestoreReconciliation? Reconciliation,
    BackupRestorePhaseRecord[] Phases,
    BackupVerifyIssue[] Issues);

public sealed record BackupMigrateRequest(
    string ArchivePath,
    string OutputPath,
    bool Overwrite = false);

public sealed record BackupMigrateResult(
    bool Migrated,
    string ArchivePath,
    string? OutputPath,
    int SourceFormatVersion,
    int OutputFormatVersion,
    long OutputBytes,
    BackupManifest? Manifest,
    BackupVerifyIssue[] Issues);

public interface IBackupRestoreService
{

    /// <summary>
    /// Reads, authenticates, and validates the archive against this machine without mutating
    /// anything, then reports the plan a real restore would execute.
    /// </summary>
    Task<BackupRestorePlan> PlanAsync(
        BackupRestoreRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken = default);

    Task<BackupRestoreResult> RestoreAsync(
        BackupRestoreRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites a supported archive at the current format through the authoritative codec. The
    /// source archive is never modified.
    /// </summary>
    Task<BackupMigrateResult> MigrateAsync(
        BackupMigrateRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken = default);

}
