using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

/// <summary>
/// One inventoried class of Arcanum-owned persistent data.
/// </summary>
/// <remarks>
/// Every code is written literally because the value is persisted — retention policy rows and durable
/// operation checkpoints both store it — so reordering or removing a member repoints an existing row
/// at a different class. A rule an operator wrote for <see cref="SagaMemories"/> would silently begin
/// deleting something else, which is the one failure a retention enum must never have.
///
/// <para><see cref="Covenant"/> is inventoried and never swept. It has no
/// <see cref="Core.Configuration.RetentionSettings"/> property and
/// <see cref="DataRetentionSettingsCatalog.ResolveRule"/> resolves it to null, because its versions,
/// heads, provenance, tombstones, and disclosure receipts are the evidence that makes an erasure claim
/// checkable. A rule that could age them out would destroy the only record of what was destroyed
/// (§10.20.1).</para>
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<RetentionDataClass>))]
public enum RetentionDataClass
{

    ActiveSessions = 0,

    ArchivedSessions = 1,

    Entries = 2,

    AttachmentVersions = 3,

    AttachmentBytes = 4,

    AttachmentChunks = 5,

    AttachmentEmbeddings = 6,

    UploadedFiles = 7,

    BatchInputFiles = 8,

    BatchOutputFiles = 9,

    BatchErrorFiles = 10,

    CompletedBatches = 11,

    SagaMemories = 12,

    LexiconEntries = 13,

    WorkspaceChunks = 14,

    WorkspaceEmbeddings = 15,

    SessionEntryEmbeddings = 16,

    Tapestry = 17,

    AuditLogs = 18,

    GuardrailLogs = 19,

    IdempotencyClaims = 20,

    InferenceRuns = 21,

    BillableOperations = 22,

    BudgetReservations = 23,

    CostAdjustments = 24,

    LongRunningOperations = 25,

    SanctumBreaches = 26,

    DaemonExecutions = 27,

    /// <summary>The Covenant family. Inventoried, never aged out.</summary>
    Covenant = 28,

    /// <summary>
    /// The Annals. Inventoried, never aged out on its own timer: a claim's lifecycle is its subject's.
    /// </summary>
    Annals = 29,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<DataRetentionOperation>))]
public enum DataRetentionOperation
{

    Prune,

    DeleteSession,

    DeleteAttachment,

    ResetMemory,

    ResetWorkspace,

    FactoryReset,

}

/// <summary>
/// The one named store a memory reset clears. There is deliberately no generic "all memory" arm.
/// </summary>
/// <remarks>
/// Literal codes for the same reason <see cref="RetentionDataClass"/> has them: the value reaches a
/// durable operation checkpoint, and a renumbered member would let a resumed reset clear a store the
/// operator never selected.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryResetScope>))]
public enum MemoryResetScope
{

    Entry = 0,

    Attachments = 1,

    Workspace = 2,

    Saga = 3,

    Lexicon = 4,

    /// <summary>
    /// The Covenant family. Inventoried and planned here; apply enters the exclusive erasure
    /// coordinator rather than the ordinary memory-reset executor (§10.20.1).
    /// </summary>
    Covenant = 5,

}

public sealed record DataRetentionRequest(
    [property: JsonRequired] DataRetentionOperation Operation = DataRetentionOperation.Prune,
    Guid? TargetId = null,
    MemoryResetScope? MemoryScope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionWorkspaceBinding? Workspace = null);

public sealed record DataRetentionWorkspaceBinding(
    [property: JsonRequired] Guid CampaignId,
    [property: JsonRequired] string WorkspaceRoot);

public sealed record DataRetentionApplyRequest(
    [property: JsonRequired] DataRetentionRequest Request,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedPlanId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RequestedOperationId = null);

public sealed record RetentionRuleUpdateRequest(
    [property: JsonRequired] string DataClass,
    [property: JsonRequired] bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Days = null);

/// <remarks>
/// <paramref name="CampaignId"/> narrows the reset to the memories one Campaign owns, leaving every
/// other Campaign's and every installation-scoped memory in place. Only the two stores that carry an
/// owning Campaign - <see cref="MemoryResetScope.Saga"/> and <see cref="MemoryResetScope.Lexicon"/> -
/// accept it; naming it for any other store is refused rather than silently widened to all of it.
/// </remarks>
public sealed record MemoryResetRequest(
    [property: JsonRequired] MemoryResetScope Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedPlanId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? CampaignId = null);

public sealed record FactoryResetRequest(
    [property: JsonRequired] string Confirmation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedPlanId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RequestedOperationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    InstallationResetHostHandoff? InstallationResetHandoff = null);

/// <summary>
/// The content-free installation inventory of the Covenant family.
/// </summary>
/// <remarks>
/// Counts only. No subject text, key, digest preimage, path, or Campaign identity appears here,
/// because <c>data status</c> and a retention rehearsal are both readable by anything that can reach
/// the route and neither has any business carrying protected content (§10.20.1).
///
/// <para><paramref name="DisclosureCountKind"/> is carried rather than resolved away for the reason
/// §10.18 gives: a joined bucket is a lower bound, and reporting a lower bound as exact would
/// understate exposure in exactly the report an operator uses to decide. Only nonrevocable buckets
/// are folded — a locally revocable effect is one Arcanum can still undo, and including it would
/// inflate the number.</para>
/// </remarks>
public sealed record DataRetentionCovenantInventory(
    long Rows,
    long ManagedFiles,
    long LocalArtifacts,
    long AffectedSessions,
    long PossibleDisclosures,
    CovenantDisclosureCountKind DisclosureCountKind);

