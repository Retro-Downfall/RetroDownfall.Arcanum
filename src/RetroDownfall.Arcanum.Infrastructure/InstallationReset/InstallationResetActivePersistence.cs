using System.Collections.Immutable;

using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed record InstallationResetActiveLocation(
    string ActivePath,
    CovenantDigest ProfileNamespaceDigest,
    CovenantDigest GuardedParentPhysicalIdentityDigest,
    string ActiveLeaf,
    CovenantDigest Digest);

internal enum InstallationResetActiveAnchorState : byte
{

    Active = 1,

    Closed = 2,

}

internal sealed record InstallationResetActiveEnvelopeV2(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid OperationId,
    ulong Revision,
    CovenantDigest PreviousEnvelopeDigest,
    CovenantDigest ActiveLocationDigest,
    InstallationResetScope Scope,
    string PlanId,
    string NonceBase64Url,
    string CiphertextBase64Url,
    string AuthenticationTagBase64Url);

internal sealed record InstallationResetActiveAnchorV1(
    byte Version,
    InstallationResetActiveAnchorState State,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid OperationId,
    ulong Revision,
    CovenantDigest EnvelopeDigest,
    CovenantDigest ActiveLocationDigest);

internal sealed record InstallationResetActiveWorkspaceV2(
    Guid CampaignId,
    string WorkspaceRoot);

internal sealed record InstallationResetActiveFileIdentityV2(
    string Value,
    long Length,
    ulong HardLinkCount);

internal sealed record InstallationResetActivePreservedBackupV2(
    string CanonicalPath,
    InstallationResetActiveFileIdentityV2 Identity);

internal sealed record InstallationResetActiveAcceptedBindingV2(
    string BindingId,
    ImmutableArray<string> SelectedRoots,
    ImmutableArray<string> ExcludedRoots,
    ImmutableArray<InstallationResetActivePreservedBackupV2> PreservedBackups,
    ImmutableArray<string> CredentialAccounts,
    ImmutableArray<string> DataPlanIds);

internal sealed record InstallationResetActiveCredentialResultV2(
    string Account,
    InstallationResetItemStatus Status,
    string? ErrorCode);

internal sealed record InstallationResetActiveOnlineCompletionV2(
    Guid ServerOperationId,
    Guid RequestedOperationId,
    string DataPlanId,
    long RowsDeleted,
    long FilesDeleted,
    long EstimatedBytesDeleted,
    long DerivedRecordsDeleted);

internal sealed record FullInstallationResetRemediationClaimV1(
    byte Version,
    Guid OperationId,
    Guid InstallationId,
    CovenantDigest AttestationDigest,
    CovenantDigest NonceDigest,
    CovenantDigest IssuerDigest,
    DateTimeOffset AcceptedAtUtc);

