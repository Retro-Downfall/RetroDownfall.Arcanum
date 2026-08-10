using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<RetentionDataClass>))]
public enum RetentionDataClass
{

    ActiveSessions,

    ArchivedSessions,

    Entries,

    AttachmentVersions,

    AttachmentBytes,

    AttachmentChunks,

    AttachmentEmbeddings,

    UploadedFiles,

    BatchInputFiles,

    BatchOutputFiles,

    BatchErrorFiles,

    CompletedBatches,

    SagaMemories,

    LexiconEntries,

    WorkspaceChunks,

    WorkspaceEmbeddings,

    SessionEntryEmbeddings,

    Tapestry,

    AuditLogs,

    GuardrailLogs,

    IdempotencyClaims,

    InferenceRuns,

    BillableOperations,

    BudgetReservations,

    CostAdjustments,

    LongRunningOperations,

    SanctumBreaches,

    DaemonExecutions,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<DataRetentionOperation>))]
public enum DataRetentionOperation
{

    Prune,

    DeleteSession,

    DeleteAttachment,

    ResetMemory,

    FactoryReset,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryResetScope>))]
public enum MemoryResetScope
{

    Entry,

    Attachments,

    Workspace,

    Saga,

    Lexicon,

}

public sealed record DataRetentionRequest(
    [property: JsonRequired] DataRetentionOperation Operation = DataRetentionOperation.Prune,
    Guid? TargetId = null,
    MemoryResetScope? MemoryScope = null);

public sealed record DataRetentionApplyRequest(
    [property: JsonRequired] DataRetentionRequest Request,
    string? ExpectedPlanId = null);

public sealed record RetentionRuleUpdateRequest(
    [property: JsonRequired] string DataClass,
    [property: JsonRequired] bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Days = null);

public sealed record MemoryResetRequest(
    [property: JsonRequired] MemoryResetScope Scope);

public sealed record FactoryResetRequest(
    [property: JsonRequired] string Confirmation);

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
    string[] PreservedOutsideSelectedRoot);

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
    bool RequiresConfirmation);

public sealed record DataRetentionApplyResult(
    Guid OperationId,
    string PlanId,
    long RowsDeleted,
    long FilesDeleted,
    long EstimatedBytesDeleted,
    long DerivedRecordsDeleted,
    bool Reconciled,
    DataRetentionBlocker[] Blockers,
    DataRetentionConflict[] Conflicts);

public interface IDataRetentionService
{

    Task<DataRetentionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    Task<DataRetentionPlan> PlanAsync(
        DataRetentionRequest request,
        CancellationToken cancellationToken = default);

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

            _ => null,

        };

}
