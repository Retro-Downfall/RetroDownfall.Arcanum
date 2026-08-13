using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<InstallationResetScope>))]
public enum InstallationResetScope
{

    Workspace,

    Global,

    All,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<InstallationResetDataScope>))]
public enum InstallationResetDataScope
{

    Workspace,

    Global,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<InstallationResetPhase>))]
public enum InstallationResetPhase
{

    Prepared,

    DataResetComplete,

    OfflineCleanupComplete,

    Verified,

    Completed,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<InstallationResetItemStatus>))]
public enum InstallationResetItemStatus
{

    Pending,

    Preserved,

    Deleted,

    Absent,

    Unavailable,

    Failed,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<InstallationResetTargetRole>))]
public enum InstallationResetTargetRole
{

    Database,

    FileSystem,

    Credential,

    Daemon,

}

public sealed record InstallationResetPlanRequest(
    [property: JsonRequired] InstallationResetScope Scope,
    [property: JsonRequired] string InvocationDirectory);

public sealed record InstallationResetApplyRequest(
    [property: JsonRequired] InstallationResetPlanRequest Request,
    [property: JsonRequired] string ExpectedPlanId);

public sealed record InstallationResetDataPlanRequest(
    [property: JsonRequired] InstallationResetDataScope Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DataRetentionWorkspaceBinding? Workspace = null);

public sealed record InstallationResetFileIdentity(
    [property: JsonRequired] string Value,
    long Length,
    ulong HardLinkCount);

public sealed record InstallationResetTargetDescriptor(
    [property: JsonRequired] string Category,
    [property: JsonRequired] InstallationResetTargetRole Role,
    [property: JsonRequired] string ResourceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CanonicalPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DatabasePredicate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InstallationResetFileIdentity? Identity,
    long? Rows,
    long Files,
    long EstimatedBytes);

public sealed record InstallationResetPreservedBackup(
    [property: JsonRequired] string CanonicalPath,
    [property: JsonRequired] InstallationResetFileIdentity Identity);

public sealed record InstallationResetCredentialSummary(
    [property: JsonRequired] string Account,
    [property: JsonRequired] InstallationResetItemStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null);

public sealed record InstallationResetCredentialResult(
    [property: JsonRequired] string Account,
    [property: JsonRequired] InstallationResetItemStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null);

public sealed record InstallationResetExclusion(
    [property: JsonRequired] string Category,
    [property: JsonRequired] string ResourceId,
    [property: JsonRequired] string Reason);

public sealed record InstallationResetIssueSummary(
    [property: JsonRequired] string Code,
    [property: JsonRequired] string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceId = null);

public sealed record InstallationResetAcceptedBinding(
    [property: JsonRequired] string BindingId,
    [property: JsonRequired] string[] SelectedRoots,
    [property: JsonRequired] string[] ExcludedRoots,
    [property: JsonRequired] InstallationResetPreservedBackup[] PreservedBackups,
    [property: JsonRequired] string[] CredentialAccounts,
    [property: JsonRequired] string[] DataPlanIds);

public sealed record InstallationResetPlan(
    [property: JsonRequired] string PlanId,
    [property: JsonRequired] InstallationResetScope Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DataRetentionWorkspaceBinding? Workspace,
    DateTimeOffset GeneratedAt,
    bool DataInventoryAvailable,
    bool CredentialInventoryAvailable,
    [property: JsonRequired] InstallationResetTargetDescriptor[] Targets,
    [property: JsonRequired] InstallationResetCredentialSummary[] Credentials,
    [property: JsonRequired] InstallationResetPreservedBackup[] PreservedBackups,
    [property: JsonRequired] InstallationResetExclusion[] Exclusions,
    [property: JsonRequired] InstallationResetIssueSummary[] Blockers,
    long? Rows,
    long Files,
    long EstimatedBytes,
    [property: JsonRequired] InstallationResetAcceptedBinding AcceptedBinding);

public sealed record InstallationResetVerification(
    bool Succeeded,
    [property: JsonRequired] InstallationResetIssueSummary[] RemainingIssues);

public sealed record InstallationResetResult(
    Guid OperationId,
    [property: JsonRequired] string PlanId,
    [property: JsonRequired] InstallationResetScope Scope,
    [property: JsonRequired] InstallationResetPhase Phase,
    bool PointOfNoReturn,
    long RowsDeleted,
    long FilesDeleted,
    long EstimatedBytesDeleted,
    [property: JsonRequired] InstallationResetCredentialResult[] CredentialResults,
    [property: JsonRequired] InstallationResetPreservedBackup[] PreservedBackups,
    [property: JsonRequired] InstallationResetVerification Verification,
    bool ResumeRequired,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null);

public sealed record ActiveInstallationReset(
    InstallationResetScope Scope,
    string? WorkspaceRoot,
    string PlanId);

public interface IInstallationStartupProbe
{

    Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
        CancellationToken cancellationToken = default);

    Result<bool> IsFreshInstallation();

}

public interface IInstallationResetService
{

    Task<Result<InstallationResetPlan>> PlanAsync(
        InstallationResetPlanRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<InstallationResetResult>> ApplyAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken = default);

}

public interface IInstallationResetDataService
{

    Task<Result<DataRetentionPlan>> PlanAsync(
        InstallationResetDataPlanRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DataRetentionApplyResult>> ApplyAsync(
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken = default);

}