internal sealed record InstallationResetActivePayloadV2(
    byte Version,
    Guid OperationId,
    string PlanId,
    InstallationResetScope Scope,
    InstallationResetActiveWorkspaceV2? Workspace,
    InstallationResetActiveAcceptedBindingV2 AcceptedBinding,
    InstallationResetPhase Phase,
    bool PointOfNoReturn,
    long RowsDeleted,
    long FilesDeleted,
    long EstimatedBytesDeleted,
    ImmutableArray<InstallationResetActiveCredentialResultV2> CredentialResults,
    string? LastErrorCode,
    InstallationResetDataHandoff? DataHandoff,
    InstallationResetActiveOnlineCompletionV2? OnlineDataCompletion,
    JsonElement? HostToolsMarkerPairReset,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    FullInstallationResetRemediationClaimV1? FullInstallationResetRemediationClaim = null)
{

    /// <summary>Projects mutable service state into a detached immutable persistence graph.</summary>
    internal static InstallationResetActivePayloadV2 FromRecord(
        InstallationResetActiveRecord record)
    {

        ArgumentNullException.ThrowIfNull(record);

        ArgumentNullException.ThrowIfNull(record.AcceptedBinding);

        return new InstallationResetActivePayloadV2(
            Version: InstallationResetActiveRecordAuthenticator.EnvelopeVersion,
            OperationId: record.OperationId,
            PlanId: record.PlanId,
            Scope: record.Scope,
            Workspace: record.Workspace is null
                ? null
                : new InstallationResetActiveWorkspaceV2(
                    record.Workspace.CampaignId,
                    record.Workspace.WorkspaceRoot),
            AcceptedBinding: new InstallationResetActiveAcceptedBindingV2(
                record.AcceptedBinding.BindingId,
                ImmutableArray.CreateRange(record.AcceptedBinding.SelectedRoots ?? []),
                ImmutableArray.CreateRange(record.AcceptedBinding.ExcludedRoots ?? []),
                ImmutableArray.CreateRange(
                    (record.AcceptedBinding.PreservedBackups ?? [])
                    .Select(static backup =>
                        new InstallationResetActivePreservedBackupV2(
                            backup.CanonicalPath,
                            new InstallationResetActiveFileIdentityV2(
                                backup.Identity.Value,
                                backup.Identity.Length,
                                backup.Identity.HardLinkCount)))),
                ImmutableArray.CreateRange(record.AcceptedBinding.CredentialAccounts ?? []),
                ImmutableArray.CreateRange(record.AcceptedBinding.DataPlanIds ?? [])),
            Phase: record.Phase,
            PointOfNoReturn: record.PointOfNoReturn,
            RowsDeleted: record.RowsDeleted,
            FilesDeleted: record.FilesDeleted,
            EstimatedBytesDeleted: record.EstimatedBytesDeleted,
            CredentialResults: ImmutableArray.CreateRange(
                (record.CredentialResults ?? [])
                .Select(static result =>
                    new InstallationResetActiveCredentialResultV2(
                        result.Account,
                        result.Status,
                        result.ErrorCode))),
            LastErrorCode: record.LastErrorCode,
            DataHandoff: record.DataHandoff,
            OnlineDataCompletion: record.OnlineDataCompletion is null
                ? null
                : new InstallationResetActiveOnlineCompletionV2(
                    record.OnlineDataCompletion.ServerOperationId,
                    record.OnlineDataCompletion.RequestedOperationId,
                    record.OnlineDataCompletion.DataPlanId,
                    record.OnlineDataCompletion.RowsDeleted,
                    record.OnlineDataCompletion.FilesDeleted,
                    record.OnlineDataCompletion.EstimatedBytesDeleted,
                    record.OnlineDataCompletion.DerivedRecordsDeleted),
            HostToolsMarkerPairReset: null,
            FullInstallationResetRemediationClaim:
                record.FullInstallationResetRemediationClaim);

    }

    /// <summary>Reconstructs fresh mutable arrays for the service-facing domain record.</summary>
    internal InstallationResetActiveRecord ToRecord() =>
        new(
            Version,
            OperationId,
            PlanId,
            Scope,
            Workspace is null
                ? null
                : new DataRetentionWorkspaceBinding(
                    Workspace.CampaignId,
                    Workspace.WorkspaceRoot),
            new InstallationResetAcceptedBinding(
                AcceptedBinding.BindingId,
                AcceptedBinding.SelectedRoots.IsDefault
                    ? []
                    : AcceptedBinding.SelectedRoots.ToArray(),
                AcceptedBinding.ExcludedRoots.IsDefault
                    ? []
                    : AcceptedBinding.ExcludedRoots.ToArray(),
                AcceptedBinding.PreservedBackups.IsDefault
                    ? []
                    : AcceptedBinding.PreservedBackups
                        .Select(static backup =>
                            new InstallationResetPreservedBackup(
                                backup.CanonicalPath,
                                new InstallationResetFileIdentity(
                                    backup.Identity.Value,
                                    backup.Identity.Length,
                                    backup.Identity.HardLinkCount)))
                        .ToArray(),
                AcceptedBinding.CredentialAccounts.IsDefault
                    ? []
                    : AcceptedBinding.CredentialAccounts.ToArray(),
                AcceptedBinding.DataPlanIds.IsDefault
                    ? []
                    : AcceptedBinding.DataPlanIds.ToArray()),
            Phase,
            PointOfNoReturn,
            RowsDeleted,
            FilesDeleted,
            EstimatedBytesDeleted,
            CredentialResults.IsDefault
                ? []
                : CredentialResults
                    .Select(static result =>
                        new InstallationResetCredentialResult(
                            result.Account,
                            result.Status,
                            result.ErrorCode))
                    .ToArray(),
            LastErrorCode,
            DataHandoff,
            OnlineDataCompletion is null
                ? null
                : new InstallationResetOnlineDataCompletion(
                    OnlineDataCompletion.ServerOperationId,
                    OnlineDataCompletion.RequestedOperationId,
                    OnlineDataCompletion.DataPlanId,
                    OnlineDataCompletion.RowsDeleted,
                    OnlineDataCompletion.FilesDeleted,
                    OnlineDataCompletion.EstimatedBytesDeleted,
                    OnlineDataCompletion.DerivedRecordsDeleted),
            FullInstallationResetRemediationClaim:
                FullInstallationResetRemediationClaim);

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(InstallationResetActiveEnvelopeV2))]
[JsonSerializable(typeof(InstallationResetActiveAnchorV1))]
[JsonSerializable(typeof(InstallationResetActivePayloadV2))]
[JsonSerializable(typeof(InstallationResetActiveWorkspaceV2))]
[JsonSerializable(typeof(InstallationResetActiveFileIdentityV2))]
[JsonSerializable(typeof(InstallationResetActivePreservedBackupV2))]
[JsonSerializable(typeof(InstallationResetActiveAcceptedBindingV2))]
[JsonSerializable(typeof(InstallationResetActiveCredentialResultV2))]
[JsonSerializable(typeof(InstallationResetActiveOnlineCompletionV2))]
[JsonSerializable(typeof(FullInstallationResetRemediationClaimV1))]
[JsonSerializable(typeof(InstallationResetActiveAnchorState))]
[JsonSerializable(typeof(InstallationResetScope))]
[JsonSerializable(typeof(InstallationResetPhase))]
[JsonSerializable(typeof(InstallationResetItemStatus))]
[JsonSerializable(typeof(InstallationResetDataHandoff))]
[JsonSerializable(typeof(CovenantDigest))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<InstallationResetActivePreservedBackupV2>))]
[JsonSerializable(typeof(ImmutableArray<InstallationResetActiveCredentialResultV2>))]
internal sealed partial class InstallationResetActiveJsonContext : JsonSerializerContext;
