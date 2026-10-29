using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Primitives;

public readonly record struct Error(
    string Code,
    string Message,
    IReadOnlyList<ConfigurationValidationError>? Details = null)
{
    /// <summary>
    /// W4.1: a defensive, non-downcastable read-only copy of the supplied details. Returning a plain
    /// <c>List&lt;&gt;</c> as <see cref="IReadOnlyList{T}"/> let a consumer cast it back and mutate the
    /// error after construction; a <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/>
    /// closes that. (Note: record-struct equality still compares this by reference; full structural
    /// equality is out of scope — <c>Details</c> is rarely set and <see cref="None"/> carries null.)
    /// </summary>
    public IReadOnlyList<ConfigurationValidationError>? Details { get; init; } =
        Details is null ? null : System.Array.AsReadOnly<ConfigurationValidationError>([.. Details]);

    /// <summary>In-process, content-free context admission data; never part of the error wire shape.</summary>
    [JsonIgnore]
    public ContextOverflowDetails? ContextOverflow { get; init; }

    /// <summary>Closed, content-free connection diagnostics; never part of the error wire shape.</summary>
    [JsonIgnore]
    public GrimoireConnectionFailureDetails? ConnectionFailure { get; init; }

    /// <summary>Safe startup-owner refusal facts; never part of the error wire shape.</summary>
    [JsonIgnore]
    public CovenantStartupFailureDetails? CovenantStartupFailure { get; init; }

    /// <summary>Validated erasure inventory facts; never part of the error wire shape.</summary>
    [JsonIgnore]
    public CovenantInventoryFailureDetails? CovenantInventoryFailure { get; init; }

    /// <summary>Closed marker refusal observations; no capability or persisted/wire state.</summary>
    [JsonIgnore]
    public CampaignMarkerFailureDetails? CampaignMarkerFailure { get; init; }

    /// <summary>Existing durable retention correlation only; excluded from the error wire shape.</summary>
    [JsonIgnore]
    public Guid? RetentionOperationId { get; init; }

    public static readonly Error None = new(string.Empty, string.Empty);

    /// <summary>
    /// Whether this error has exactly the field values of <see cref="None"/>: empty code, empty
    /// message, no details, context overflow, connection failure, startup failure, inventory failure, marker failure or retention correlation. It is the same
    /// answer as <c>this == None</c>, read from the fields so a caller never runs the generated record comparison, which would compare
    /// <see cref="Details"/> through its interface.
    /// </summary>
    [JsonIgnore]
    public bool IsNone => Code is { Length: 0 } && Message is { Length: 0 } && Details is null && ContextOverflow is null && ConnectionFailure is null && CovenantStartupFailure is null && CovenantInventoryFailure is null && CampaignMarkerFailure is null && RetentionOperationId is null;
}

/// <summary>Observation only: no value grants authority or changes marker recovery policy.</summary>
public enum CampaignMarkerFailureStage
{
    Unknown,
    RestorePreparation,
    RestartProof,
    FullResetInventory,
    FullResetRevalidation,
    FullResetPreparation,
    FullResetReconciliation,
    RootRelease,
}

/// <summary>Closed refusal facts; no collaborator text, path, digest, or exception data.</summary>
public enum CampaignMarkerFailureReason
{
    Unknown,
    RootUnavailable,
    PhysicalIdentityMismatch,
    OwnershipMismatch,
    InvalidFacts,
    DuplicateCampaign,
    ExistingRootKeyUnavailable,
    RootIdentificationUnproved,
    MarkerOwnershipUnproved,
    RetainedRootUnavailable,
    InventoryInvalid,
    InventoryChanged,
    AuthorityUnproved,
    ChildVectorMismatch,
    ReceiptMismatch,
    JournalReadRefused,
    JournalWriteRefused,
    ObservationUnproved,
    OperationFailed,
    MarkerLifecycleRefused,
    RootReleaseFailed,
}

