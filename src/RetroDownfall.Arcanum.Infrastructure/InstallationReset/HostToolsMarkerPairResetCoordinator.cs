using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed class HostToolsMarkerPairResetCoordinator : IHostToolsMarkerPairResetCoordinator
{

    private readonly IInstallationResetActiveStore _activeStore;

    private readonly IHostToolsMarkerPairResetDatabase _database;

    private readonly IFullInstallationResetCampaignSchemaReadiness _readiness;

    private readonly IHostProcessToolsMarkerPairJoiner _joiner;

    private readonly IFullInstallationResetRemediationAttestationVerifier _verifier;

    private readonly ICampaignPathMarkerLifecycle _lifecycle;

    private readonly IHostToolsMarkerPairResetOsPort _os;

    internal HostToolsMarkerPairResetCoordinator(
        IInstallationResetActiveStore activeStore,
        IHostToolsMarkerPairResetDatabase database,
        IFullInstallationResetCampaignSchemaReadiness readiness,
        IHostProcessToolsMarkerPairJoiner joiner,
        IFullInstallationResetRemediationAttestationVerifier verifier,
        ICampaignPathMarkerLifecycle lifecycle,
        IHostToolsMarkerPairResetOsPort os)
    {

        _activeStore = activeStore ?? throw new ArgumentNullException(nameof(activeStore));

        _database = database ?? throw new ArgumentNullException(nameof(database));

        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));

        _joiner = joiner ?? throw new ArgumentNullException(nameof(joiner));

        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));

        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

        _os = os ?? throw new ArgumentNullException(nameof(os));

    }

    internal async Task<Result<InstallationResetActivePublication>> BeginAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication acceptedClaim,
        FullInstallationResetExternalRemediationAttestation attestation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(acceptedClaim);

        ArgumentNullException.ThrowIfNull(attestation);

        heldInstallationLock.AssertHeldFor(_activeStore.GuardedRoot);

        BeginAttemptState attempt = new();

        try
        {

            return await BeginCoreAsync(
                heldInstallationLock,
                acceptedClaim,
                attestation,
                attempt,
                cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException exception) when (
            !attempt.PairJournaledPublished
            && cancellationToken.IsCancellationRequested
            && exception.CancellationToken == cancellationToken)
        {

            throw;

        }
        catch (Exception) when (!attempt.PairJournaledPublished)
        {

            return PreJournalRefusal<InstallationResetActivePublication>(
                cancellationToken);

        }
        catch (Exception)
        {

            return Inert<InstallationResetActivePublication>();

        }

    }

    private async Task<Result<InstallationResetActivePublication>> BeginCoreAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication acceptedClaim,
        FullInstallationResetExternalRemediationAttestation attestation,
        BeginAttemptState attempt,
        CancellationToken cancellationToken)
    {

        Result<InstallationResetActiveRecoveryState> recovered =
            await _activeStore.RecoverAsync(
                heldInstallationLock,
                cancellationToken).ConfigureAwait(false);

        if (recovered.IsFailure
            || recovered.Value.Outcome
                is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } current
            || !PublicationEquals(acceptedClaim, current)
            || InstallationResetActiveRecordAuthenticator.ValidatePayload(
                current.Payload).IsFailure
            || current.Payload.FullInstallationResetRemediationClaim is not { } claim
            || current.Payload.HostToolsMarkerPairReset is not null)
        {

            return PreJournalRefusal<InstallationResetActivePublication>(
                cancellationToken);

        }

        HostToolsMarkerPairResetOsOpenResult opened = _os.OpenExact();

        if (opened.Status is not HostToolsMarkerPairResetOsOpenStatus.Opened
            || opened.Evidence is null
            || opened.Capability is null)
        {

            return PreJournalRefusal<InstallationResetActivePublication>(
                cancellationToken);

        }

        using IHostToolsMarkerPairResetOsCapability capability = opened.Capability;

        Result<HostToolsMarkerPairResetDatabaseSession> databaseOpened =
            await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (databaseOpened.IsFailure)
        {

            return PreJournalRefusal<InstallationResetActivePublication>(
                cancellationToken);

        }

        await using HostToolsMarkerPairResetDatabaseSession session = databaseOpened.Value;

        Result ready = await _readiness.RequireExactAsync(
            session.BorrowCoreConnection(),
            cancellationToken).ConfigureAwait(false);

        if (ready.IsFailure)
        {

            return PreJournalRefusal<InstallationResetActivePublication>(
                cancellationToken);

        }

        Result<HostProcessToolsDatabaseMarkerEvidence> databaseEvidence =
            await session.ReadTaintedAsync(cancellationToken).ConfigureAwait(false);

        if (databaseEvidence.IsFailure)
        {

            return PreJournalRefusal<InstallationResetActivePublication>(
                cancellationToken);

        }

        HostProcessToolsMarkerPairJoinResult joined = _joiner.Join(
            databaseEvidence.Value,
            opened.Evidence);

        if (joined.Disposition
                is not HostProcessToolsMarkerPairDisposition.TaintedMatched
            || joined.MatchedPair is not { } pair
            || !DatabaseEvidenceEquals(pair.Database, databaseEvidence.Value)
            || !OsEvidenceEquals(pair.OsMarker, opened.Evidence))
        {

            return PreJournalRefusal<InstallationResetActivePublication>(
                cancellationToken);

        }

        Result<FullInstallationResetRemediationAuthorization> verified =
            _verifier.VerifyAtAcceptedTime(
                attestation,
                claim.InstallationId,
                pair,
                claim.AcceptedAtUtc);

        if (verified.IsFailure)
        {

            return PreJournalRefusal<InstallationResetActivePublication>(
                cancellationToken);

        }

        FullInstallationResetRemediationAuthorization authorization = verified.Value;

        if (authorization.OperationId != claim.OperationId
            || authorization.InstallationId != claim.InstallationId
            || !DigestEquals(
                authorization.AttestationDigest,
                claim.AttestationDigest)
            || !DigestEquals(authorization.NonceDigest, claim.NonceDigest)
            || !DigestEquals(authorization.IssuerDigest, claim.IssuerDigest)
            || authorization.AcceptedAtUtc.Ticks != claim.AcceptedAtUtc.Ticks
            || authorization.AcceptedAtUtc.Offset != claim.AcceptedAtUtc.Offset)
        {

            return PreJournalRefusal<InstallationResetActivePublication>(
                cancellationToken);

        }

        try
        {

            Result<CampaignPathFullInstallationResetInventory> inventory =
                await _lifecycle.InventoryFullInstallationResetCleanupAsync(
                    claim.OperationId,
                    session.BorrowCoreConnection(),
                    cancellationToken).ConfigureAwait(false);

            if (inventory.IsFailure)
            {

                return PreJournalRefusal<InstallationResetActivePublication>(
                    cancellationToken);

            }

            if (inventory.Value.OwnerOperationId != claim.OperationId)
            {

                return PreJournalRefusal<InstallationResetActivePublication>(
                    cancellationToken);

            }

            Result<CovenantDigest> signedDigest =
                FullInstallationResetRemediationAttestationDigest.Calculate(attestation);

            Result<CovenantDigest> pairDigest =
                FullInstallationResetMarkerPairResetDigests.PairEvidence(pair);

            Result<CovenantDigest> inventoryDigest =
                FullInstallationResetMarkerPairResetDigests.CampaignInventory(
                    inventory.Value.Entries);

            if (signedDigest.IsFailure
                || pairDigest.IsFailure
                || inventoryDigest.IsFailure
                || !DigestEquals(signedDigest.Value, claim.AttestationDigest)
                || !DigestEquals(
                    inventoryDigest.Value,
                    inventory.Value.InventoryDigest))
            {

                return PreJournalRefusal<InstallationResetActivePublication>(
                    cancellationToken);

            }

            Result<CovenantDigest> ownerEffect =
                FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                    claim.OperationId,
                    claim.InstallationId,
                    pair.Database.TransitionId!.Value,
                    pair.Database.TaintMasterKeyVersion!.Value,
                    pair.Database.TaintFingerprint!.Value,
                    pair.Database.DatabaseMarkerDigest,
                    pair.OsMarker.MarkerBytesDigest,
                    attestation.RemediationActionDigest,
                    inventoryDigest.Value);

            if (ownerEffect.IsFailure)
            {

                return PreJournalRefusal<InstallationResetActivePublication>(
                    cancellationToken);

            }

            Result inventoryRevalidated =
                await _lifecycle.RevalidateFullInstallationResetInventoryAsync(
                    inventory.Value,
                    session.BorrowCoreConnection(),
                    cancellationToken).ConfigureAwait(false);

            if (inventoryRevalidated.IsFailure)
            {

                return PreJournalRefusal<InstallationResetActivePublication>(
                    cancellationToken);

            }

            HostToolsMarkerPairResetCheckpointV1 journaled = new(
                Version: 1,
                HostToolsMarkerPairResetPhase.PairJournaled,
                new FullInstallationResetRestartProofV1(
                    Version: 1,
                    FullInstallationResetSignedAttestationProjectionV1.FromAttestation(
                        attestation),
                    claim.AcceptedAtUtc,
                    signedDigest.Value,
                    pair.Database,
                    pair.OsMarker,
                    pairDigest.Value),
                inventory.Value.Entries,
                inventoryDigest.Value,
                ownerEffect.Value,
                MarkerIntentCount: null,
                OrderedMarkerIntentIds: null,
                MarkerIntentVectorDigest: null,
                DeletedCount: null,
                OrphanCount: null);

            InstallationResetActiveRecord next = current.Payload.ToRecord() with
            {
                HostToolsMarkerPairReset = journaled,
            };

            Result<InstallationResetActivePublication> published =
                await _activeStore.AdvanceAsync(
                    heldInstallationLock,
                    current,
                    next,
                    cancellationToken).ConfigureAwait(false);

            if (published.IsFailure)
            {

                return PreJournalRefusal<InstallationResetActivePublication>(
                    cancellationToken);

            }

            attempt.PairJournaledPublished = true;

            using CancellationTokenSource recoveryCheckpoint =
                new(TimeSpan.FromSeconds(5));

            try
            {

                Result<RevalidatedPairCheckpoint> journalProof =
                    await RecoverAndRevalidatePairCheckpointAsync(
                        heldInstallationLock,
                        published.Value,
                        HostToolsMarkerPairResetPhase.PairJournaled,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (journalProof.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                InstallationResetActivePublication currentJournal =
                    journalProof.Value.Publication;

                HostToolsMarkerPairResetCheckpointV1 checkpoint =
                    journalProof.Value.Checkpoint;

                Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
                    await session.BeginImmediateAndCaptureAsync(
                        checkpoint.RestartProof.DatabaseMarkerEvidence,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (captured.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                Result databaseCleared =
                    await session.CompareClearCommitAndProveDurableAsync(
                        captured.Value,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (databaseCleared.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                HostToolsMarkerPairResetCheckpointV1 databaseMarkerDeleted =
                    checkpoint with
                    {
                        Phase = HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
                    };

                InstallationResetActiveRecord databaseMarkerDeletedRecord =
                    currentJournal.Payload.ToRecord() with
                    {
                        HostToolsMarkerPairReset = databaseMarkerDeleted,
                    };

                Result<InstallationResetActivePublication> databaseMarkerPublished =
                    await _activeStore.AdvanceAsync(
                        heldInstallationLock,
                        currentJournal,
                        databaseMarkerDeletedRecord,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (databaseMarkerPublished.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                Result<RevalidatedPairCheckpoint> databaseMarkerProof =
                    await RecoverAndRevalidatePairCheckpointAsync(
                        heldInstallationLock,
                        databaseMarkerPublished.Value,
                        HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (databaseMarkerProof.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                HostToolsMarkerPairResetOsDeleteStatus deleted =
                    await _os.CompareDeleteExactAsync(
                        capability,
                        databaseMarkerProof.Value.Checkpoint.RestartProof.OsMarkerEvidence,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (deleted is not HostToolsMarkerPairResetOsDeleteStatus.Deleted)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                Result<RevalidatedPairCheckpoint> deletedMarkerProof =
                    await RecoverAndRevalidatePairCheckpointAsync(
                        heldInstallationLock,
                        databaseMarkerProof.Value.Publication,
                        HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (deletedMarkerProof.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                HostToolsMarkerPairResetCheckpointV1 osMarkerDeleted =
                    deletedMarkerProof.Value.Checkpoint with
                    {
                        Phase = HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted,
                    };

                InstallationResetActiveRecord osMarkerDeletedRecord =
                    deletedMarkerProof.Value.Publication.Payload.ToRecord() with
                    {
                        HostToolsMarkerPairReset = osMarkerDeleted,
                    };

                Result<InstallationResetActivePublication> osMarkerPublished =
                    await _activeStore.AdvanceAsync(
                        heldInstallationLock,
                        deletedMarkerProof.Value.Publication,
                        osMarkerDeletedRecord,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (osMarkerPublished.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                Result<RevalidatedPairCheckpoint> osMarkerProof =
                    await RecoverAndRevalidatePairCheckpointAsync(
                        heldInstallationLock,
                        osMarkerPublished.Value,
                        HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (osMarkerProof.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                HostToolsMarkerPairResetOsAbsenceStatus finalOsAbsence =
                    await _os.ProveExactAbsenceAsync(
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (finalOsAbsence is not HostToolsMarkerPairResetOsAbsenceStatus.Absent)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                Result<RevalidatedPairCheckpoint> finalDatabaseProof =
                    await RecoverAndRevalidatePairCheckpointAsync(
                        heldInstallationLock,
                        osMarkerProof.Value.Publication,
                        HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (finalDatabaseProof.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                Result databaseAbsent =
                    await session.ProveSameInstallationCleanDurableAsync(
                        finalDatabaseProof.Value.Checkpoint.RestartProof
                            .DatabaseMarkerEvidence.InstallationIdentity,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (databaseAbsent.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                HostToolsMarkerPairResetCheckpointV1 pairAbsenceVerified =
                    finalDatabaseProof.Value.Checkpoint with
                    {
                        Phase = HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                    };

                InstallationResetActiveRecord pairAbsenceVerifiedRecord =
                    finalDatabaseProof.Value.Publication.Payload.ToRecord() with
                    {
                        HostToolsMarkerPairReset = pairAbsenceVerified,
                    };

                Result<InstallationResetActivePublication> pairAbsencePublished =
                    await _activeStore.AdvanceAsync(
                        heldInstallationLock,
                        finalDatabaseProof.Value.Publication,
                        pairAbsenceVerifiedRecord,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (pairAbsencePublished.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

            }
            catch (Exception)
            {

                return Inert<InstallationResetActivePublication>();

            }

            return Inert<InstallationResetActivePublication>();

        }
        finally
        {

            if (!attempt.PairJournaledPublished)
            {

                try
                {

                    await _lifecycle.ReleaseRetainedRootsAsync(
                        claim.OperationId).ConfigureAwait(false);

                }
                catch (Exception)
                {
                    // Best effort: cleanup cannot replace the primary refusal or caller cancellation.
                }

            }

        }

    }

    internal async Task<Result<InstallationResetActivePublication>> ResumeAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication checkpoint,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(checkpoint);

        heldInstallationLock.AssertHeldFor(_activeStore.GuardedRoot);

        _ = cancellationToken;

        using CancellationTokenSource recoveryCheckpoint =
            new(TimeSpan.FromSeconds(5));

        try
        {

            Result<InstallationResetActiveRecoveryState> recovered =
                await _activeStore.RecoverAsync(
                    heldInstallationLock,
                    recoveryCheckpoint.Token).ConfigureAwait(false);

            if (recovered.IsFailure
                || recovered.Value.Outcome
                    is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
                || recovered.Value.Publication is not { } current
                || !PublicationEquals(checkpoint, current)
                || current.Payload.HostToolsMarkerPairReset is not { } currentCheckpoint
                || currentCheckpoint.Phase
                    is not HostToolsMarkerPairResetPhase.PairJournaled
                    and not HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted
                    and not HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted
                    and not HostToolsMarkerPairResetPhase.PairAbsenceVerified)
            {

                return Inert<InstallationResetActivePublication>();

            }

            if (currentCheckpoint.Phase
                is HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted
                    or HostToolsMarkerPairResetPhase.PairAbsenceVerified)
            {

                return await ResumeFromAbsentPairStateAsync(
                    heldInstallationLock,
                    current,
                    currentCheckpoint,
                    recoveryCheckpoint.Token).ConfigureAwait(false);

            }

            HostToolsMarkerPairResetOsOpenResult opened = _os.ReopenExact(
                currentCheckpoint.RestartProof.OsMarkerEvidence);

            if (currentCheckpoint.Phase
                    is HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted
                && opened.Status is HostToolsMarkerPairResetOsOpenStatus.Absent)
            {

                return await ResumeFromDatabaseDeletedOsAbsentAsync(
                    heldInstallationLock,
                    current,
                    currentCheckpoint,
                    recoveryCheckpoint.Token).ConfigureAwait(false);

            }

            if (opened.Status is not HostToolsMarkerPairResetOsOpenStatus.Opened
                || opened.Capability is null)
            {

                return Inert<InstallationResetActivePublication>();

            }

            using IHostToolsMarkerPairResetOsCapability capability = opened.Capability;

            if (opened.Evidence is null
                || !OsEvidenceEquals(
                    opened.Evidence,
                    currentCheckpoint.RestartProof.OsMarkerEvidence))
            {

                return Inert<InstallationResetActivePublication>();

            }

            Result<HostToolsMarkerPairResetDatabaseSession> databaseOpened =
                await _database.OpenAsync(
                    recoveryCheckpoint.Token).ConfigureAwait(false);

            if (databaseOpened.IsFailure)
            {

                return Inert<InstallationResetActivePublication>();

            }

            await using HostToolsMarkerPairResetDatabaseSession session =
                databaseOpened.Value;

            Result ready = await _readiness.RequireExactAsync(
                session.BorrowCoreConnection(),
                recoveryCheckpoint.Token).ConfigureAwait(false);

            if (ready.IsFailure)
            {

                return Inert<InstallationResetActivePublication>();

            }

            Result<HostToolsDatabaseMarkerRecoveryObservation> observed =
                await session.ObserveExpectedOrCleanAsync(
                    currentCheckpoint.RestartProof.DatabaseMarkerEvidence,
                    recoveryCheckpoint.Token).ConfigureAwait(false);

            if (observed.IsFailure
                || currentCheckpoint.Phase
                    is HostToolsMarkerPairResetPhase.PairJournaled
                    && observed.Value
                        is not HostToolsDatabaseMarkerRecoveryObservation.OriginalTainted
                        and not HostToolsDatabaseMarkerRecoveryObservation.SameInstallationClean
                || currentCheckpoint.Phase
                    is HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted
                    && observed.Value
                        is not HostToolsDatabaseMarkerRecoveryObservation.SameInstallationClean)
            {

                return Inert<InstallationResetActivePublication>();

            }

            HostProcessToolsMarkerPairJoinResult joined = _joiner.Join(
                currentCheckpoint.RestartProof.DatabaseMarkerEvidence,
                opened.Evidence);

            if (joined.Disposition
                    is not HostProcessToolsMarkerPairDisposition.TaintedMatched
                || joined.MatchedPair is not { } pair
                || !DatabaseEvidenceEquals(
                    pair.Database,
                    currentCheckpoint.RestartProof.DatabaseMarkerEvidence)
                || !OsEvidenceEquals(
                    pair.OsMarker,
                    currentCheckpoint.RestartProof.OsMarkerEvidence))
            {

                return Inert<InstallationResetActivePublication>();

            }

            Result<RevalidatedPairCheckpoint> revalidated =
                await RecoverAndRevalidatePairCheckpointAsync(
                    heldInstallationLock,
                    current,
                    currentCheckpoint.Phase,
                    recoveryCheckpoint.Token).ConfigureAwait(false);

            if (revalidated.IsFailure)
            {

                return Inert<InstallationResetActivePublication>();

            }

            if (currentCheckpoint.Phase
                    is HostToolsMarkerPairResetPhase.PairJournaled
                && observed.Value
                    is HostToolsDatabaseMarkerRecoveryObservation.SameInstallationClean)
            {

                Result databaseAbsent =
                    await session.ProveSameInstallationCleanDurableAsync(
                        revalidated.Value.Checkpoint.RestartProof.DatabaseMarkerEvidence
                            .InstallationIdentity,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (databaseAbsent.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                Result<RevalidatedPairCheckpoint> durableProof =
                    await RecoverAndRevalidatePairCheckpointAsync(
                        heldInstallationLock,
                        revalidated.Value.Publication,
                        HostToolsMarkerPairResetPhase.PairJournaled,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (durableProof.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                HostToolsMarkerPairResetCheckpointV1 recoveredDatabaseMarkerDeleted =
                    durableProof.Value.Checkpoint with
                    {
                        Phase = HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
                    };

                InstallationResetActiveRecord recoveredDatabaseMarkerDeletedRecord =
                    durableProof.Value.Publication.Payload.ToRecord() with
                    {
                        HostToolsMarkerPairReset = recoveredDatabaseMarkerDeleted,
                    };

                Result<InstallationResetActivePublication> recoveredPublication =
                    await _activeStore.AdvanceAsync(
                        heldInstallationLock,
                        durableProof.Value.Publication,
                        recoveredDatabaseMarkerDeletedRecord,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (recoveredPublication.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                return Inert<InstallationResetActivePublication>();

            }

            if (currentCheckpoint.Phase
                is HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted)
            {

                HostToolsMarkerPairResetOsDeleteStatus deleted =
                    await _os.CompareDeleteExactAsync(
                        capability,
                        revalidated.Value.Checkpoint.RestartProof.OsMarkerEvidence,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (deleted is not HostToolsMarkerPairResetOsDeleteStatus.Deleted)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                Result<RevalidatedPairCheckpoint> deletedMarkerProof =
                    await RecoverAndRevalidatePairCheckpointAsync(
                        heldInstallationLock,
                        revalidated.Value.Publication,
                        HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (deletedMarkerProof.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                HostToolsMarkerPairResetCheckpointV1 osMarkerDeleted =
                    deletedMarkerProof.Value.Checkpoint with
                    {
                        Phase = HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted,
                    };

                InstallationResetActiveRecord osMarkerDeletedRecord =
                    deletedMarkerProof.Value.Publication.Payload.ToRecord() with
                    {
                        HostToolsMarkerPairReset = osMarkerDeleted,
                    };

                Result<InstallationResetActivePublication> osMarkerPublished =
                    await _activeStore.AdvanceAsync(
                        heldInstallationLock,
                        deletedMarkerProof.Value.Publication,
                        osMarkerDeletedRecord,
                        recoveryCheckpoint.Token).ConfigureAwait(false);

                if (osMarkerPublished.IsFailure)
                {

                    return Inert<InstallationResetActivePublication>();

                }

                return Inert<InstallationResetActivePublication>();

            }

            Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
                await session.BeginImmediateAndCaptureAsync(
                    revalidated.Value.Checkpoint.RestartProof.DatabaseMarkerEvidence,
                    recoveryCheckpoint.Token).ConfigureAwait(false);

            if (captured.IsFailure)
            {

                return Inert<InstallationResetActivePublication>();

            }

            Result databaseCleared =
                await session.CompareClearCommitAndProveDurableAsync(
                    captured.Value,
                    recoveryCheckpoint.Token).ConfigureAwait(false);

            if (databaseCleared.IsFailure)
            {

                return Inert<InstallationResetActivePublication>();

            }

            HostToolsMarkerPairResetCheckpointV1 databaseMarkerDeleted =
                revalidated.Value.Checkpoint with
                {
                    Phase = HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
                };

            InstallationResetActiveRecord databaseMarkerDeletedRecord =
                revalidated.Value.Publication.Payload.ToRecord() with
                {
                    HostToolsMarkerPairReset = databaseMarkerDeleted,
                };

            Result<InstallationResetActivePublication> published =
                await _activeStore.AdvanceAsync(
                    heldInstallationLock,
                    revalidated.Value.Publication,
                    databaseMarkerDeletedRecord,
                    recoveryCheckpoint.Token).ConfigureAwait(false);

            if (published.IsFailure)
            {

                return Inert<InstallationResetActivePublication>();

            }

        }
        catch (Exception)
        {

            return Inert<InstallationResetActivePublication>();

        }

        return Inert<InstallationResetActivePublication>();

    }

    private async Task<Result<InstallationResetActivePublication>>
        ResumeFromDatabaseDeletedOsAbsentAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication current,
            HostToolsMarkerPairResetCheckpointV1 currentCheckpoint,
            CancellationToken cancellationToken)
    {

        Result<HostToolsMarkerPairResetDatabaseSession> databaseOpened =
            await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (databaseOpened.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        await using HostToolsMarkerPairResetDatabaseSession session =
            databaseOpened.Value;

        Result ready = await _readiness.RequireExactAsync(
            session.BorrowCoreConnection(),
            cancellationToken).ConfigureAwait(false);

        if (ready.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        Result<HostToolsDatabaseMarkerRecoveryObservation> observed =
            await session.ObserveExpectedOrCleanAsync(
                currentCheckpoint.RestartProof.DatabaseMarkerEvidence,
                cancellationToken).ConfigureAwait(false);

        if (observed.IsFailure
            || observed.Value
                is not HostToolsDatabaseMarkerRecoveryObservation.SameInstallationClean)
        {

            return Inert<InstallationResetActivePublication>();

        }

        HostProcessToolsMarkerPairJoinResult joined = _joiner.Join(
            currentCheckpoint.RestartProof.DatabaseMarkerEvidence,
            currentCheckpoint.RestartProof.OsMarkerEvidence);

        if (joined.Disposition
                is not HostProcessToolsMarkerPairDisposition.TaintedMatched
            || joined.MatchedPair is not { } pair
            || !DatabaseEvidenceEquals(
                pair.Database,
                currentCheckpoint.RestartProof.DatabaseMarkerEvidence)
            || !OsEvidenceEquals(
                pair.OsMarker,
                currentCheckpoint.RestartProof.OsMarkerEvidence))
        {

            return Inert<InstallationResetActivePublication>();

        }

        Result<RevalidatedPairCheckpoint> revalidated =
            await RecoverAndRevalidatePairCheckpointAsync(
                heldInstallationLock,
                current,
                HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
                cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        HostToolsMarkerPairResetOsAbsenceStatus osAbsence =
            await _os.ProveExactAbsenceAsync(cancellationToken).ConfigureAwait(false);

        if (osAbsence is not HostToolsMarkerPairResetOsAbsenceStatus.Absent)
        {

            return Inert<InstallationResetActivePublication>();

        }

        Result<RevalidatedPairCheckpoint> durableProof =
            await RecoverAndRevalidatePairCheckpointAsync(
                heldInstallationLock,
                revalidated.Value.Publication,
                HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
                cancellationToken).ConfigureAwait(false);

        if (durableProof.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        HostToolsMarkerPairResetCheckpointV1 osMarkerDeleted =
            durableProof.Value.Checkpoint with
            {
                Phase = HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted,
            };

        InstallationResetActiveRecord osMarkerDeletedRecord =
            durableProof.Value.Publication.Payload.ToRecord() with
            {
                HostToolsMarkerPairReset = osMarkerDeleted,
            };

        Result<InstallationResetActivePublication> published =
            await _activeStore.AdvanceAsync(
                heldInstallationLock,
                durableProof.Value.Publication,
                osMarkerDeletedRecord,
                cancellationToken).ConfigureAwait(false);

        if (published.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        return Inert<InstallationResetActivePublication>();

    }

    private async Task<Result<InstallationResetActivePublication>>
        ResumeFromAbsentPairStateAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication current,
            HostToolsMarkerPairResetCheckpointV1 currentCheckpoint,
            CancellationToken cancellationToken)
    {

        HostProcessToolsMarkerPairJoinResult joined = _joiner.Join(
            currentCheckpoint.RestartProof.DatabaseMarkerEvidence,
            currentCheckpoint.RestartProof.OsMarkerEvidence);

        if (joined.Disposition
                is not HostProcessToolsMarkerPairDisposition.TaintedMatched
            || joined.MatchedPair is not { } pair
            || !DatabaseEvidenceEquals(
                pair.Database,
                currentCheckpoint.RestartProof.DatabaseMarkerEvidence)
            || !OsEvidenceEquals(
                pair.OsMarker,
                currentCheckpoint.RestartProof.OsMarkerEvidence))
        {

            return Inert<InstallationResetActivePublication>();

        }

        Result<RevalidatedPairCheckpoint> revalidated =
            await RecoverAndRevalidatePairCheckpointAsync(
                heldInstallationLock,
                current,
                currentCheckpoint.Phase,
                cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        HostToolsMarkerPairResetOsAbsenceStatus osAbsence =
            await _os.ProveExactAbsenceAsync(cancellationToken).ConfigureAwait(false);

        if (osAbsence is not HostToolsMarkerPairResetOsAbsenceStatus.Absent)
        {

            return Inert<InstallationResetActivePublication>();

        }

        Result<HostToolsMarkerPairResetDatabaseSession> databaseOpened =
            await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (databaseOpened.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        await using HostToolsMarkerPairResetDatabaseSession session =
            databaseOpened.Value;

        Result ready = await _readiness.RequireExactAsync(
            session.BorrowCoreConnection(),
            cancellationToken).ConfigureAwait(false);

        if (ready.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        Result<HostToolsDatabaseMarkerRecoveryObservation> observed =
            await session.ObserveExpectedOrCleanAsync(
                currentCheckpoint.RestartProof.DatabaseMarkerEvidence,
                cancellationToken).ConfigureAwait(false);

        if (observed.IsFailure
            || observed.Value
                is not HostToolsDatabaseMarkerRecoveryObservation.SameInstallationClean)
        {

            return Inert<InstallationResetActivePublication>();

        }

        Result databaseAbsent =
            await session.ProveSameInstallationCleanDurableAsync(
                revalidated.Value.Checkpoint.RestartProof.DatabaseMarkerEvidence
                    .InstallationIdentity,
                cancellationToken).ConfigureAwait(false);

        if (databaseAbsent.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        Result<RevalidatedPairCheckpoint> finalProof =
            await RecoverAndRevalidatePairCheckpointAsync(
                heldInstallationLock,
                revalidated.Value.Publication,
                currentCheckpoint.Phase,
                cancellationToken).ConfigureAwait(false);

        if (finalProof.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        if (currentCheckpoint.Phase
            is HostToolsMarkerPairResetPhase.PairAbsenceVerified)
        {

            return Inert<InstallationResetActivePublication>();

        }

        HostToolsMarkerPairResetCheckpointV1 pairAbsenceVerified =
            finalProof.Value.Checkpoint with
            {
                Phase = HostToolsMarkerPairResetPhase.PairAbsenceVerified,
            };

        InstallationResetActiveRecord pairAbsenceVerifiedRecord =
            finalProof.Value.Publication.Payload.ToRecord() with
            {
                HostToolsMarkerPairReset = pairAbsenceVerified,
            };

        Result<InstallationResetActivePublication> published =
            await _activeStore.AdvanceAsync(
                heldInstallationLock,
                finalProof.Value.Publication,
                pairAbsenceVerifiedRecord,
                cancellationToken).ConfigureAwait(false);

        if (published.IsFailure)
        {

            return Inert<InstallationResetActivePublication>();

        }

        return Inert<InstallationResetActivePublication>();

    }

    Task<Result<InstallationResetActivePublication>>
        IHostToolsMarkerPairResetCoordinator.BeginAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication acceptedClaim,
            FullInstallationResetExternalRemediationAttestation attestation,
            CancellationToken cancellationToken) =>
        BeginAsync(
            heldInstallationLock,
            acceptedClaim,
            attestation,
            cancellationToken);

    Task<Result<InstallationResetActivePublication>>
        IHostToolsMarkerPairResetCoordinator.ResumeAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication checkpoint,
            CancellationToken cancellationToken) =>
        ResumeAsync(
            heldInstallationLock,
            checkpoint,
            cancellationToken);

    private async Task<Result<RevalidatedPairCheckpoint>>
        RecoverAndRevalidatePairCheckpointAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication expectedPublication,
            HostToolsMarkerPairResetPhase expectedPhase,
            CancellationToken cancellationToken)
    {

        Result<InstallationResetActiveRecoveryState> recovered =
            await _activeStore.RecoverAsync(
                heldInstallationLock,
                cancellationToken).ConfigureAwait(false);

        if (recovered.IsFailure
            || recovered.Value.Outcome
                is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } current
            || !PublicationEquals(expectedPublication, current)
            || current.Payload.HostToolsMarkerPairReset is not { } checkpoint
            || checkpoint.Phase != expectedPhase
            || current.Payload.FullInstallationResetRemediationClaim is not { } claim)
        {

            return Inert<RevalidatedPairCheckpoint>();

        }

        try
        {

            FullInstallationResetExternalRemediationAttestation attestation =
                checkpoint.RestartProof.SignedAttestation.ToAttestation();

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            Result<CovenantDigest> signedDigest =
                FullInstallationResetRemediationAttestationDigest.Calculate(attestation);

            Result<CovenantDigest> pairDigest =
                FullInstallationResetMarkerPairResetDigests.PairEvidence(pair);

            Result<CovenantDigest> inventoryDigest =
                FullInstallationResetMarkerPairResetDigests.CampaignInventory(
                    checkpoint.CampaignInventory);

            Result<CovenantDigest> ownerEffect =
                FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                    claim.OperationId,
                    claim.InstallationId,
                    pair.Database.TransitionId!.Value,
                    pair.Database.TaintMasterKeyVersion!.Value,
                    pair.Database.TaintFingerprint!.Value,
                    pair.Database.DatabaseMarkerDigest,
                    pair.OsMarker.MarkerBytesDigest,
                    attestation.RemediationActionDigest,
                    checkpoint.CampaignMarkerInventoryDigest);

            Result<CampaignPathFullInstallationResetInventory> inventory =
                CampaignPathFullInstallationResetInventory.Create(
                    claim.OperationId,
                    checkpoint.CampaignInventory,
                    checkpoint.CampaignMarkerInventoryDigest);

            Result<FullInstallationResetRemediationAuthorization> verified =
                _verifier.VerifyAtAcceptedTime(
                    attestation,
                    claim.InstallationId,
                    pair,
                    checkpoint.RestartProof.AcceptedAtUtc);

            if (signedDigest.IsFailure
                || pairDigest.IsFailure
                || inventoryDigest.IsFailure
                || ownerEffect.IsFailure
                || inventory.IsFailure
                || verified.IsFailure
                || !DigestEquals(
                    signedDigest.Value,
                    checkpoint.RestartProof.SignedAttestationDigest)
                || !DigestEquals(signedDigest.Value, claim.AttestationDigest)
                || !DigestEquals(
                    pairDigest.Value,
                    checkpoint.RestartProof.PairEvidenceDigest)
                || !DigestEquals(
                    inventoryDigest.Value,
                    checkpoint.CampaignMarkerInventoryDigest)
                || !DigestEquals(ownerEffect.Value, checkpoint.OwnerEffectDigest)
                || !AuthorizationEqualsClaim(
                    verified.Value,
                    claim,
                    checkpoint.RestartProof.AcceptedAtUtc))
            {

                return Inert<RevalidatedPairCheckpoint>();

            }

            return Result<RevalidatedPairCheckpoint>.Success(
                new RevalidatedPairCheckpoint(current, checkpoint));

        }
        catch (Exception)
        {

            return Inert<RevalidatedPairCheckpoint>();

        }

    }

    private static bool AuthorizationEqualsClaim(
        FullInstallationResetRemediationAuthorization authorization,
        FullInstallationResetRemediationClaimV1 claim,
        DateTimeOffset acceptedAtUtc) =>
        authorization.OperationId == claim.OperationId
        && authorization.InstallationId == claim.InstallationId
        && DigestEquals(authorization.AttestationDigest, claim.AttestationDigest)
        && DigestEquals(authorization.NonceDigest, claim.NonceDigest)
        && DigestEquals(authorization.IssuerDigest, claim.IssuerDigest)
        && authorization.AcceptedAtUtc.EqualsExact(acceptedAtUtc)
        && acceptedAtUtc.EqualsExact(claim.AcceptedAtUtc);

    private sealed record RevalidatedPairCheckpoint(
        InstallationResetActivePublication Publication,
        HostToolsMarkerPairResetCheckpointV1 Checkpoint);

    private async Task<Result<FullInstallationResetMarkerCleanupAuthority>>
        MintCleanupAuthorityAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication publication,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(publication);

        heldInstallationLock.AssertHeldFor(_activeStore.GuardedRoot);

        Result<RevalidatedPairCheckpoint> revalidated =
            await RecoverAndRevalidatePairCheckpointAsync(
                heldInstallationLock,
                publication,
                HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return Inert<FullInstallationResetMarkerCleanupAuthority>();

        }

        HostToolsMarkerPairResetCheckpointV1 checkpoint =
            revalidated.Value.Checkpoint;

        Result<CampaignPathFullInstallationResetInventory> inventory =
            CampaignPathFullInstallationResetInventory.Create(
                checkpoint.RestartProof.SignedAttestation.OperationId,
                checkpoint.CampaignInventory,
                checkpoint.CampaignMarkerInventoryDigest);

        Result<CampaignPathFullInstallationResetCleanupReceipt?> receipt =
            ReconstructCleanupReceipt(checkpoint);

        if (inventory.IsFailure || receipt.IsFailure)
        {

            return Inert<FullInstallationResetMarkerCleanupAuthority>();

        }

        MintTicket mintTicket = new();

        AuthenticatedFullInstallationResetJournalProof proof =
            AuthenticatedFullInstallationResetJournalProof.Create(
            mintTicket,
            heldInstallationLock,
            revalidated.Value.Publication,
            checkpoint,
            inventory.Value,
            receipt.Value);

        return Result<FullInstallationResetMarkerCleanupAuthority>.Success(
            FullInstallationResetMarkerCleanupAuthority.Create(
                mintTicket,
                this,
                proof));

    }

    private async Task<Result> RevalidateCleanupProofAsync(
        AuthenticatedFullInstallationResetJournalProof proof,
        CancellationToken cancellationToken)
    {

        try
        {

            proof.HeldInstallationLock.AssertHeldFor(_activeStore.GuardedRoot);

            Result<RevalidatedPairCheckpoint> revalidated =
                await RecoverAndRevalidatePairCheckpointAsync(
                    proof.HeldInstallationLock,
                    proof.Publication,
                    HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                    cancellationToken).ConfigureAwait(false);

            if (revalidated.IsFailure
                || !CheckpointEquals(proof.Checkpoint, revalidated.Value.Checkpoint))
            {

                return Inert();

            }

            Result<CampaignPathFullInstallationResetInventory> inventory =
                CampaignPathFullInstallationResetInventory.Create(
                    revalidated.Value.Checkpoint.RestartProof.SignedAttestation.OperationId,
                    revalidated.Value.Checkpoint.CampaignInventory,
                    revalidated.Value.Checkpoint.CampaignMarkerInventoryDigest);

            Result<CampaignPathFullInstallationResetCleanupReceipt?> receipt =
                ReconstructCleanupReceipt(revalidated.Value.Checkpoint);

            return inventory.IsSuccess
                && receipt.IsSuccess
                && CampaignPathFullInstallationResetContractComparer.InventoryEquals(
                    proof.Inventory,
                    inventory.Value)
                && CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
                    proof.Receipt,
                    receipt.Value)
                    ? Result.Success()
                    : Inert();

        }
        catch (Exception)
        {

            return Inert();

        }

    }

    private static Result<CampaignPathFullInstallationResetCleanupReceipt?>
        ReconstructCleanupReceipt(
            HostToolsMarkerPairResetCheckpointV1 checkpoint)
    {

        if (checkpoint.MarkerIntentCount is null
            && checkpoint.OrderedMarkerIntentIds is null
            && checkpoint.MarkerIntentVectorDigest is null
            && checkpoint.DeletedCount is null
            && checkpoint.OrphanCount is null)
        {

            return Result<CampaignPathFullInstallationResetCleanupReceipt?>.Success(null);

        }

        if (checkpoint.OrderedMarkerIntentIds is not { } intentIds
            || checkpoint.MarkerIntentVectorDigest is not { } vectorDigest
            || checkpoint.DeletedCount is not { } deletedCount
            || checkpoint.OrphanCount is not { } orphanCount)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt?>();

        }

        Result<CampaignPathFullInstallationResetCleanupReceipt> receipt =
            deletedCount == 0 && orphanCount == 0
                ? CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
                    checkpoint.RestartProof.SignedAttestation.OperationId,
                    checkpoint.OwnerEffectDigest,
                    intentIds,
                    vectorDigest)
                : CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
                    checkpoint.RestartProof.SignedAttestation.OperationId,
                    checkpoint.OwnerEffectDigest,
                    intentIds,
                    vectorDigest,
                    deletedCount,
                    orphanCount);

        if (receipt.IsFailure
            || receipt.Value.MarkerIntentCount != checkpoint.MarkerIntentCount)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt?>();

        }

        return Result<CampaignPathFullInstallationResetCleanupReceipt?>.Success(
            receipt.Value);

    }

    private sealed class MintTicket
    {
    }

    private sealed class AuthenticatedFullInstallationResetJournalProof
    {

        private AuthenticatedFullInstallationResetJournalProof(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication publication,
            HostToolsMarkerPairResetCheckpointV1 checkpoint,
            CampaignPathFullInstallationResetInventory inventory,
            CampaignPathFullInstallationResetCleanupReceipt? receipt)
        {

            HeldInstallationLock = heldInstallationLock;

            Publication = publication;

            Checkpoint = checkpoint;

            Inventory = inventory;

            Receipt = receipt;

        }

        internal ArcanumMaintenanceLock HeldInstallationLock { get; }

        internal InstallationResetActivePublication Publication { get; }

        internal HostToolsMarkerPairResetCheckpointV1 Checkpoint { get; }

        internal CampaignPathFullInstallationResetInventory Inventory { get; }

        internal CampaignPathFullInstallationResetCleanupReceipt? Receipt { get; }

        internal static AuthenticatedFullInstallationResetJournalProof Create(
            MintTicket mintTicket,
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication publication,
            HostToolsMarkerPairResetCheckpointV1 checkpoint,
            CampaignPathFullInstallationResetInventory inventory,
            CampaignPathFullInstallationResetCleanupReceipt? receipt)
        {

            ArgumentNullException.ThrowIfNull(mintTicket);

            return new AuthenticatedFullInstallationResetJournalProof(
                heldInstallationLock,
                publication,
                checkpoint,
                inventory,
                receipt);

        }

    }

    internal sealed class FullInstallationResetMarkerCleanupAuthority
    {

        private FullInstallationResetMarkerCleanupAuthority(
            HostToolsMarkerPairResetCoordinator owner,
            AuthenticatedFullInstallationResetJournalProof proof)
        {

            _owner = owner ?? throw new ArgumentNullException(nameof(owner));

            ArgumentNullException.ThrowIfNull(proof);

            _proof = proof;

        }

        private readonly HostToolsMarkerPairResetCoordinator _owner;

        private readonly AuthenticatedFullInstallationResetJournalProof _proof;

        internal static FullInstallationResetMarkerCleanupAuthority Create(
            object mintTicket,
            HostToolsMarkerPairResetCoordinator owner,
            object proof)
        {

            if (mintTicket is not MintTicket
                || proof is not AuthenticatedFullInstallationResetJournalProof typedProof)
            {

                throw new InvalidOperationException(
                    "The full-installation reset cleanup authority is unavailable.");

            }

            return new FullInstallationResetMarkerCleanupAuthority(owner, typedProof);

        }

        internal Task<Result> RevalidatePreparationAsync(
            CampaignPathFullInstallationResetCleanupPreparation preparation,
            CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
            CancellationToken cancellationToken)
        {

            ArgumentNullException.ThrowIfNull(preparation);

            return RevalidatePreparationCoreAsync(
                preparation,
                expectedReceipt,
                cancellationToken);

        }

        private async Task<Result> RevalidatePreparationCoreAsync(
            CampaignPathFullInstallationResetCleanupPreparation preparation,
            CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
            CancellationToken cancellationToken)
        {

            Result current = await _owner.RevalidateCleanupProofAsync(
                _proof,
                cancellationToken).ConfigureAwait(false);

            if (current.IsFailure)
            {

                return current;

            }

            Result<CampaignPathFullInstallationResetCleanupPreparation> expected =
                CampaignPathFullInstallationResetCleanupPreparation.Create(
                    _proof.Checkpoint.RestartProof.SignedAttestation.OperationId,
                    _proof.Checkpoint.OwnerEffectDigest,
                    _proof.Inventory);

            return expected.IsSuccess
                && CampaignPathFullInstallationResetContractComparer.PreparationEquals(
                    expected.Value,
                    preparation)
                && CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
                    _proof.Receipt,
                    expectedReceipt)
                    ? Result.Success()
                    : Inert();

        }

        internal Task<Result> RevalidateReceiptAsync(
            CampaignPathFullInstallationResetCleanupReceipt receipt,
            CancellationToken cancellationToken)
        {

            ArgumentNullException.ThrowIfNull(receipt);

            return RevalidateReceiptCoreAsync(receipt, cancellationToken);

        }

        private async Task<Result> RevalidateReceiptCoreAsync(
            CampaignPathFullInstallationResetCleanupReceipt receipt,
            CancellationToken cancellationToken)
        {

            Result current = await _owner.RevalidateCleanupProofAsync(
                _proof,
                cancellationToken).ConfigureAwait(false);

            return current.IsSuccess
                && _proof.Receipt is not null
                && CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
                    _proof.Receipt,
                    receipt)
                    ? Result.Success()
                    : Inert();

        }

    }

    private static Result Inert() =>
        Result.Failure(new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The full-installation reset marker-pair operation requires recovery."));

    private static Result<T> Inert<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The full-installation reset marker-pair operation requires recovery."));

    private static Result<T> PreJournalRefusal<T>(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return Inert<T>();

    }

    private sealed class BeginAttemptState
    {

        internal bool PairJournaledPublished { get; set; }

    }

    private static bool PublicationEquals(
        InstallationResetActivePublication left,
        InstallationResetActivePublication right) =>
        LocationEquals(left.Location, right.Location)
        && EnvelopeEquals(left.Envelope, right.Envelope)
        && DigestEquals(left.EnvelopeDigest, right.EnvelopeDigest)
        && PayloadEquals(left.Payload, right.Payload)
        && AnchorEquals(left.Anchor, right.Anchor);

    private static bool LocationEquals(
        InstallationResetActiveLocation left,
        InstallationResetActiveLocation right) =>
        string.Equals(left.ActivePath, right.ActivePath, StringComparison.Ordinal)
        && DigestEquals(left.ProfileNamespaceDigest, right.ProfileNamespaceDigest)
        && DigestEquals(
            left.GuardedParentPhysicalIdentityDigest,
            right.GuardedParentPhysicalIdentityDigest)
        && string.Equals(left.ActiveLeaf, right.ActiveLeaf, StringComparison.Ordinal)
        && DigestEquals(left.Digest, right.Digest);

    private static bool EnvelopeEquals(
        InstallationResetActiveEnvelopeV2 left,
        InstallationResetActiveEnvelopeV2 right) =>
        left.Version == right.Version
        && DigestEquals(left.ProfileNamespaceDigest, right.ProfileNamespaceDigest)
        && left.InstallationId == right.InstallationId
        && left.OperationId == right.OperationId
        && left.Revision == right.Revision
        && DigestEquals(left.PreviousEnvelopeDigest, right.PreviousEnvelopeDigest)
        && DigestEquals(left.ActiveLocationDigest, right.ActiveLocationDigest)
        && left.Scope == right.Scope
        && string.Equals(left.PlanId, right.PlanId, StringComparison.Ordinal)
        && string.Equals(left.NonceBase64Url, right.NonceBase64Url, StringComparison.Ordinal)
        && string.Equals(
            left.CiphertextBase64Url,
            right.CiphertextBase64Url,
            StringComparison.Ordinal)
        && string.Equals(
            left.AuthenticationTagBase64Url,
            right.AuthenticationTagBase64Url,
            StringComparison.Ordinal);

    private static bool AnchorEquals(
        InstallationResetActiveAnchorV1 left,
        InstallationResetActiveAnchorV1 right) =>
        left.Version == right.Version
        && left.State == right.State
        && DigestEquals(left.ProfileNamespaceDigest, right.ProfileNamespaceDigest)
        && left.InstallationId == right.InstallationId
        && left.OperationId == right.OperationId
        && left.Revision == right.Revision
        && DigestEquals(left.EnvelopeDigest, right.EnvelopeDigest)
        && DigestEquals(left.ActiveLocationDigest, right.ActiveLocationDigest);

    private static bool PayloadEquals(
        InstallationResetActivePayloadV2 left,
        InstallationResetActivePayloadV2 right) =>
        left.Version == right.Version
        && left.OperationId == right.OperationId
        && string.Equals(left.PlanId, right.PlanId, StringComparison.Ordinal)
        && left.Scope == right.Scope
        && WorkspaceEquals(left.Workspace, right.Workspace)
        && BindingEquals(left.AcceptedBinding, right.AcceptedBinding)
        && left.Phase == right.Phase
        && left.PointOfNoReturn == right.PointOfNoReturn
        && left.RowsDeleted == right.RowsDeleted
        && left.FilesDeleted == right.FilesDeleted
        && left.EstimatedBytesDeleted == right.EstimatedBytesDeleted
        && CredentialResultsEqual(left.CredentialResults, right.CredentialResults)
        && string.Equals(left.LastErrorCode, right.LastErrorCode, StringComparison.Ordinal)
        && left.DataHandoff == right.DataHandoff
        && OnlineCompletionEquals(
            left.OnlineDataCompletion,
            right.OnlineDataCompletion)
        && CheckpointEquals(
            left.HostToolsMarkerPairReset,
            right.HostToolsMarkerPairReset)
        && ClaimEquals(
            left.FullInstallationResetRemediationClaim,
            right.FullInstallationResetRemediationClaim);

    private static bool WorkspaceEquals(
        InstallationResetActiveWorkspaceV2? left,
        InstallationResetActiveWorkspaceV2? right) =>
        left is null && right is null
        || left is not null
            && right is not null
            && left.CampaignId == right.CampaignId
            && string.Equals(left.WorkspaceRoot, right.WorkspaceRoot, StringComparison.Ordinal);

    private static bool BindingEquals(
        InstallationResetActiveAcceptedBindingV2 left,
        InstallationResetActiveAcceptedBindingV2 right) =>
        string.Equals(left.BindingId, right.BindingId, StringComparison.Ordinal)
        && SequenceEquals(left.SelectedRoots, right.SelectedRoots)
        && SequenceEquals(left.ExcludedRoots, right.ExcludedRoots)
        && PreservedBackupsEqual(left.PreservedBackups, right.PreservedBackups)
        && SequenceEquals(left.CredentialAccounts, right.CredentialAccounts)
        && SequenceEquals(left.DataPlanIds, right.DataPlanIds);

    private static bool PreservedBackupsEqual(
        System.Collections.Immutable.ImmutableArray<InstallationResetActivePreservedBackupV2> left,
        System.Collections.Immutable.ImmutableArray<InstallationResetActivePreservedBackupV2> right)
    {

        if (left.IsDefault || right.IsDefault || left.Length != right.Length)
        {

            return left.IsDefault && right.IsDefault;

        }

        for (int index = 0; index < left.Length; index++)
        {

            InstallationResetActivePreservedBackupV2 first = left[index];

            InstallationResetActivePreservedBackupV2 second = right[index];

            if (!string.Equals(
                    first.CanonicalPath,
                    second.CanonicalPath,
                    StringComparison.Ordinal)
                || !string.Equals(
                    first.Identity.Value,
                    second.Identity.Value,
                    StringComparison.Ordinal)
                || first.Identity.Length != second.Identity.Length
                || first.Identity.HardLinkCount != second.Identity.HardLinkCount)
            {

                return false;

            }

        }

        return true;

    }

    private static bool CredentialResultsEqual(
        System.Collections.Immutable.ImmutableArray<InstallationResetActiveCredentialResultV2> left,
        System.Collections.Immutable.ImmutableArray<InstallationResetActiveCredentialResultV2> right)
    {

        if (left.IsDefault || right.IsDefault || left.Length != right.Length)
        {

            return left.IsDefault && right.IsDefault;

        }

        for (int index = 0; index < left.Length; index++)
        {

            InstallationResetActiveCredentialResultV2 first = left[index];

            InstallationResetActiveCredentialResultV2 second = right[index];

            if (!string.Equals(first.Account, second.Account, StringComparison.Ordinal)
                || first.Status != second.Status
                || !string.Equals(first.ErrorCode, second.ErrorCode, StringComparison.Ordinal))
            {

                return false;

            }

        }

        return true;

    }

    private static bool OnlineCompletionEquals(
        InstallationResetActiveOnlineCompletionV2? left,
        InstallationResetActiveOnlineCompletionV2? right) =>
        left is null && right is null
        || left is not null
            && right is not null
            && left.ServerOperationId == right.ServerOperationId
            && left.RequestedOperationId == right.RequestedOperationId
            && string.Equals(left.DataPlanId, right.DataPlanId, StringComparison.Ordinal)
            && left.RowsDeleted == right.RowsDeleted
            && left.FilesDeleted == right.FilesDeleted
            && left.EstimatedBytesDeleted == right.EstimatedBytesDeleted
            && left.DerivedRecordsDeleted == right.DerivedRecordsDeleted;

    private static bool CheckpointEquals(
        HostToolsMarkerPairResetCheckpointV1? left,
        HostToolsMarkerPairResetCheckpointV1? right) =>
        left is null && right is null
        || left is not null
            && right is not null
            && left.Version == right.Version
            && left.Phase == right.Phase
            && RestartProofEquals(left.RestartProof, right.RestartProof)
            && CampaignInventoryEquals(
                left.CampaignInventory,
                right.CampaignInventory)
            && DigestEquals(
                left.CampaignMarkerInventoryDigest,
                right.CampaignMarkerInventoryDigest)
            && DigestEquals(left.OwnerEffectDigest, right.OwnerEffectDigest)
            && left.MarkerIntentCount == right.MarkerIntentCount
            && IntentIdsEqual(
                left.OrderedMarkerIntentIds,
                right.OrderedMarkerIntentIds)
            && OptionalDigestEquals(
                left.MarkerIntentVectorDigest,
                right.MarkerIntentVectorDigest)
            && left.DeletedCount == right.DeletedCount
            && left.OrphanCount == right.OrphanCount;

    private static bool RestartProofEquals(
        FullInstallationResetRestartProofV1 left,
        FullInstallationResetRestartProofV1 right) =>
        left.Version == right.Version
        && SignedProjectionEquals(left.SignedAttestation, right.SignedAttestation)
        && left.AcceptedAtUtc.EqualsExact(right.AcceptedAtUtc)
        && DigestEquals(left.SignedAttestationDigest, right.SignedAttestationDigest)
        && DatabaseEvidenceEquals(
            left.DatabaseMarkerEvidence,
            right.DatabaseMarkerEvidence)
        && OsEvidenceEquals(left.OsMarkerEvidence, right.OsMarkerEvidence)
        && DigestEquals(left.PairEvidenceDigest, right.PairEvidenceDigest);

    private static bool SignedProjectionEquals(
        FullInstallationResetSignedAttestationProjectionV1 left,
        FullInstallationResetSignedAttestationProjectionV1 right) =>
        left.Version == right.Version
        && left.OperationId == right.OperationId
        && left.InstallationId == right.InstallationId
        && left.HostToolsTransitionId == right.HostToolsTransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && DigestEquals(left.AuthorityFingerprint, right.AuthorityFingerprint)
        && DigestEquals(left.DatabaseMarkerDigest, right.DatabaseMarkerDigest)
        && DigestEquals(left.OsMarkerDigest, right.OsMarkerDigest)
        && DigestEquals(left.RemediationActionDigest, right.RemediationActionDigest)
        && string.Equals(left.NonceBase64Url, right.NonceBase64Url, StringComparison.Ordinal)
        && string.Equals(left.Issuer, right.Issuer, StringComparison.Ordinal)
        && left.IssuedAtUtc.EqualsExact(right.IssuedAtUtc)
        && left.ExpiresAtUtc.EqualsExact(right.ExpiresAtUtc)
        && string.Equals(
            left.SignatureBase64Url,
            right.SignatureBase64Url,
            StringComparison.Ordinal);

    private static bool DatabaseEvidenceEquals(
        HostProcessToolsDatabaseMarkerEvidence left,
        HostProcessToolsDatabaseMarkerEvidence right) =>
        string.Equals(
            left.InstallationIdentity,
            right.InstallationIdentity,
            StringComparison.Ordinal)
        && left.State == right.State
        && left.TransitionId == right.TransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && OptionalDigestEquals(left.TaintFingerprint, right.TaintFingerprint)
        && OptionalDigestEquals(left.TaintIdentityDigest, right.TaintIdentityDigest)
        && DigestEquals(left.DatabaseMarkerDigest, right.DatabaseMarkerDigest);

    private static bool OsEvidenceEquals(
        HostProcessToolsOsMarkerEvidence left,
        HostProcessToolsOsMarkerEvidence right) =>
        string.Equals(
            left.InstallationIdentity,
            right.InstallationIdentity,
            StringComparison.Ordinal)
        && left.TransitionId == right.TransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && DigestEquals(left.TaintFingerprint, right.TaintFingerprint)
        && DigestEquals(left.MarkerBytesDigest, right.MarkerBytesDigest)
        && DigestEquals(left.DurableIdentityDigest, right.DurableIdentityDigest)
        && DigestEquals(left.TaintIdentityDigest, right.TaintIdentityDigest);

    private static bool CampaignInventoryEquals(
        System.Collections.Immutable.ImmutableArray<CampaignMarkerInventoryEntryV1> left,
        System.Collections.Immutable.ImmutableArray<CampaignMarkerInventoryEntryV1> right)
    {

        if (left.IsDefault || right.IsDefault || left.Length != right.Length)
        {

            return left.IsDefault && right.IsDefault;

        }

        for (int index = 0; index < left.Length; index++)
        {

            CampaignMarkerInventoryEntryV1 first = left[index];

            CampaignMarkerInventoryEntryV1 second = right[index];

            if (first.CampaignId != second.CampaignId
                || first.PriorPathRevision != second.PriorPathRevision
                || !DigestEquals(first.MarkerDigest, second.MarkerDigest)
                || !DigestEquals(
                    first.IndexedPhysicalIdentityDigest,
                    second.IndexedPhysicalIdentityDigest)
                || !DigestEquals(
                    first.CanonicalDisplayPathDigest,
                    second.CanonicalDisplayPathDigest)
                || !DigestEquals(
                    first.SameHandleOwnershipEvidenceDigest,
                    second.SameHandleOwnershipEvidenceDigest))
            {

                return false;

            }

        }

        return true;

    }

    private static bool IntentIdsEqual(
        System.Collections.Immutable.ImmutableArray<Guid>? left,
        System.Collections.Immutable.ImmutableArray<Guid>? right)
    {

        if (left is null || right is null)
        {

            return left is null && right is null;

        }

        if (left.Value.IsDefault
            || right.Value.IsDefault
            || left.Value.Length != right.Value.Length)
        {

            return left.Value.IsDefault && right.Value.IsDefault;

        }

        for (int index = 0; index < left.Value.Length; index++)
        {

            if (left.Value[index] != right.Value[index])
            {

                return false;

            }

        }

        return true;

    }

    private static bool ClaimEquals(
        FullInstallationResetRemediationClaimV1? left,
        FullInstallationResetRemediationClaimV1? right) =>
        left is null && right is null
        || left is not null
            && right is not null
            && left.Version == right.Version
            && left.OperationId == right.OperationId
            && left.InstallationId == right.InstallationId
            && DigestEquals(left.AttestationDigest, right.AttestationDigest)
            && DigestEquals(left.NonceDigest, right.NonceDigest)
            && DigestEquals(left.IssuerDigest, right.IssuerDigest)
            && left.AcceptedAtUtc.EqualsExact(right.AcceptedAtUtc);

    private static bool OptionalDigestEquals(
        Core.Covenant.CovenantDigest? left,
        Core.Covenant.CovenantDigest? right) =>
        left is null && right is null
        || left is { } first
            && right is { } second
            && DigestEquals(first, second);

    private static bool SequenceEquals(
        System.Collections.Immutable.ImmutableArray<string> left,
        System.Collections.Immutable.ImmutableArray<string> right)
    {

        if (left.IsDefault || right.IsDefault || left.Length != right.Length)
        {

            return left.IsDefault && right.IsDefault;

        }

        for (int index = 0; index < left.Length; index++)
        {

            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {

                return false;

            }

        }

        return true;

    }

    private static bool DigestEquals(
        Core.Covenant.CovenantDigest left,
        Core.Covenant.CovenantDigest right) =>
        left.IsValid
        && right.IsValid
        && left.Bytes.AsSpan().SequenceEqual(right.Bytes);

}
