using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal enum HostToolsMarkerPairResetPhase : byte
{

    PairJournaled = 1,

    DatabaseMarkerCompareDeleted = 2,

    OsMarkerCompareDeleted = 3,

    PairAbsenceVerified = 4,

}

internal sealed record FullInstallationResetSignedAttestationProjectionV1(
    byte Version,
    Guid OperationId,
    Guid InstallationId,
    Guid HostToolsTransitionId,
    ulong TaintMasterKeyVersion,
    CovenantDigest AuthorityFingerprint,
    CovenantDigest DatabaseMarkerDigest,
    CovenantDigest OsMarkerDigest,
    CovenantDigest RemediationActionDigest,
    string NonceBase64Url,
    string Issuer,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string SignatureBase64Url)
{

    internal static FullInstallationResetSignedAttestationProjectionV1 FromAttestation(
        FullInstallationResetExternalRemediationAttestation attestation)
    {

        ArgumentNullException.ThrowIfNull(attestation);

        return new FullInstallationResetSignedAttestationProjectionV1(
            attestation.Version,
            attestation.OperationId,
            attestation.InstallationId,
            attestation.HostToolsTransitionId,
            attestation.TaintMasterKeyVersion,
            new CovenantDigest(attestation.AuthorityFingerprint.Bytes),
            new CovenantDigest(attestation.DatabaseMarkerDigest.Bytes),
            new CovenantDigest(attestation.OsMarkerDigest.Bytes),
            new CovenantDigest(attestation.RemediationActionDigest.Bytes),
            attestation.NonceBase64Url,
            attestation.Issuer,
            attestation.IssuedAtUtc,
            attestation.ExpiresAtUtc,
            attestation.SignatureBase64Url);

    }

    internal FullInstallationResetExternalRemediationAttestation ToAttestation() =>
        new(
            Version,
            OperationId,
            InstallationId,
            HostToolsTransitionId,
            TaintMasterKeyVersion,
            new CovenantDigest(AuthorityFingerprint.Bytes),
            new CovenantDigest(DatabaseMarkerDigest.Bytes),
            new CovenantDigest(OsMarkerDigest.Bytes),
            new CovenantDigest(RemediationActionDigest.Bytes),
            NonceBase64Url,
            Issuer,
            IssuedAtUtc,
            ExpiresAtUtc,
            SignatureBase64Url);

}

internal sealed record CampaignMarkerInventoryEntryV1(
    Guid CampaignId,
    long PriorPathRevision,
    CovenantDigest MarkerDigest,
    CovenantDigest IndexedPhysicalIdentityDigest,
    CovenantDigest CanonicalDisplayPathDigest,
    CovenantDigest SameHandleOwnershipEvidenceDigest);

internal sealed record FullInstallationResetRestartProofV1(
    byte Version,
    FullInstallationResetSignedAttestationProjectionV1 SignedAttestation,
    DateTimeOffset AcceptedAtUtc,
    CovenantDigest SignedAttestationDigest,
    HostProcessToolsDatabaseMarkerEvidence DatabaseMarkerEvidence,
    HostProcessToolsOsMarkerEvidence OsMarkerEvidence,
    CovenantDigest PairEvidenceDigest);

internal sealed record HostToolsMarkerPairResetCheckpointV1(
    byte Version,
    HostToolsMarkerPairResetPhase Phase,
    FullInstallationResetRestartProofV1 RestartProof,
    ImmutableArray<CampaignMarkerInventoryEntryV1> CampaignInventory,
    CovenantDigest CampaignMarkerInventoryDigest,
    CovenantDigest OwnerEffectDigest,
    ulong? MarkerIntentCount,
    ImmutableArray<Guid>? OrderedMarkerIntentIds,
    CovenantDigest? MarkerIntentVectorDigest,
    ulong? DeletedCount,
    ulong? OrphanCount,
    FullInstallationResetManagedFileCheckpointV1? ManagedFile = null);

internal static class HostToolsMarkerPairResetCheckpointBounds
{

    internal const int MaximumVectorCount = 4096;

    internal static bool HasValidVectorShape(
        HostToolsMarkerPairResetCheckpointV1 checkpoint) =>
        !checkpoint.CampaignInventory.IsDefault
        && checkpoint.CampaignInventory.Length <= MaximumVectorCount
        && (checkpoint.OrderedMarkerIntentIds is not { } intents
            || !intents.IsDefault
            && intents.Length <= MaximumVectorCount)
        && (checkpoint.ManagedFile is not { } managedFile
            || FullInstallationResetManagedFileBounds.HasValidVectorShape(managedFile)
                && FullInstallationResetManagedFileBounds.HasCoherentTerminalTail(managedFile));

    internal static void RequireValidVectorShapeBeforeCopy(
        HostToolsMarkerPairResetCheckpointV1 checkpoint)
    {

        ArgumentNullException.ThrowIfNull(checkpoint);

        if (!HasValidVectorShape(checkpoint))
        {

            throw new ArgumentException(
                "Checkpoint inventory, optional intent vectors, and any nested managed-file inventory must be initialized, contain at most 4,096 entries, and carry a whole or absent terminal tail.",
                nameof(checkpoint));

        }

    }

}

internal enum CampaignPathFullResetCleanupObservationCode : byte
{

    Opened = 1,

    Unavailable = 2,

    Mismatch = 3,

}