/// <summary>Existing public correlation GUIDs and closed reasons, excluded from Error serialization.</summary>
public readonly record struct CampaignMarkerFailureDetails(
    CampaignMarkerFailureStage Stage,
    CampaignMarkerFailureReason Reason,
    Guid? OperationId = null,
    Guid? CampaignId = null)
{
    /// <summary>Unknown values fail closed to fixed observations; empty GUIDs are not correlations.</summary>
    public CampaignMarkerFailureDetails Validated() => new(
        Stage switch
        {
            CampaignMarkerFailureStage.RestorePreparation => CampaignMarkerFailureStage.RestorePreparation,
            CampaignMarkerFailureStage.RestartProof => CampaignMarkerFailureStage.RestartProof,
            CampaignMarkerFailureStage.FullResetInventory => CampaignMarkerFailureStage.FullResetInventory,
            CampaignMarkerFailureStage.FullResetRevalidation => CampaignMarkerFailureStage.FullResetRevalidation,
            CampaignMarkerFailureStage.FullResetPreparation => CampaignMarkerFailureStage.FullResetPreparation,
            CampaignMarkerFailureStage.FullResetReconciliation => CampaignMarkerFailureStage.FullResetReconciliation,
            CampaignMarkerFailureStage.RootRelease => CampaignMarkerFailureStage.RootRelease,
            _ => CampaignMarkerFailureStage.Unknown,
        },
        Reason switch
        {
            CampaignMarkerFailureReason.RootUnavailable => CampaignMarkerFailureReason.RootUnavailable,
            CampaignMarkerFailureReason.PhysicalIdentityMismatch => CampaignMarkerFailureReason.PhysicalIdentityMismatch,
            CampaignMarkerFailureReason.OwnershipMismatch => CampaignMarkerFailureReason.OwnershipMismatch,
            CampaignMarkerFailureReason.InvalidFacts => CampaignMarkerFailureReason.InvalidFacts,
            CampaignMarkerFailureReason.DuplicateCampaign => CampaignMarkerFailureReason.DuplicateCampaign,
            CampaignMarkerFailureReason.ExistingRootKeyUnavailable => CampaignMarkerFailureReason.ExistingRootKeyUnavailable,
            CampaignMarkerFailureReason.RootIdentificationUnproved => CampaignMarkerFailureReason.RootIdentificationUnproved,
            CampaignMarkerFailureReason.MarkerOwnershipUnproved => CampaignMarkerFailureReason.MarkerOwnershipUnproved,
            CampaignMarkerFailureReason.RetainedRootUnavailable => CampaignMarkerFailureReason.RetainedRootUnavailable,
            CampaignMarkerFailureReason.InventoryInvalid => CampaignMarkerFailureReason.InventoryInvalid,
            CampaignMarkerFailureReason.InventoryChanged => CampaignMarkerFailureReason.InventoryChanged,
            CampaignMarkerFailureReason.AuthorityUnproved => CampaignMarkerFailureReason.AuthorityUnproved,
            CampaignMarkerFailureReason.ChildVectorMismatch => CampaignMarkerFailureReason.ChildVectorMismatch,
            CampaignMarkerFailureReason.ReceiptMismatch => CampaignMarkerFailureReason.ReceiptMismatch,
            CampaignMarkerFailureReason.JournalReadRefused => CampaignMarkerFailureReason.JournalReadRefused,
            CampaignMarkerFailureReason.JournalWriteRefused => CampaignMarkerFailureReason.JournalWriteRefused,
            CampaignMarkerFailureReason.ObservationUnproved => CampaignMarkerFailureReason.ObservationUnproved,
            CampaignMarkerFailureReason.OperationFailed => CampaignMarkerFailureReason.OperationFailed,
            CampaignMarkerFailureReason.MarkerLifecycleRefused => CampaignMarkerFailureReason.MarkerLifecycleRefused,
            CampaignMarkerFailureReason.RootReleaseFailed => CampaignMarkerFailureReason.RootReleaseFailed,
            _ => CampaignMarkerFailureReason.Unknown,
        },
        OperationId is { } operation && operation != Guid.Empty ? operation : null,
        CampaignId is { } campaign && campaign != Guid.Empty ? campaign : null);

    public string ToPublicMessage()
    {
        CampaignMarkerFailureDetails safe = Validated();

        return $"Campaign marker refusal at {safe.Stage}: {safe.Reason}; operation {safe.OperationId?.ToString("D") ?? "unknown"}, Campaign {safe.CampaignId?.ToString("D") ?? "unknown"}. Preserve the installation profile, marker and recovery journal. Restore access to the original Campaign root and required installation key where applicable, then retry the recovery operation.";
    }
}

/// <summary>Only configured public identities and numeric context-window accounting, never prompt or response text.</summary>
public readonly record struct ContextOverflowDetails(
    string Provider,
    string Model,
    int InputTokens,
    int ReservedTokens,
    int TotalTokens,
    int WindowTokens)
{
    public const string Remedy = "Shorten the prompt, run arcanum session compact, start a new Session, choose a model with a larger context window, or raise providers[].contextWindowLimit to the model's actual capacity.";

    public string ToPublicMessage() => FormattableString.Invariant(
        $"Context budget exceeded for provider '{PublicIdentifier(Provider, 64)}', model '{PublicIdentifier(Model, 128)}': {TotalTokens} accounted tokens ({InputTokens} input + {ReservedTokens} reserved) exceed the {WindowTokens}-token window. {Remedy}");

    private static string PublicIdentifier(string value, int maxUtf8Bytes)
    {
        string bounded = Utf8Truncation.TruncateToUtf8ByteBudget(Utf8Truncation.NormalizeInvalidUtf16(value ?? string.Empty), maxUtf8Bytes);

        return string.Concat(bounded.EnumerateRunes().Select(static scalar => Rune.IsControl(scalar)
            || Rune.GetUnicodeCategory(scalar) is System.Globalization.UnicodeCategory.Format
            || scalar.Value is 0x2028 or 0x2029 or 0x27 ? "_" : scalar.ToString()));
    }
}

