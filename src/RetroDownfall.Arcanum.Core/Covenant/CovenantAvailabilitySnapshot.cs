using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// How usable one Covenant tier is right now.
/// </summary>
/// <remarks>
/// <see cref="Healthy"/> means the tier is authoritative for its owned work, <see cref="Degraded"/>
/// means it remains diagnosable but cannot serve every owned operation, and
/// <see cref="Unavailable"/> means callers cannot use it at all. No member is zero: a zero would be
/// indistinguishable from a default-initialized field, and "accidentally healthy" is the one wrong
/// answer this type must never give.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantCapabilityState>))]
public enum CovenantCapabilityState : byte
{
    Unavailable = 1,
    Degraded = 2,
    Healthy = 3
}

/// <summary>
/// Whether the FTS5 inspection index may be trusted to answer a query.
/// </summary>
/// <remarks>
/// <see cref="Synchronized"/> requires the complete applied dataset, search, and Campaign-deletion
/// tuple plus a rank-1 integrity proof. Anything less is <see cref="Dirty"/>, which selects the
/// canonical fallback rather than returning stale text - including text belonging to a Campaign that
/// has since been deleted.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantFtsSynchronizationState>))]
public enum CovenantFtsSynchronizationState : byte
{
    Unavailable = 1,
    Dirty = 2,
    Synchronized = 3
}

/// <summary>
/// Which publisher produced a snapshot. A caller cannot substitute another category or a free-form
/// reason, so "who last changed Covenant availability, and why" is always answerable.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantHealthTransition>))]
public enum CovenantHealthTransition : byte
{
    Bootstrap = 1,
    SchemaRepair = 2,
    CanonicalMutation = 3,
    OwnerCleanup = 4,
    AcceleratorSynchronization = 5,
    AcceleratorRebuild = 6,
    Reset = 7,
    Restore = 8,
    FamilyReinitialize = 9,
    FeatureConfiguration = 10,
    SchemaEvolution = 11
}

/// <summary>
/// One complete, immutable view of Covenant availability and the cursors a hot-path gate compares.
/// </summary>
/// <remarks>
/// Published by swapping the whole record at once, so no reader can observe a mix of old and new
/// fields - for example a fresh dataset generation beside a stale applied sequence, which would let
/// a gate conclude the accelerator was current when it had just been reset.
///
/// <para>Every field is content-free. No SQL, path, exception text, object DDL, or secret-derived
/// fingerprint enters this snapshot, because it reaches diagnostics and operator surfaces.</para>
/// </remarks>
public sealed record CovenantAvailabilitySnapshot(
    long Generation,
    bool FeatureEnabled,
    CovenantCapabilityState Canonical,
    int? CanonicalSchemaVersion,
    string? CanonicalInstalledFingerprint,
    CovenantCapabilityState Accelerator,
    int? AcceleratorSchemaVersion,
    string? AcceleratorInstalledFingerprint,
    Guid? DatasetGeneration,
    long CanonicalSequence,
    long CoreCampaignDeletionSequence,
    Guid? AppliedDatasetGeneration,
    long? AppliedSequence,
    long? AppliedCampaignDeletionSequence,
    ulong AcceleratorEpoch,
    CovenantFtsSynchronizationState FtsSynchronization,
    bool RebuildRequired,
    CovenantHealthTransition LastHealthTransition,
    string? CanonicalDiagnosticCode,
    string? AcceleratorDiagnosticCode);
