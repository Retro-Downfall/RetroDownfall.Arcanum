using System.Collections.Immutable;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Security;

using BackupRestoreFullResetTerminalArm =
    RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreFullResetTerminalArm;

using BackupRestoreFullResetTerminalProjectionV1 =
    RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreFullResetTerminalProjectionV1;

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

/// <summary>How far the one nested offline transition an installation reset launches has got.</summary>
/// <remarks>
/// Two phases, and the absence of the receipt is a third fact. Nothing started, something started and
/// has not reported, and something finished are three different positions for a reset to resume from,
/// and only the first and the third allow it to go on. A single boolean, or a receipt written only on
/// success, would collapse the middle one into whichever neighbour it happened to resemble.
///
/// <para>The claim is published before the nested transition is launched. That ordering is what makes
/// the middle state observable at all: a claim that arrived after the effect could not distinguish a
/// transition that never began from one whose journal is gone.</para>
/// </remarks>
internal enum InstallationResetNestedTransitionPhase : byte
{

    Claimed = 1,

    Completed = 2,

}

/// <summary>The outer workflow's record of the nested database transition it launched.</summary>
/// <remarks>
/// It names the nested operation and, once that operation is over, the effect it was launched against
/// and the exact database row it terminalized. It carries nothing else: no path, credential, key,
/// generation, epoch, lease, handle, count, subject identity, or disclosure detail. The outer
/// operation id is deliberately absent — this record lives inside the payload whose
/// <c>OperationId</c> is that value, and a second copy would be a second place for the two to
/// disagree.
///
/// <para>The digests are null together at <see cref="InstallationResetNestedTransitionPhase.Claimed"/>
/// and valid together at <see cref="InstallationResetNestedTransitionPhase.Completed"/>, because
/// neither is knowable before the nested transition reaches its terminal compare-exchange.</para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InstallationResetNestedTransitionReceiptV1(
    byte Version,
    Guid NestedOperationId,
    InstallationResetNestedTransitionPhase Phase,
    CovenantDigest? NestedEffectDigest,
    CovenantDigest? TerminalWinnerDigest);