/// <summary>Shared operator guidance for every enforced daily budget refusal.</summary>
public static class BudgetErrorMessages
{
    /// <summary>Explains the exact proposed exposure in the UTC ledger period being refused.</summary>
    public static string WouldExceed(string period, decimal limit, decimal current, decimal additional,
        string additionalLabel, decimal projected, decimal? replacedReservation = null) =>
        $"Daily budget limit of ${FormatUsd(limit)} USD for UTC day {period} would be exceeded "
        + $"(committed+reserved: ${FormatUsd(current)} USD; "
        + (replacedReservation is { } replaced ? $"replacing reservation: ${FormatUsd(replaced)} USD; " : string.Empty)
        + $"{additionalLabel}: ${FormatUsd(additional)} USD; projected: ${FormatUsd(projected)} USD). " + Guidance;

    /// <summary>Formats USD invariantly with at least cents and every significant decimal place.</summary>
    public static string FormatUsd(decimal value) =>
        value.ToString("0.00##########################", System.Globalization.CultureInfo.InvariantCulture);

    public const string Guidance = "The budget day is UTC; a new accounting day starts at 00:00 UTC. Outstanding reservations from prior days may still count. Review Arcanum:Cost:Budget:DailyLimitUsd and raise it if appropriate.";
}

/// <summary>The fixed stages at which opening an admitted Grimoire connection can fail.</summary>
public enum GrimoireConnectionFailureStage
{
    NativeRuntime,
    Admission,
    ProviderConstruction,
    Open,
    Initialization,
    TicketResolution,
}

/// <summary>Closed exception classifications; no collaborator text or loader paths.</summary>
public enum GrimoireConnectionFailureKind
{
    Other,
    Sqlite,
    NativeRuntime,
    InvalidOperation,
    IO,
    UnauthorizedAccess,
    Argument,
}

/// <summary>Only fixed classifications and numeric SQLite codes may cross the storage boundary.</summary>
public readonly record struct GrimoireConnectionFailureDetails(
    GrimoireConnectionFailureStage Stage,
    GrimoireConnectionFailureKind Kind,
    int? SqliteErrorCode = null,
    int? SqliteExtendedErrorCode = null)
{
    public string ToPublicMessage()
    {
        string stage = Stage switch
        {
            GrimoireConnectionFailureStage.NativeRuntime => "native runtime",
            GrimoireConnectionFailureStage.Admission => "admission",
            GrimoireConnectionFailureStage.ProviderConstruction => "provider construction",
            GrimoireConnectionFailureStage.Open => "open",
            GrimoireConnectionFailureStage.Initialization => "initialization",
            GrimoireConnectionFailureStage.TicketResolution => "admission ticket resolution",
            _ => "connection",
        };

        string kind = Kind switch
        {
            GrimoireConnectionFailureKind.Sqlite => "SqliteException",
            GrimoireConnectionFailureKind.NativeRuntime => "SqliteNativeRuntimeUnavailableException; Grimoire.NativeRuntimeUnavailable",
            GrimoireConnectionFailureKind.InvalidOperation => "InvalidOperationException",
            GrimoireConnectionFailureKind.IO => "IOException",
            GrimoireConnectionFailureKind.UnauthorizedAccess => "UnauthorizedAccessException",
            GrimoireConnectionFailureKind.Argument => "ArgumentException",
            _ => "Exception",
        };

        return Kind == GrimoireConnectionFailureKind.Sqlite
            ? FormattableString.Invariant($"Grimoire {stage} failed: {kind} (SQLite {SqliteErrorCode}, extended {SqliteExtendedErrorCode}).")
            : $"Grimoire {stage} failed: {kind}.";
    }
}

/// <summary>Closed reasons why startup cannot safely adopt a durable Covenant erasure owner.</summary>
public enum CovenantStartupFailureReason
{
    MalformedRow,
    PolicyMismatch,
    UnsupportedVersion,
    UnreadableCheckpoint,
    WrongOperation,
    SecondOwner,
    GateRefused,
    StorageFault,
}