public sealed record DataRetentionStatusItem(
    RetentionDataClass DataClass,
    long Rows,
    long Files,
    long EstimatedBytes,
    bool PolicyEnabled,
    int? RetentionDays,
    string Store,
    string Provenance);

public sealed record DataRetentionStatus(
    DateTimeOffset GeneratedAt,
    DataRetentionStatusItem[] Items,
    long Rows,
    long Files,
    long EstimatedBytes,
    string[] PreservedOutsideSelectedRoot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionCovenantInventory? Covenant = null);

public sealed record DataRetentionPlanItem(
    RetentionDataClass DataClass,
    long Rows,
    long Files,
    long EstimatedBytes,
    long DerivedRecords);

public sealed record DataRetentionBlocker(
    RetentionDataClass DataClass,
    string ResourceId,
    string ReasonCode,
    string Message);

public sealed record DataRetentionConflict(
    string Code,
    string ResourceId,
    string Message);

public sealed record DataRetentionPlan(
    string PlanId,
    DataRetentionRequest Request,
    DateTimeOffset GeneratedAt,
    DataRetentionPlanItem[] Items,
    DataRetentionBlocker[] Blockers,
    DataRetentionConflict[] Conflicts,
    long Rows,
    long Files,
    long EstimatedBytes,
    long DerivedRecords,
    string[] CandidateIds,
    bool RequiresConfirmation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionCovenantInventory? Covenant = null);

/// <summary>
/// A plan and the optional Covenant read lease that protects its response until serialization ends.
/// </summary>
/// <remarks>
/// The lease is transferred to the HTTP result, which revalidates it immediately before writing and
/// disposes it after the final byte. Callers that only need a plan use <see cref="IDataRetentionService.PlanAsync"/>,
/// which preserves the legacy ownership boundary by disposing any lease before it returns.
/// </remarks>
public sealed record DataRetentionPlanAdmission(
    DataRetentionPlan Plan,
    ICovenantSnapshotReadLease? ReadLease);

/// <summary>
/// The Covenant capability a plan-admission caller needs held through its response.
/// </summary>
public enum DataRetentionPlanAdmissionCapability
{

    Request = 0,

    Installation = 1,

}

public sealed record DataRetentionApplyResult(
    Guid OperationId,
    string PlanId,
    long RowsDeleted,
    long FilesDeleted,
    long EstimatedBytesDeleted,
    long DerivedRecordsDeleted,
    bool Reconciled,
    DataRetentionBlocker[] Blockers,
    DataRetentionConflict[] Conflicts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RequestedOperationId = null);

public interface IDataRetentionService
{

    Task<DataRetentionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    Task<DataRetentionPlan> PlanAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DataRetentionPlanAdmission>> PlanAdmissionAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken = default,
        DataRetentionPlanAdmissionCapability capability = DataRetentionPlanAdmissionCapability.Request);

    Task<Result<DataRetentionApplyResult>> ApplyAsync(
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken = default);

}

public static class DataRetentionSettingsCatalog
{

    public static RetentionRuleSettings? ResolveRule(
        RetentionSettings settings,
        RetentionDataClass dataClass) =>
        dataClass switch
        {

            RetentionDataClass.ActiveSessions => settings.ActiveSessions,

            RetentionDataClass.ArchivedSessions => settings.ArchivedSessions,

            RetentionDataClass.Entries => settings.Entries,

            RetentionDataClass.AttachmentVersions
                or RetentionDataClass.AttachmentBytes
                or RetentionDataClass.AttachmentChunks
                or RetentionDataClass.AttachmentEmbeddings => settings.Attachments,

            RetentionDataClass.UploadedFiles
                or RetentionDataClass.BatchInputFiles
                or RetentionDataClass.BatchOutputFiles
                or RetentionDataClass.BatchErrorFiles => settings.UploadedFiles,

            RetentionDataClass.CompletedBatches => settings.CompletedBatches,

            RetentionDataClass.SagaMemories => settings.SagaMemories,

            RetentionDataClass.LexiconEntries => settings.LexiconEntries,

            RetentionDataClass.WorkspaceChunks
                or RetentionDataClass.WorkspaceEmbeddings => settings.WorkspaceIndexes,

            RetentionDataClass.SessionEntryEmbeddings => settings.SessionEntryEmbeddings,

            RetentionDataClass.AuditLogs => settings.AuditLogs,

            RetentionDataClass.GuardrailLogs => settings.GuardrailLogs,

            RetentionDataClass.IdempotencyClaims => settings.IdempotencyClaims,

            RetentionDataClass.InferenceRuns
                or RetentionDataClass.BillableOperations
                or RetentionDataClass.BudgetReservations
                or RetentionDataClass.CostAdjustments => settings.Accounting,

            RetentionDataClass.LongRunningOperations => settings.LongRunningOperations,

            RetentionDataClass.SanctumBreaches => settings.SanctumBreaches,

            RetentionDataClass.DaemonExecutions => settings.DaemonHistory,

            // Explicit, not a fall-through. The Covenant family is inventoried and never aged out, and
            // an arm that says so cannot be mistaken for a rule somebody forgot to wire.
            RetentionDataClass.Covenant => null,

            // Explicit for the same reason. A claim's lifecycle is its subject's, and a rule that could
            // age one out from under a live memory would leave that memory unexplained.
            RetentionDataClass.Annals => null,

            _ => null,

        };

}
