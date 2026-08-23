using System.Collections.Immutable;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Security;

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
    HostToolsMarkerPairResetCheckpointV1? HostToolsMarkerPairReset,
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
            HostToolsMarkerPairReset: CopyCheckpoint(record.HostToolsMarkerPairReset),
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
                FullInstallationResetRemediationClaim,
            HostToolsMarkerPairReset: CopyCheckpoint(HostToolsMarkerPairReset));

    private static HostToolsMarkerPairResetCheckpointV1? CopyCheckpoint(
        HostToolsMarkerPairResetCheckpointV1? checkpoint)
    {

        if (checkpoint is null)
        {

            return null;

        }

        HostToolsMarkerPairResetCheckpointBounds.RequireValidVectorShapeBeforeCopy(checkpoint);

        FullInstallationResetRestartProofV1 restartProof = checkpoint.RestartProof;

        FullInstallationResetSignedAttestationProjectionV1 signed =
            restartProof.SignedAttestation;

        HostProcessToolsDatabaseMarkerEvidence database =
            restartProof.DatabaseMarkerEvidence;

        HostProcessToolsOsMarkerEvidence osMarker = restartProof.OsMarkerEvidence;

        return new HostToolsMarkerPairResetCheckpointV1(
            checkpoint.Version,
            checkpoint.Phase,
            new FullInstallationResetRestartProofV1(
                restartProof.Version,
                new FullInstallationResetSignedAttestationProjectionV1(
                    signed.Version,
                    signed.OperationId,
                    signed.InstallationId,
                    signed.HostToolsTransitionId,
                    signed.TaintMasterKeyVersion,
                    CopyDigest(signed.AuthorityFingerprint),
                    CopyDigest(signed.DatabaseMarkerDigest),
                    CopyDigest(signed.OsMarkerDigest),
                    CopyDigest(signed.RemediationActionDigest),
                    signed.NonceBase64Url,
                    signed.Issuer,
                    signed.IssuedAtUtc,
                    signed.ExpiresAtUtc,
                    signed.SignatureBase64Url),
                restartProof.AcceptedAtUtc,
                CopyDigest(restartProof.SignedAttestationDigest),
                new HostProcessToolsDatabaseMarkerEvidence(
                    database.InstallationIdentity,
                    database.State,
                    database.TransitionId,
                    database.TaintMasterKeyVersion,
                    database.TaintFingerprint is { } databaseFingerprint
                        ? CopyDigest(databaseFingerprint)
                        : null),
                new HostProcessToolsOsMarkerEvidence(
                    osMarker.InstallationIdentity,
                    osMarker.TransitionId,
                    osMarker.TaintMasterKeyVersion,
                    CopyDigest(osMarker.TaintFingerprint),
                    CopyDigest(osMarker.MarkerBytesDigest),
                    CopyDigest(osMarker.DurableIdentityDigest)),
                CopyDigest(restartProof.PairEvidenceDigest)),
            checkpoint.CampaignInventory.IsDefault
                ? default
                : ImmutableArray.CreateRange(
                    checkpoint.CampaignInventory.Select(static entry =>
                        new CampaignMarkerInventoryEntryV1(
                            entry.CampaignId,
                            entry.PriorPathRevision,
                            CopyDigest(entry.MarkerDigest),
                            CopyDigest(entry.IndexedPhysicalIdentityDigest),
                            CopyDigest(entry.CanonicalDisplayPathDigest),
                            CopyDigest(entry.SameHandleOwnershipEvidenceDigest)))),
            CopyDigest(checkpoint.CampaignMarkerInventoryDigest),
            CopyDigest(checkpoint.OwnerEffectDigest),
            checkpoint.MarkerIntentCount,
            checkpoint.OrderedMarkerIntentIds is { } intents
                ? intents.IsDefault
                    ? default(ImmutableArray<Guid>)
                    : CopyIntentIds(intents)
                : null,
            checkpoint.MarkerIntentVectorDigest is { } intentDigest
                ? CopyDigest(intentDigest)
                : null,
            checkpoint.DeletedCount,
            checkpoint.OrphanCount);

    }

    private static CovenantDigest CopyDigest(CovenantDigest digest) =>
        new(digest.Bytes);

    private static ImmutableArray<Guid> CopyIntentIds(ImmutableArray<Guid> intentIds)
    {

        ImmutableArray<Guid>.Builder builder =
            ImmutableArray.CreateBuilder<Guid>(intentIds.Length);

        builder.AddRange(intentIds);

        return builder.MoveToImmutable();

    }

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
[JsonSerializable(typeof(HostToolsMarkerPairResetCheckpointV1))]
[JsonSerializable(typeof(HostToolsMarkerPairResetPhase))]
[JsonSerializable(typeof(FullInstallationResetRestartProofV1))]
[JsonSerializable(typeof(FullInstallationResetSignedAttestationProjectionV1))]
[JsonSerializable(typeof(CampaignMarkerInventoryEntryV1))]
[JsonSerializable(typeof(HostProcessToolsDatabaseMarkerEvidence))]
[JsonSerializable(typeof(HostProcessToolsOsMarkerEvidence))]
[JsonSerializable(typeof(CovenantHostToolsState))]
[JsonSerializable(typeof(InstallationResetActiveAnchorState))]
[JsonSerializable(typeof(InstallationResetScope))]
[JsonSerializable(typeof(InstallationResetPhase))]
[JsonSerializable(typeof(InstallationResetItemStatus))]
[JsonSerializable(typeof(InstallationResetDataHandoff))]
[JsonSerializable(typeof(CovenantDigest))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<InstallationResetActivePreservedBackupV2>))]
[JsonSerializable(typeof(ImmutableArray<InstallationResetActiveCredentialResultV2>))]
[JsonSerializable(typeof(ImmutableArray<CampaignMarkerInventoryEntryV1>))]
[JsonSerializable(typeof(ImmutableArray<Guid>))]
internal sealed partial class InstallationResetActiveJsonContext : JsonSerializerContext;