/// <summary>Only a validated identity, closed classifications and numeric SQLite codes; no stored content.</summary>
public readonly record struct CovenantStartupFailureDetails(
    CovenantStartupFailureReason Reason,
    Guid? OperationId = null,
    GrimoireConnectionFailureKind? FailureKind = null,
    int? SqliteErrorCode = null,
    int? SqliteExtendedErrorCode = null)
{
    public string ToPublicMessage()
    {
        string identity = OperationId is { } id ? $" OperationId={id:N}." : string.Empty;

        string failure = FailureKind is { } kind ? $" Failure={kind}." : string.Empty;

        string sqlite = SqliteErrorCode is { } code
            ? FormattableString.Invariant($" SQLite={code}, extended={SqliteExtendedErrorCode}.")
            : string.Empty;

        return $"Durable Covenant erasure startup refused. Reason={Reason}.{identity}{failure}{sqlite} "
            + "Keep the installation intact. Run 'arcanum doctor --only operations --only grimoire --json' "
            + "for supplemental offline diagnostics; it does not repair ownership. "
            + "See Command Reference: Covenant startup recovery refusal.";
    }
}

/// <summary>Closed reasons an erasure inventory cannot prove its snapshot or artifact ownership.</summary>
public enum CovenantInventoryFailureReason
{
    UnsupportedRequest,
    InvalidDataset,
    SnapshotMismatch,
    MalformedLabel,
    UnknownArtifactKind,
    ManagedOwnershipMissing,
    ManagedOwnershipMismatch,
    StorageBusy,
    StorageUnavailable,
    StorageFault,
    AdmissionUnavailable,
    CleanupFailed,
    CapacityExceeded,
}

/// <summary>Only independently validated identifiers and a declared artifact kind may describe a refusal.</summary>
public readonly record struct CovenantInventoryFailureDetails(
    CovenantInventoryFailureReason Reason,
    Guid? LabelId = null,
    Guid? ArtifactId = null,
    RetroDownfall.Arcanum.Core.Covenant.SensitiveArtifactKind? ArtifactKind = null)
{
    /// <summary>Uses only SQLite's primary result code; missing schema is an integrity fault, never retryable busy.</summary>
    public static CovenantInventoryFailureDetails FromSqliteCode(int? sqliteCode) => new(SqliteStorageFailure.Classify(sqliteCode) switch
    {
        SqliteStorageFailureKind.Busy => CovenantInventoryFailureReason.StorageBusy,
        SqliteStorageFailureKind.Unavailable => CovenantInventoryFailureReason.StorageUnavailable,
        _ => CovenantInventoryFailureReason.StorageFault,
    });

    public Error ToError()
    {
        string code = Reason switch
        {
            CovenantInventoryFailureReason.StorageBusy or CovenantInventoryFailureReason.StorageUnavailable
                or CovenantInventoryFailureReason.AdmissionUnavailable => ErrorCodes.Covenant.Unavailable,
            CovenantInventoryFailureReason.ManagedOwnershipMissing or CovenantInventoryFailureReason.ManagedOwnershipMismatch
                => ErrorCodes.Covenant.ManualArtifactErasureRequired,
            CovenantInventoryFailureReason.CleanupFailed => ErrorCodes.Covenant.MaintenanceFailed,
            _ => ErrorCodes.Covenant.IntegrityFailure,
        };

        string label = LabelId is { } id && id != Guid.Empty ? $" LabelId={id:N}." : string.Empty;

        string artifact = ArtifactId is { } artifactId && artifactId != Guid.Empty ? $" ArtifactId={artifactId:N}." : string.Empty;

        string kind = ArtifactKind is { } artifactKind && Enum.IsDefined(artifactKind) ? $" ArtifactKind={artifactKind}." : string.Empty;

        string reason = Enum.IsDefined(Reason) ? Reason.ToString() : nameof(CovenantInventoryFailureReason.StorageFault);

        string guidance = Reason switch
        {
            CovenantInventoryFailureReason.StorageBusy => "Storage is busy. Retry after the conflicting work releases the database.",
            CovenantInventoryFailureReason.StorageUnavailable => "Check storage availability, permissions and free space, then retry.",
            CovenantInventoryFailureReason.AdmissionUnavailable => "Inventory admission is unavailable. Inspect the active operation before retrying.",
            CovenantInventoryFailureReason.CleanupFailed => "Snapshot cleanup did not complete. Preserve the installation and inspect the operation and logs before retrying.",
            CovenantInventoryFailureReason.ManagedOwnershipMissing or CovenantInventoryFailureReason.ManagedOwnershipMismatch
                => "Adopted ownership could not be proved. Preserve the artifact and inspect its ownership evidence; do not erase an unverified path.",
            _ => "Preserve the installation and inspect the named canonical evidence and diagnostics before retrying.",
        };

        return new Error(code, $"Covenant erasure inventory refused. Reason={reason}.{label}{artifact}{kind} {guidance}")
        {
            CovenantInventoryFailure = this,
        };
    }
}
