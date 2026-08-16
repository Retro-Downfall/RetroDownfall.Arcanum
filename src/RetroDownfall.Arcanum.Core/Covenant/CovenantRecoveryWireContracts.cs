using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The three things schema repair is permitted to do.
/// </summary>
/// <remarks>
/// Closed and numbered because the code is persisted in <c>covenant_schema_repair_intents</c> and
/// compared after a restart, and its numeric values are part of that table's CHECK constraint.
/// Nothing here repairs a missing trigger: writes may have occurred while its invariant was absent,
/// so restoring the trigger would restore the guarantee without restoring the data it protected
/// (§10.16).
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantSchemaRepairAction>))]
public enum CovenantSchemaRepairAction : byte
{

    /// <summary>Install a wholly absent canonical family that has no metadata and no data.</summary>
    InstallAbsentCanonicalFamily = 1,

    /// <summary>Rerun the idempotent installer over an existing, validated family.</summary>
    RepairExistingFamily = 2,

    /// <summary>Recreate one missing ordinary index after full table-constraint validation.</summary>
    RepairOrdinaryIndex = 3,

}

/// <summary>
/// The durable phases of one schema-repair intent.
/// </summary>
/// <remarks>
/// Persisted in the always-present core journal, so the numeric codes are part of that table's CHECK
/// constraint. The proven no-mutation path is <see cref="Prepared"/> to <see cref="ReopenPending"/>
/// to <see cref="Abandoned"/>; only the one-shot post-disposition finalizer may advance
/// <see cref="ReopenPending"/>, which is what stops a journal from recording a terminal phase nobody
/// proved.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantSchemaRepairPhase>))]
public enum CovenantSchemaRepairPhase : byte
{

    Prepared = 1,

    CatalogCommitted = 2,

    HealthVerified = 3,

    ReopenPending = 4,

    Completed = 5,

    Abandoned = 6,

}

/// <summary>
/// How a completed repair left the installation.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantSchemaRepairDisposition>))]
public enum CovenantSchemaRepairDisposition : byte
{

    AlreadyHealthy = 1,

    Repaired = 2,

    ManualRecoveryRequired = 3,

}

/// <summary>
/// The closed phases of a Covenant family reinitialize.
/// </summary>
/// <remarks>
/// Ordered, closed, and persisted. Reinitialize closes the database and drops a whole schema family,
/// so recovery cannot infer where it got to by looking at the result: a half-dropped family looks the
/// same whether this process dropped it or found it that way.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantFamilyReinitializePhase>))]
public enum CovenantFamilyReinitializePhase : byte
{

    Planned = 1,

    AdmissionClosed = 2,

    LocalArtifactsProcessed = 3,

    HandlesClosed = 4,

    FamilyDropped = 5,

    DatabaseCompacted = 6,

    CanonicalInstalled = 7,

    AcceleratorInstalled = 8,

    FinalWalTruncated = 9,

    SidecarsVerified = 10,

    ReopenedVerified = 11,

}

/// <summary>
/// One catalog defect a reinitialize plan found, named without disclosing anything about content.
/// </summary>
public sealed record CovenantCatalogDefectDto(
    string ObjectName,
    string DefectCode);

/// <summary>
/// One core family a reinitialize preserves, with the row count it will still hold afterwards.
/// </summary>
public sealed record CovenantPreservedFamilyDto(
    string FamilyName,
    long RowCount);

/// <summary>
/// Everything a reinitialize would destroy, everything it would keep, and the token that binds the
/// plan to the catalog it was computed from.
/// </summary>
/// <remarks>
/// It returns no Covenant content at all — counts, defects, and identities only. An operator
/// confirming the most destructive Covenant operation needs to know how much is being lost, and
/// showing them what is being lost would make the confirmation dialog the largest single disclosure
/// the feature has.
/// </remarks>
public sealed record CovenantFamilyReinitializePlanDto(
    Guid OperationId,
    CovenantCatalogDefectDto[] Defects,
    long EntryCount,
    long VersionCount,
    long TaintedArtifactCount,
    long TaintedManagedFileCount,
    long NonrevocableDisclosureCount,
    CovenantPreservedFamilyDto[] PreservedFamilies,
    long RequiredFreeSpaceBytes,
    string InspectedCatalogFingerprint,
    string DatabaseFileIdentityDigest,
    string EffectDigest,
    string ApplyRequestDigest,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Token);

/// <summary>
/// The outcome of a schema repair.
/// </summary>
public sealed record CovenantSchemaRepairResultDto(
    CovenantSchemaRepairDisposition Disposition,
    CovenantSchemaRepairAction Action,
    CovenantSchemaRepairPhase Phase,
    bool CanonicalAvailable,
    bool AcceleratorAvailable,
    long CapabilityGeneration,
    string InspectedCatalogFingerprint,
    string? BlockingCode);

/// <summary>
/// A request to rerun the idempotent optional installer under the exclusive Covenant operation gate.
/// </summary>
/// <remarks>
/// Available while Covenant is disabled or canonically degraded, which is the only time it is useful.
/// An unknown action is <c>Covenant.ManualRecoveryRequired</c> rather than a validation failure: a
/// caller asking for a repair this build does not implement is asking for something only an operator
/// with the release notes can resolve.
/// </remarks>
public sealed record CovenantSchemaRepairRequest(CovenantSchemaRepairAction Action)
{

    public Result Validate() =>
        Action is CovenantSchemaRepairAction.InstallAbsentCanonicalFamily
            or CovenantSchemaRepairAction.RepairExistingFamily
            or CovenantSchemaRepairAction.RepairOrdinaryIndex
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This build does not implement the requested Covenant schema repair.");

}

/// <summary>
/// A request to discard and rebuild the derived inspection index.
/// </summary>
public sealed record CovenantIndexRebuildRequest(Guid OperationId)
{

    public Result Validate() =>
        CovenantWireValidation.RequireIdentity(OperationId, "client-generated operation identity");

}

/// <summary>
/// A request to plan a Covenant family reinitialize.
/// </summary>
public sealed record CovenantFamilyReinitializePrepareRequest(Guid OperationId)
{

    public Result Validate() =>
        CovenantWireValidation.RequireIdentity(OperationId, "client-generated operation identity");

}

/// <summary>
/// The apply half of a reinitialize: the operation identity, the stable digest of the request it is
/// applying, and the token the plan issued.
/// </summary>
/// <remarks>
/// The digest is stable across restart, boot salt, timestamps, and key version, which is what makes
/// receipt-first replay work. Apply looks up a terminal receipt or durable operation by operation ID
/// before it looks at the token at all: an exact stored digest replays or resumes the already-admitted
/// operation, a different digest is an idempotency conflict, and an expired token with no durable
/// operation is a re-prepare rather than an authority grant.
/// </remarks>
public sealed record CovenantFamilyReinitializeApplyRequest(
    Guid OperationId,
    string ApplyRequestDigest,
    string Token)
{

    public Result Validate() =>
        CovenantWireValidation.First(
            CovenantWireValidation.RequireIdentity(OperationId, "client-generated operation identity"),
            CovenantWireValidation.ValidateApplyRequestDigest(ApplyRequestDigest),
            CovenantWireValidation.ValidateToken(Token));

}
