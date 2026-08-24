using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// What the coordinator does once both host-tools markers are provably gone: prepare the Campaign
/// cleanup vector, publish it, reconcile it under an authority the publication has not yet stale, and
/// release the roots this process retained.
/// </summary>
/// <remarks>
/// Every assertion here is about the publication sequence and the collaborators, not about the
/// returned code. Full installation reset still ends in recovery-required on every path, so a suite
/// that read the result would be unable to tell a completed cleanup from a refused one.
/// </remarks>
public sealed partial class HostToolsMarkerPairResetCoordinatorTests
{

    [Fact]
    public async Task Cleanup_prepares_publishes_the_receipt_and_reconciles_under_a_fresh_authority()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = CreateGuardedRoot("cleanup-continuation");

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Claim(current);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            ImmutableArray<Guid> intentIds = [Guid.NewGuid(), Guid.NewGuid()];

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId, 2))
            {
                PreparedIntentIds = intentIds,
                ReconciledDeletedCount = 1,
                ReconciledOrphanCount = 1,
            };

            HostToolsMarkerPairResetCoordinator subject = CreateSubject(
                database,
                store,
                lifecycle,
                events,
                claim);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            // Still recovery-required: deleting both markers and terminalizing the Campaign children
            // is not the whole of a full installation reset.
            Assert.True(result.IsFailure);

            Assert.Equal(1, lifecycle.PrepareCalls);

            Assert.Equal(1, lifecycle.ReconcileCalls);

            // Prepare, publish, reconcile, publish, release — in that order and no other. A
            // reconciliation that ran before its receipt was durable would be terminalizing children
            // no restart could find.
            Assert.Equal(
                [
                    "advance:PairAbsenceVerified",
                    "prepare",
                    "advance:PairAbsenceVerified",
                    "reconcile",
                    "advance:PairAbsenceVerified",
                    "release",
                ],
                events[events.IndexOf("advance:PairAbsenceVerified")..]);

            // The authority that committed the prepared children was minted against a publication the
            // receipt then superseded. Carrying it into reconciliation would be acting on a journal
            // revision that has moved.
            Assert.NotNull(lifecycle.PrepareAuthority);

            Assert.NotNull(lifecycle.ReconcileAuthority);

            Assert.NotSame(lifecycle.PrepareAuthority, lifecycle.ReconcileAuthority);

            // Asserted directly rather than inferred from two objects having been minted: the
            // authority that committed the prepared children no longer authenticates at all.
            Assert.True(
                (await lifecycle.PrepareAuthority.RevalidateReceiptAsync(
                    lifecycle.ReconcilePreparedReceipt!,
                    CancellationToken.None)).IsFailure);

            Assert.Null(lifecycle.PrepareExpectedReceipt);

            Assert.Equal(
                intentIds.ToArray(),
                lifecycle.ReconcilePreparedReceipt!.OrderedMarkerIntentIds.ToArray());

            Assert.Equal(0ul, lifecycle.ReconcilePreparedReceipt.DeletedCount);

            HostToolsMarkerPairResetCheckpointV1 terminal = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    store.LastNext!.HostToolsMarkerPairReset);

            Assert.Equal(HostToolsMarkerPairResetPhase.PairAbsenceVerified, terminal.Phase);

            Assert.Equal(2ul, terminal.MarkerIntentCount);

            Assert.Equal(intentIds.ToArray(), terminal.OrderedMarkerIntentIds!.Value.ToArray());

            Assert.Equal(
                Value(FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                    intentIds)),
                terminal.MarkerIntentVectorDigest);

            Assert.Equal(1ul, terminal.DeletedCount);

            Assert.Equal(1ul, terminal.OrphanCount);

            Assert.Equal(1, lifecycle.ReleaseCalls);

            Assert.Equal(claim.OperationId, lifecycle.ReleasedOwnerOperationId);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Cleanup_publishes_once_when_reconciliation_changes_nothing()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = CreateGuardedRoot("cleanup-idempotent");

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Claim(current);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            // A proven-empty inventory: the prepared receipt and the terminal receipt are the same
            // frozen vector with the same zero counts, so the second publication would say nothing.
            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId));

            HostToolsMarkerPairResetCoordinator subject = CreateSubject(
                database,
                store,
                lifecycle,
                events,
                claim);

            _ = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.Equal(1, lifecycle.PrepareCalls);

            Assert.Equal(1, lifecycle.ReconcileCalls);

            Assert.Equal(
                ["advance:PairAbsenceVerified", "prepare", "advance:PairAbsenceVerified", "reconcile", "release"],
                events[events.IndexOf("advance:PairAbsenceVerified")..]);

            Assert.Equal(1, lifecycle.ReleaseCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("reconcile")]
    public async Task Cleanup_releases_retained_roots_once_on_failure(string failure)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = CreateGuardedRoot("cleanup-failure");

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Claim(current);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId, 1))
            {
                PreparedIntentIds = [Guid.NewGuid()],
                ReconciledDeletedCount = 1,
                FailPrepare = failure == "prepare",
                FailReconcile = failure == "reconcile",
            };

            HostToolsMarkerPairResetCoordinator subject = CreateSubject(
                database,
                store,
                lifecycle,
                events,
                claim);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, lifecycle.ReleaseCalls);

            Assert.Equal("release", events[^1]);

            Assert.Equal(
                failure == "reconcile" ? 1 : 0,
                lifecycle.ReconcileCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Cleanup_survives_a_release_that_throws_and_still_refuses()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = CreateGuardedRoot("cleanup-release-throws");

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Claim(current);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId),
                releaseException: new IOException("A retained root refused to close."));

            HostToolsMarkerPairResetCoordinator subject = CreateSubject(
                database,
                store,
                lifecycle,
                events,
                claim);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            // Release is best effort. A disposal failure cannot replace the refusal the operation
            // already earned, and it certainly cannot escape as an exception.
            Assert.True(result.IsFailure);

            Assert.Equal(1, lifecycle.ReleaseCalls);

            Assert.Equal(1, lifecycle.ReconcileCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Resume_from_a_terminal_receipt_republishes_nothing_and_prepares_nothing()
    {

        // The pair is already gone, so the live row has to read back clean for the resume to accept
        // the checkpoint at all.
        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = CreateGuardedRoot("cleanup-terminal-retry");

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            ImmutableArray<Guid> intentIds = [Guid.NewGuid(), Guid.NewGuid()];

            InstallationResetActivePublication current = CleanupCheckpointPublication(
                intentIds,
                deletedCount: 1,
                orphanCount: 1);

            FullInstallationResetRemediationClaimV1 claim = Claim(current);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId, 2))
            {
                PreparedIntentIds = intentIds,
                ReconciledDeletedCount = 1,
                ReconciledOrphanCount = 1,
            };

            HostToolsMarkerPairResetCoordinator subject = CreateSubject(
                database,
                store,
                lifecycle,
                events,
                claim);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            // The journal already holds the terminal vector, so there is nothing to prepare and
            // nothing to publish. A retry that rewrote the same receipt would advance the envelope
            // revision for no reason and invalidate every proof bound to the old one.
            Assert.Equal(0, lifecycle.PrepareCalls);

            Assert.Equal(1, lifecycle.ReconcileCalls);

            Assert.DoesNotContain("advance:PairAbsenceVerified", events);

            Assert.Null(store.LastNext);

            Assert.Equal(1ul, lifecycle.ReconcilePreparedReceipt!.DeletedCount);

            Assert.Equal(1ul, lifecycle.ReconcilePreparedReceipt.OrphanCount);

            Assert.Equal(1, lifecycle.ReleaseCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Cleanup_borrows_the_one_core_connection_the_pair_effects_used()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = CreateGuardedRoot("cleanup-connection");

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Claim(current);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId, 1))
            {
                PreparedIntentIds = [Guid.NewGuid()],
                ReconciledDeletedCount = 1,
            };

            HostToolsMarkerPairResetCoordinator subject = CreateSubject(
                database,
                store,
                lifecycle,
                events,
                claim);

            _ = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            // One non-pooled core connection for the whole operation. A cleanup that opened its own
            // would be reading a different snapshot than the marker effects just committed on.
            Assert.NotNull(lifecycle.InventoryConnection);

            Assert.Same(lifecycle.InventoryConnection, lifecycle.PrepareConnection);

            Assert.Same(lifecycle.InventoryConnection, lifecycle.ReconcileConnection);

            // The caller owns the transaction and it belongs to the borrowed connection, which is
            // what the seam refuses to proceed without.
            Assert.True(lifecycle.PrepareTransactionBoundToConnection);

            // Live while reconciliation ran. The session owns the connection for the whole
            // operation and closes it only once the coordinator is finished with it.
            Assert.Equal(
                System.Data.ConnectionState.Open,
                lifecycle.ReconcileConnectionState);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    /// <summary>
    /// A pair-absence checkpoint that already carries a terminal Campaign cleanup receipt.
    /// </summary>
    private static InstallationResetActivePublication CleanupCheckpointPublication(
        ImmutableArray<Guid> intentIds,
        ulong deletedCount,
        ulong orphanCount)
    {

        InstallationResetActivePublication claimPublication = Publication();

        FullInstallationResetRemediationClaimV1 claim = Claim(claimPublication);

        FullInstallationResetExternalRemediationAttestation attestation =
            Attestation(claim.OperationId);

        HostProcessToolsMatchedPair pair = new(
            TaintedDatabaseEvidence(),
            MatchedOsEvidence());

        CampaignPathFullInstallationResetInventory inventory =
            Inventory(claim.OperationId, intentIds.Length);

        CovenantDigest signedDigest = Value(
            FullInstallationResetRemediationAttestationDigest.Calculate(attestation));

        HostToolsMarkerPairResetCheckpointV1 checkpoint = new(
            1,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified,
            new FullInstallationResetRestartProofV1(
                1,
                FullInstallationResetSignedAttestationProjectionV1.FromAttestation(attestation),
                claim.AcceptedAtUtc,
                signedDigest,
                pair.Database,
                pair.OsMarker,
                Value(FullInstallationResetMarkerPairResetDigests.PairEvidence(pair))),
            inventory.Entries,
            inventory.InventoryDigest,
            Value(FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                claim.OperationId,
                claim.InstallationId,
                pair.Database.TransitionId!.Value,
                pair.Database.TaintMasterKeyVersion!.Value,
                pair.Database.TaintFingerprint!.Value,
                pair.Database.DatabaseMarkerDigest,
                pair.OsMarker.MarkerBytesDigest,
                attestation.RemediationActionDigest,
                inventory.InventoryDigest)),
            checked((ulong)intentIds.Length),
            intentIds,
            Value(FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(intentIds)),
            deletedCount,
            orphanCount);

        CovenantDigest envelopeDigest = Digest(0x62);

        return new InstallationResetActivePublication(
            claimPublication.Location,
            claimPublication.Envelope with
            {
                Revision = claimPublication.Envelope.Revision + 1,
                PreviousEnvelopeDigest = claimPublication.EnvelopeDigest,
            },
            envelopeDigest,
            InstallationResetActivePayloadV2.FromRecord(
                claimPublication.Payload.ToRecord() with
                {
                    HostToolsMarkerPairReset = checkpoint,
                }),
            claimPublication.Anchor with
            {
                Revision = claimPublication.Anchor.Revision + 1,
                EnvelopeDigest = envelopeDigest,
            });

    }

    private static string CreateGuardedRoot(string leaf)
    {

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-{leaf}-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(guardedRoot);

        return guardedRoot;

    }

    private static FullInstallationResetRemediationClaimV1 Claim(
        InstallationResetActivePublication publication) =>
        Assert.IsType<FullInstallationResetRemediationClaimV1>(
            publication.Payload.FullInstallationResetRemediationClaim);

    private static HostToolsMarkerPairResetCoordinator CreateSubject(
        CovenantSchemaScratchDatabase database,
        RecordingActiveStore store,
        RecordingFullResetLifecycle lifecycle,
        List<string>? events,
        FullInstallationResetRemediationClaimV1 claim)
    {

        HostProcessToolsMatchedPair pair = new(
            TaintedDatabaseEvidence(),
            MatchedOsEvidence());

        return new HostToolsMarkerPairResetCoordinator(
            store,
            events is null
                ? new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance)
                : new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
            new SuccessfulReadiness(),
            new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched,
                pair)),
            new AuthorizingVerifier(Authorization(claim)),
            lifecycle,
            events is null
                ? new RecordingOsPort(
                    openResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability()))
                : new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

    }

    /// <summary>
    /// The authenticated inventory the checkpoint names, with the canonical Campaign ordering.
    /// </summary>
    private static CampaignPathFullInstallationResetInventory Inventory(
        Guid operationId,
        int campaignCount)
    {

        List<CampaignMarkerInventoryEntryV1> entries = [];

        for (int index = 0; index < campaignCount; index++)
        {

            entries.Add(new CampaignMarkerInventoryEntryV1(
                Guid.NewGuid(),
                index + 1,
                Digest((byte)(0x80 + index)),
                Digest((byte)(0x90 + index)),
                Digest((byte)(0xA0 + index)),
                Digest((byte)(0xB0 + index))));

        }

        entries.Sort(static (left, right) =>
            FullInstallationResetCanonicalEvidenceV1.CompareGuid(
                left.CampaignId,
                right.CampaignId));

        ImmutableArray<CampaignMarkerInventoryEntryV1> ordered = [.. entries];

        return Value(CampaignPathFullInstallationResetInventory.Create(
            operationId,
            ordered,
            Value(FullInstallationResetMarkerPairResetDigests.CampaignInventory(ordered))));

    }

}