internal sealed record InstallationResetActivePayloadV3(
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
    FullInstallationResetRemediationClaimV1? FullInstallationResetRemediationClaim = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    InstallationResetNestedTransitionReceiptV1? NestedTransitionReceipt = null)
{

    /// <summary>Projects mutable service state into a detached immutable persistence graph.</summary>
    internal static InstallationResetActivePayloadV3 FromRecord(
        InstallationResetActiveRecord record)
    {

        ArgumentNullException.ThrowIfNull(record);

        ArgumentNullException.ThrowIfNull(record.AcceptedBinding);

        return new InstallationResetActivePayloadV3(
            Version: InstallationResetActiveRecordAuthenticator.PayloadVersion,
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
                record.FullInstallationResetRemediationClaim,
            NestedTransitionReceipt:
                CopyNestedTransitionReceipt(record.NestedTransitionReceipt));

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
            HostToolsMarkerPairReset: CopyCheckpoint(HostToolsMarkerPairReset),
            NestedTransitionReceipt: CopyNestedTransitionReceipt(NestedTransitionReceipt));

    /// <summary>Deep-copies the nested receipt so the two graphs share no digest buffer.</summary>
    private static InstallationResetNestedTransitionReceiptV1? CopyNestedTransitionReceipt(
        InstallationResetNestedTransitionReceiptV1? receipt) =>
        receipt is null
            ? null
            : new InstallationResetNestedTransitionReceiptV1(
                receipt.Version,
                receipt.NestedOperationId,
                receipt.Phase,
                receipt.NestedEffectDigest is { } effect ? CopyDigest(effect) : null,
                receipt.TerminalWinnerDigest is { } winner ? CopyDigest(winner) : null);

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
            checkpoint.OrphanCount,
            CopyManagedFileCheckpoint(checkpoint.ManagedFile),
            CopyRestoreTerminal(checkpoint.RestoreTerminal),
            checkpoint.RestoreCredentialCleanup);

    }

    private static FullInstallationResetManagedFileCheckpointV1? CopyManagedFileCheckpoint(
        FullInstallationResetManagedFileCheckpointV1? checkpoint)
    {

        if (checkpoint is null)
        {

            return null;

        }

        FullInstallationResetManagedFileBounds.RequireValidVectorShapeBeforeCopy(checkpoint);

        return new FullInstallationResetManagedFileCheckpointV1(
            checkpoint.Version,
            checkpoint.Phase,
            checkpoint.SourceCount,
            CopyIntentIds(checkpoint.OrderedSourceWriteOperationIds),
            CopyDigest(checkpoint.SourceWriteIntentVectorDigest),
            checkpoint.LocalErasureWorkItemCount,
            checkpoint.OrderedLocalErasureWorkItemIds is { } workItems
                ? workItems.IsDefault
                    ? default(ImmutableArray<Guid>)
                    : CopyIntentIds(workItems)
                : null,
            checkpoint.LocalErasureWorkItemVectorDigest is { } workItemDigest
                ? CopyDigest(workItemDigest)
                : null,
            checkpoint.SafeTerminalWriteIntentCount,
            checkpoint.ManualWriteOrphanCount,
            checkpoint.CompletedWorkItemCount,
            checkpoint.ManualWorkItemOrphanCount,
            checkpoint.TerminalClassificationDigest is { } classification
                ? CopyDigest(classification)
                : null);

    }

    /// <summary>
    /// Deep-copies the terminal restore projection a resumed credential removal compares against.
    /// </summary>
    /// <remarks>
    /// The projection is persisted rather than re-derived because a removal that has already started
    /// cannot prove itself again: the credential set it is midway through taking no longer has the
    /// shape the proof was made from. So the proof travels with the operation, and a resume compares
    /// each surviving account against the digest that was projected for it while all three were still
    /// there.
    /// </remarks>
    private static BackupRestoreFullResetTerminalProjectionV1? CopyRestoreTerminal(
        BackupRestoreFullResetTerminalProjectionV1? terminal)
    {

        if (terminal is null)
        {

            return null;

        }

        return new BackupRestoreFullResetTerminalProjectionV1(
            terminal.Version,
            terminal.Arm,
            CopyDigest(terminal.ProfileNamespaceDigest),
            terminal.InstallationId,
            terminal.ClosedOperationId,
            terminal.ClosedRevision,
            terminal.ClosedEnvelopeDigest is { } closedEnvelope
                ? CopyDigest(closedEnvelope)
                : null,
            terminal.ClosedJournalLocationDigest is { } closedLocation
                ? CopyDigest(closedLocation)
                : null,
            terminal.InstallationAccountValueDigest is { } installation
                ? CopyDigest(installation)
                : null,
            terminal.JournalKeyAccountValueDigest is { } journalKey
                ? CopyDigest(journalKey)
                : null,
            terminal.AnchorAccountValueDigest is { } anchor
                ? CopyDigest(anchor)
                : null,
            CopyDigest(terminal.TerminalEvidenceDigest));

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
[JsonSerializable(typeof(InstallationResetActivePayloadV3))]
[JsonSerializable(typeof(InstallationResetActiveWorkspaceV2))]
[JsonSerializable(typeof(InstallationResetActiveFileIdentityV2))]
[JsonSerializable(typeof(InstallationResetActivePreservedBackupV2))]
[JsonSerializable(typeof(InstallationResetActiveAcceptedBindingV2))]
[JsonSerializable(typeof(InstallationResetActiveCredentialResultV2))]
[JsonSerializable(typeof(InstallationResetActiveOnlineCompletionV2))]
[JsonSerializable(typeof(FullInstallationResetRemediationClaimV1))]
[JsonSerializable(typeof(HostToolsMarkerPairResetCheckpointV1))]
[JsonSerializable(typeof(HostToolsMarkerPairResetPhase))]
[JsonSerializable(typeof(FullInstallationResetManagedFileCheckpointV1))]
[JsonSerializable(typeof(FullInstallationResetManagedFileReconciliationPhase))]
[JsonSerializable(typeof(InstallationResetRestoreCredentialCleanupPhase))]
[JsonSerializable(typeof(InstallationResetNestedTransitionReceiptV1))]
[JsonSerializable(typeof(InstallationResetNestedTransitionPhase))]
[JsonSerializable(typeof(BackupRestoreFullResetTerminalProjectionV1))]
[JsonSerializable(typeof(BackupRestoreFullResetTerminalArm))]
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
