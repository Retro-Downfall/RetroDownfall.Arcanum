using Microsoft.Data.Sqlite;

using System.Buffers.Text;
using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Backup;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed partial class HostToolsMarkerPairResetCoordinatorTests
{

    [Fact]
    public async Task Begin_requires_the_callers_exact_held_installation_lock_and_authenticated_claim_publication()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-coordinator-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            string otherRoot = Path.Combine(
                Path.GetTempPath(),
                $"arcanum-pair-coordinator-other-{Guid.NewGuid():N}");

            Directory.CreateDirectory(otherRoot);

            try
            {

                using ArcanumMaintenanceLock wrongLock = Assert.IsType<ArcanumMaintenanceLock>(
                    ArcanumMaintenanceLock.TryAcquire(otherRoot));

                InstallationResetActivePublication current = Publication();

                InstallationResetActivePublication stale = Publication(
                    operationId: current.Payload.OperationId);

                RecordingActiveStore store = new(guardedRoot, current);

                RecordingOsPort os = new();

                HostToolsMarkerPairResetCoordinator subject = new(
                    store,
                    new HostToolsMarkerPairResetDatabase(
                        database.MaintenanceConnections(),
                        CovenantSqliteConnectionInitializer.Instance),
                    new SuccessfulReadiness(),
                    new HostProcessToolsMarkerPairJoiner(),
                    new RejectingVerifier(),
                    new FakeCampaignPathMarkerLifecycle(),
                    os);

                await Assert.ThrowsAnyAsync<Exception>(() => subject.BeginAsync(
                    wrongLock,
                    current,
                    Attestation(current.Payload.OperationId),
                    CancellationToken.None));

                Result<InstallationResetActivePublication> staleResult =
                    await subject.BeginAsync(
                        heldLock,
                        stale,
                        Attestation(stale.Payload.OperationId),
                        CancellationToken.None);

                Assert.True(staleResult.IsFailure);

                Assert.Equal(0, os.OpenCalls);

            }
            finally
            {

                Directory.Delete(otherRoot, recursive: true);

            }

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Begin_rejects_an_authenticated_pair_checkpoint_before_fresh_os_or_database_admission()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-begin-claim-only-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            List<string> events = [];

            RecordingJoiner joiner = new(new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched,
                null));

            RecordingFullResetLifecycle lifecycle = new();

            RecordingOsPort os = new(events);

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    new RecordingMaintenanceConnections(
                        database.MaintenanceConnections(),
                        events),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                joiner,
                new RejectingVerifier(),
                lifecycle,
                os);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, os.OpenCalls);

            Assert.DoesNotContain("database", events);

            Assert.Equal(0, joiner.Calls);

            Assert.Equal(0, lifecycle.InventoryCalls);

            Assert.DoesNotContain(
                events,
                value => value.StartsWith("advance:", StringComparison.Ordinal));

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Begin_rejects_an_authenticated_claimless_publication_before_fresh_os_or_database_admission()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-begin-claim-required-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = WithoutRemediationClaim(
                Publication());

            Assert.Null(current.Payload.FullInstallationResetRemediationClaim);

            Assert.Null(current.Payload.HostToolsMarkerPairReset);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            RecordingJoiner joiner = new(new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched,
                pair));

            RecordingFullResetLifecycle lifecycle = new();

            FakeOsCapability capability = new();

            RecordingOsPort os = new(
                events,
                HostToolsMarkerPairResetOsOpenResult.Opened(
                    pair.OsMarker,
                    capability));

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    new RecordingMaintenanceConnections(
                        database.MaintenanceConnections(),
                        events),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                joiner,
                new RejectingVerifier(),
                lifecycle,
                os);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, os.OpenCalls);

            Assert.Equal(0, capability.DisposeCalls);

            Assert.DoesNotContain("database", events);

            Assert.Equal(0, joiner.Calls);

            Assert.Equal(0, lifecycle.InventoryCalls);

            Assert.Null(store.LastNext);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public async Task Begin_refuses_every_noncanonical_claim_only_field_before_any_downstream_call(
        int mutation)
    {

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-begin-claim-shape-{mutation}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            InstallationResetActivePayloadV2 payload = current.Payload;

            current = mutation switch
            {
                0 => WithPayload(current, payload with { Version = 3 }),
                1 => WithPayload(current, payload with { Scope = InstallationResetScope.Global }),
                2 => WithPayload(current, payload with { Phase = InstallationResetPhase.DataResetComplete }),
                3 => WithPayload(current, payload with { PointOfNoReturn = true }),
                4 => WithPayload(current, payload with { RowsDeleted = 1 }),
                5 => WithPayload(current, payload with { FilesDeleted = 1 }),
                6 => WithPayload(current, payload with { EstimatedBytesDeleted = 1 }),
                7 => WithPayload(
                    current,
                    payload with
                    {
                        CredentialResults =
                        [
                            new InstallationResetActiveCredentialResultV2(
                                "credential",
                                InstallationResetItemStatus.Deleted,
                                ErrorCode: null),
                        ],
                    }),
                8 => WithPayload(
                    current,
                    payload with
                    {
                        DataHandoff = InstallationResetDataHandoff.HostFactoryErasure,
                    }),
                9 => WithPayload(
                    current,
                    payload with
                    {
                        OnlineDataCompletion = new InstallationResetActiveOnlineCompletionV2(
                            Guid.NewGuid(),
                            payload.OperationId,
                            "data-plan",
                            0,
                            0,
                            0,
                            0),
                    }),
                10 => CheckpointPublication(HostToolsMarkerPairResetPhase.PairJournaled),
                11 => WithPayload(current, payload with { LastErrorCode = null }),
                12 => WithPayload(
                    current,
                    payload with
                    {
                        FullInstallationResetRemediationClaim = payload
                            .FullInstallationResetRemediationClaim! with
                        {
                            Version = 2,
                        },
                    }),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };

            RecordingActiveStore store = new(guardedRoot, current);

            RecordingOsPort os = new();

            RecordingJoiner joiner = new(new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched,
                null));

            RejectingVerifier verifier = new();

            RecordingFullResetLifecycle lifecycle = new();

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new ThrowingDatabase(),
                new SuccessfulReadiness(),
                joiner,
                verifier,
                lifecycle,
                os);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

            Assert.Equal(1, store.RecoverCalls);

            Assert.Equal(0, os.OpenCalls);

            Assert.Equal(0, joiner.Calls);

            Assert.Equal(0, verifier.RecoveryCalls);

            Assert.Equal(0, lifecycle.InventoryCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Begin_opens_and_retains_the_os_capability_before_opening_or_reading_the_database()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-order-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            List<string> events = [];

            FakeOsCapability capability = new();

            RecordingOsPort os = new(
                events,
                HostToolsMarkerPairResetOsOpenResult.Opened(
                    OsEvidence(),
                    capability));

            ICovenantMaintenanceConnectionFactory connections =
                new RecordingMaintenanceConnections(
                    database.MaintenanceConnections(),
                    events);

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current),
                new HostToolsMarkerPairResetDatabase(
                    connections,
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new HostProcessToolsMarkerPairJoiner(),
                new RejectingVerifier(),
                new FakeCampaignPathMarkerLifecycle(),
                os);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(["os", "database"], events.Take(2));

            Assert.Equal(1, capability.DisposeCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(HostProcessToolsMarkerPairDisposition.Clean, false, 0)]
    [InlineData(HostProcessToolsMarkerPairDisposition.PendingBlocked, false, 0)]
    [InlineData(HostProcessToolsMarkerPairDisposition.MismatchBlocked, false, 0)]
    [InlineData(HostProcessToolsMarkerPairDisposition.TaintedMatched, false, 0)]
    [InlineData(HostProcessToolsMarkerPairDisposition.TaintedMatched, true, 1)]
    public async Task Begin_calls_the_shared_joiner_and_accepts_only_tainted_matched_with_a_nonnull_pair(
        HostProcessToolsMarkerPairDisposition disposition,
        bool carriesPair,
        int expectedVerifierCalls)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-join-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            HostProcessToolsDatabaseMarkerEvidence databaseEvidence = TaintedDatabaseEvidence();

            HostProcessToolsOsMarkerEvidence osEvidence = MatchedOsEvidence();

            HostProcessToolsMatchedPair pair = new(databaseEvidence, osEvidence);

            RecordingJoiner joiner = new(new HostProcessToolsMarkerPairJoinResult(
                disposition,
                carriesPair ? pair : null));

            RejectingVerifier verifier = new();

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current),
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                joiner,
                verifier,
                new FakeCampaignPathMarkerLifecycle(),
                new RecordingOsPort(
                    openResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        osEvidence,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, joiner.Calls);

            Assert.Equal(expectedVerifierCalls, verifier.RecoveryCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 0)]
    public async Task Begin_reverifies_the_signed_statement_at_claim_accepted_time_against_the_reopened_pair(
        int authorizationMutation,
        int expectedInventoryCalls)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-verify-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            FullInstallationResetRemediationAuthorization accepted = new(
                authorizationMutation == 0 ? Guid.NewGuid() : claim.OperationId,
                authorizationMutation == 1 ? Guid.NewGuid() : claim.InstallationId,
                authorizationMutation == 2 ? Digest(0x91) : claim.AttestationDigest,
                authorizationMutation == 3 ? Digest(0x92) : claim.NonceDigest,
                authorizationMutation == 4 ? Digest(0x93) : claim.IssuerDigest,
                authorizationMutation == 5
                    ? claim.AcceptedAtUtc.AddSeconds(1)
                    : claim.AcceptedAtUtc);

            if (authorizationMutation == 6)
            {

                accepted = new FullInstallationResetRemediationAuthorization(
                    claim.OperationId,
                    claim.InstallationId,
                    claim.AttestationDigest,
                    claim.NonceDigest,
                    claim.IssuerDigest,
                    claim.AcceptedAtUtc.ToOffset(TimeSpan.FromHours(1)));

            }

            AuthorizingVerifier verifier = new(accepted);

            RecordingFullResetLifecycle lifecycle = new();

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            FullInstallationResetExternalRemediationAttestation attestation =
                Attestation(current.Payload.OperationId);

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current),
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                verifier,
                lifecycle,
                new RecordingOsPort(
                    openResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                attestation,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Same(attestation, verifier.Attestation);

            Assert.Equal(claim.InstallationId, verifier.InstallationId);

            Assert.Equal(claim.AcceptedAtUtc, verifier.AcceptedAtUtc);

            Assert.True(DatabaseEvidenceEqual(
                pair.Database,
                verifier.Pair!.Database));

            Assert.True(OsEvidenceEqual(pair.OsMarker, verifier.Pair.OsMarker));

            Assert.Equal(expectedInventoryCalls, lifecycle.InventoryCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Begin_completes_campaign_inventory_before_pair_journal_publication()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-inventory-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events);

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId));

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                lifecycle,
                new RecordingOsPort(
                    openResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(
                ["inventory", "revalidate", "advance:PairJournaled", "release"],
                events);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    store.LastNext!.HostToolsMarkerPairReset);

            Assert.Equal(HostToolsMarkerPairResetPhase.PairJournaled, checkpoint.Phase);

            Assert.Empty(checkpoint.CampaignInventory);

            Assert.Null(checkpoint.MarkerIntentCount);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData("digest")]
    [InlineData("owner")]
    public async Task Prejournal_digest_or_owner_effect_failure_releases_attempt_roots_exactly_once(
        string failure) =>
        await AssertAttemptRootReleaseBoundaryAsync(failure);

    [Theory]
    [InlineData("revalidation")]
    [InlineData("publication")]
    public async Task Prejournal_revalidation_or_publication_failure_releases_attempt_roots_exactly_once(
        string failure) =>
        await AssertAttemptRootReleaseBoundaryAsync(failure);

    [Theory]
    [InlineData("inventory-failure")]
    [InlineData("inventory-exception")]
    public async Task Prejournal_inventory_failure_or_exception_still_requests_owner_release(
        string failure) =>
        await AssertAttemptRootReleaseBoundaryAsync(failure);

    [Fact]
    public async Task Prejournal_cancellation_releases_attempt_roots_and_propagates() =>
        await AssertAttemptRootReleaseBoundaryAsync("cancellation");

    [Fact]
    public async Task Successful_pair_journal_publication_and_later_failure_do_not_release_attempt_roots() =>
        await AssertAttemptRootReleaseBoundaryAsync("post-journal");

    [Fact]
    public async Task Pair_recovery_uses_frozen_campaign_inventory_after_pair_journaled()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-frozen-inventory-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId),
                failRevalidateOnCall: 2);

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                lifecycle,
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, lifecycle.RevalidateCalls);

            Assert.Equal(
                HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                store.CurrentPublication.Payload.HostToolsMarkerPairReset!.Phase);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Begin_publishes_pair_journaled_before_either_marker_effect()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-before-effects-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId));

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                lifecycle,
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            int journaled = events.IndexOf("advance:PairJournaled");

            int databaseEffect = events.IndexOf("database-effect");

            Assert.True(journaled >= 0);

            Assert.True(databaseEffect > journaled);

            Assert.DoesNotContain("os-effect", events.Take(databaseEffect));

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Fresh_pair_journal_revalidates_full_checkpoint_before_first_database_effect()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-fresh-post-journal-auth-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            List<string> events = [];

            FirstThenRejectingVerifier verifier = new(Authorization(claim));

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                verifier,
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(2, verifier.Calls);

            Assert.Contains("advance:PairJournaled", events);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("advance:DatabaseMarkerCompareDeleted", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Database_effect_advances_only_to_database_marker_compare_deleted_after_durability()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-database-phase-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            int effect = events.IndexOf("database-effect");

            int publication = events.IndexOf(
                "advance:DatabaseMarkerCompareDeleted");

            Assert.True(effect >= 0);

            Assert.True(publication > effect);

            int osEffect = events.IndexOf("os-effect");

            Assert.True(osEffect < 0 || osEffect > publication);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Os_effect_advances_only_to_os_marker_compare_deleted_after_exact_delete_and_absence()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-os-phase-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            FakeOsCapability capability = new();

            RecordingOsPort os = new(
                events,
                HostToolsMarkerPairResetOsOpenResult.Opened(
                    pair.OsMarker,
                    capability));

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            int databasePublished = events.IndexOf(
                "advance:DatabaseMarkerCompareDeleted");

            int osEffect = events.IndexOf("os-effect");

            int osAbsence = events.IndexOf("os-absence");

            int osPublished = events.IndexOf(
                "advance:OsMarkerCompareDeleted");

            Assert.True(databasePublished >= 0);

            Assert.True(osEffect > databasePublished);

            Assert.True(osPublished > osEffect);

            Assert.True(osAbsence > osPublished);

            Assert.Same(capability, os.DeleteCapability);

            Assert.True(OsEvidenceEqual(pair.OsMarker, os.DeleteExpectedEvidence!));

            Assert.Equal(
                HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                store.CurrentPublication.Payload.HostToolsMarkerPairReset!.Phase);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Final_pair_proof_advances_only_to_pair_absence_verified()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-final-proof-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            RecordingOsPort os = new(
                events,
                HostToolsMarkerPairResetOsOpenResult.Opened(
                    pair.OsMarker,
                    new FakeOsCapability()));

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            int osDeletedPublished = events.IndexOf(
                "advance:OsMarkerCompareDeleted");

            int finalOsAbsence = events.IndexOf("os-absence:1");

            int pairAbsencePublished = events.IndexOf(
                "advance:PairAbsenceVerified");

            Assert.True(osDeletedPublished >= 0);

            Assert.True(finalOsAbsence > osDeletedPublished);

            Assert.True(pairAbsencePublished > finalOsAbsence);

            Assert.Equal(1, os.AbsenceCalls);

            Assert.Equal(
                HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                store.LastNext!.HostToolsMarkerPairReset!.Phase);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(2, false, "database-effect")]
    [InlineData(3, true, "os-effect")]
    [InlineData(4, false, "advance:OsMarkerCompareDeleted")]
    [InlineData(5, true, "os-absence:1")]
    [InlineData(6, false, "advance:PairAbsenceVerified")]
    public async Task Every_effect_rereads_and_authenticates_the_current_envelope_and_anchor(
        int tamperedRecoveryCall,
        bool tamperAnchor,
        string forbiddenEvent)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-reauth-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
                RecoveryProjection = (call, publication) =>
                    call != tamperedRecoveryCall
                        ? publication
                        : tamperAnchor
                            ? new InstallationResetActivePublication(
                                publication.Location,
                                publication.Envelope,
                                publication.EnvelopeDigest,
                                publication.Payload,
                                publication.Anchor with
                                {
                                    Revision = publication.Anchor.Revision + 1,
                                })
                            : new InstallationResetActivePublication(
                                publication.Location,
                                publication.Envelope with
                                {
                                    Revision = publication.Envelope.Revision + 1,
                                },
                                publication.EnvelopeDigest,
                                publication.Payload,
                                publication.Anchor),
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.DoesNotContain(forbiddenEvent, events);

            Assert.Equal(tamperedRecoveryCall, store.RecoverCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_uncertainty_leaves_the_last_proven_phase_active_and_recovery_required(
        bool failPairJournalPublication)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-failure-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = !failPairJournalPublication,
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                failPairJournalPublication
                    ? new RecordingFullResetLifecycle(
                        events,
                        Inventory(claim.OperationId))
                    : new RecordingFullResetLifecycle(events),
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

            Assert.DoesNotContain(
                "test",
                result.Error.Message,
                StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

            Assert.Null(store.CurrentPublication.Payload.HostToolsMarkerPairReset);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Begin_maps_every_prejournal_result_failure_to_content_free_recovery_required(
        int sentinel)
    {

        await using CovenantSchemaScratchDatabase database = sentinel == 2
            ? await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None)
            : await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-prejournal-result-{sentinel}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            IHostToolsMarkerPairResetDatabase databasePort = sentinel == 0
                ? new FailingOpenDatabase()
                : new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events));

            IFullInstallationResetCampaignSchemaReadiness readiness = sentinel == 1
                ? new RecordingReadiness(events, succeeds: false)
                : new SuccessfulReadiness();

            IFullInstallationResetRemediationAttestationVerifier verifier = sentinel == 3
                ? new RejectingVerifier()
                : new AuthorizingVerifier(Authorization(claim));

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId),
                failRevalidateOnCall: sentinel == 4 ? 1 : null);

            FakeOsCapability capability = new();

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                databasePort,
                readiness,
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                verifier,
                lifecycle,
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        capability)));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

            Assert.Equal(
                "The full-installation reset marker-pair operation requires recovery.",
                result.Error.Message);

            Assert.Equal(1, capability.DisposeCalls);

            Assert.Null(store.LastNext);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Begin_maps_non_cancellation_prejournal_collaborator_exceptions_to_content_free_recovery_required(
        int sentinel)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-prejournal-throw-{sentinel}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
                ThrowOnRecover = sentinel == 0,
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            FakeOsCapability capability = new();

            IHostToolsMarkerPairResetOsPort os = sentinel == 1
                ? new ThrowingOsPort()
                : new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        capability));

            IHostToolsMarkerPairResetDatabase databasePort = sentinel == 2
                ? new ThrowingDatabase()
                : new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events));

            IFullInstallationResetCampaignSchemaReadiness readiness = sentinel == 3
                ? new ThrowingReadiness()
                : new SuccessfulReadiness();

            IHostProcessToolsMarkerPairJoiner joiner = sentinel == 4
                ? new ThrowingJoiner()
                : new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair));

            IFullInstallationResetRemediationAttestationVerifier verifier = sentinel == 5
                ? new ThrowingVerifier()
                : new AuthorizingVerifier(Authorization(claim));

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId),
                throwOnInventory: sentinel == 6,
                throwOnRevalidate: sentinel == 7);

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                databasePort,
                readiness,
                joiner,
                verifier,
                lifecycle,
                os);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

            Assert.Equal(
                "The full-installation reset marker-pair operation requires recovery.",
                result.Error.Message);

            Assert.Equal(sentinel >= 2 ? 1 : 0, capability.DisposeCalls);

            Assert.Null(store.LastNext);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Caller_cancellation_before_pair_journaled_performs_no_marker_effect()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-pre-journal-cancel-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            using CancellationTokenSource callerCancellation = new();

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
                HonorCancellation = true,
            };

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId),
                    callerCancellation.Cancel),
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                subject.BeginAsync(
                    heldLock,
                    current,
                    Attestation(current.Payload.OperationId),
                    callerCancellation.Token));

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

            Assert.Null(store.CurrentPublication.Payload.HostToolsMarkerPairReset);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Prejournal_failure_after_caller_cancellation_propagates_the_exact_caller_cancellation()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-pre-journal-failure-cancel-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            using CancellationTokenSource callerCancellation = new();

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId),
                afterRevalidate: callerCancellation.Cancel,
                failRevalidateOnCall: 1);

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events),
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                lifecycle,
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            OperationCanceledException canceled =
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    subject.BeginAsync(
                        heldLock,
                        current,
                        Attestation(current.Payload.OperationId),
                        callerCancellation.Token));

            Assert.Equal(callerCancellation.Token, canceled.CancellationToken);

            Assert.Equal(1, lifecycle.ReleaseCalls);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task Prejournal_release_failure_never_masks_the_primary_result_or_caller_cancellation(
        bool callerCanceled,
        int releaseFailure)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-release-mask-{callerCanceled}-{releaseFailure}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            using CancellationTokenSource callerCancellation = new();

            using CancellationTokenSource unrelatedCancellation = new();

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            OperationCanceledException exactCallerCancellation = new(
                "The exact caller cancellation must survive cleanup.",
                callerCancellation.Token);

            Exception releaseException = releaseFailure == 0
                ? new InvalidOperationException(
                    "The release sentinel diagnostic must not escape.")
                : new OperationCanceledException(
                    "The unrelated release cancellation must not escape.",
                    unrelatedCancellation.Token);

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId),
                afterRevalidate: callerCanceled
                    ? callerCancellation.Cancel
                    : null,
                failRevalidateOnCall: callerCanceled ? null : 1,
                revalidateException: callerCanceled
                    ? exactCallerCancellation
                    : null,
                releaseException: releaseException);

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events),
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                lifecycle,
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            if (callerCanceled)
            {

                OperationCanceledException canceled =
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        subject.BeginAsync(
                            heldLock,
                            current,
                            Attestation(current.Payload.OperationId),
                            callerCancellation.Token));

                Assert.Same(exactCallerCancellation, canceled);

                Assert.Equal(callerCancellation.Token, canceled.CancellationToken);

            }
            else
            {

                Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                    heldLock,
                    current,
                    Attestation(current.Payload.OperationId),
                    CancellationToken.None);

                Assert.True(result.IsFailure);

                Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

                Assert.Equal(
                    "The full-installation reset marker-pair operation requires recovery.",
                    result.Error.Message);

            }

            Assert.Equal(1, lifecycle.ReleaseCalls);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Caller_cancellation_after_pair_journaled_uses_a_bounded_recovery_owned_checkpoint_token()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-post-journal-cancel-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            using CancellationTokenSource callerCancellation = new();

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
                CancelAfterAdvancePhase = HostToolsMarkerPairResetPhase.PairJournaled,
                CancellationToSignal = callerCancellation,
            };

            RecordingDatabaseSeam databaseSeam = new(events);

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            RecordingOsPort os = new(
                events,
                HostToolsMarkerPairResetOsOpenResult.Opened(
                    pair.OsMarker,
                    new FakeOsCapability()));

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    databaseSeam),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                callerCancellation.Token);

            Assert.True(result.IsFailure);

            Assert.True(callerCancellation.IsCancellationRequested);

            Assert.True(databaseSeam.MarkerClearToken.CanBeCanceled);

            Assert.NotEqual(callerCancellation.Token, databaseSeam.MarkerClearToken);

            Assert.True(os.DeleteToken.CanBeCanceled);

            Assert.Equal(databaseSeam.MarkerClearToken, os.DeleteToken);

            Assert.Equal(
                HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                store.CurrentPublication.Payload.HostToolsMarkerPairReset!.Phase);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Resume_from_pair_journaled_reverifies_projection_signature_pair_and_database_cas()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-journaled-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            RecordingJoiner joiner = new(new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched,
                pair));

            AuthorizingVerifier verifier = new(Authorization(claim));

            FakeOsCapability capability = new();

            RecordingOsPort os = new(
                events,
                HostToolsMarkerPairResetOsOpenResult.Unavailable(),
                HostToolsMarkerPairResetOsOpenResult.Opened(
                    pair.OsMarker,
                    capability));

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                joiner,
                verifier,
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, os.OpenCalls);

            Assert.Equal(1, os.ReopenCalls);

            Assert.Equal(1, joiner.Calls);

            Assert.NotNull(verifier.Attestation);

            Assert.True(DatabaseEvidenceEqual(pair.Database, verifier.Pair!.Database));

            Assert.Contains("database-effect", events);

            Assert.Contains("advance:DatabaseMarkerCompareDeleted", events);

            Assert.Equal(1, capability.DisposeCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Resume_from_database_deleted_reopens_only_the_exact_fixed_os_slot()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-database-deleted-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            FakeOsCapability capability = new();

            RecordingOsPort os = new(
                events,
                HostToolsMarkerPairResetOsOpenResult.Unavailable(),
                HostToolsMarkerPairResetOsOpenResult.Opened(
                    pair.OsMarker,
                    capability));

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, os.OpenCalls);

            Assert.Equal(1, os.ReopenCalls);

            Assert.Contains("os-effect", events);

            int osEffect = events.IndexOf("os-effect");

            int osPublished = events.IndexOf("advance:OsMarkerCompareDeleted");

            int firstAbsence = events.IndexOf("os-absence:1");

            Assert.True(osPublished > osEffect);

            Assert.True(firstAbsence < 0 || firstAbsence > osPublished);

            Assert.Contains("advance:OsMarkerCompareDeleted", events);

            Assert.Equal(1, capability.DisposeCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Resume_disposes_reopened_capability_when_valid_evidence_changed_before_database_access()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-reopen-changed-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsOsMarkerEvidence expected =
                checkpoint.RestartProof.OsMarkerEvidence;

            HostProcessToolsOsMarkerEvidence changed = new(
                expected.InstallationIdentity,
                expected.TransitionId,
                expected.TaintMasterKeyVersion,
                expected.TaintFingerprint,
                expected.MarkerBytesDigest,
                Digest(0xD1));

            FakeOsCapability capability = new();

            List<string> events = [];

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    new RecordingMaintenanceConnections(
                        database.MaintenanceConnections(),
                        events),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new HostProcessToolsMarkerPairJoiner(),
                new RejectingVerifier(),
                new RecordingFullResetLifecycle(),
                new RecordingOsPort(
                    events,
                    reopenResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        changed,
                        capability)));

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, capability.DisposeCalls);

            Assert.DoesNotContain("database", events);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain(
                events,
                value => value.StartsWith("advance:", StringComparison.Ordinal));

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData((byte)HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted, true)]
    [InlineData((byte)HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted, false)]
    [InlineData((byte)HostToolsMarkerPairResetPhase.PairAbsenceVerified, true)]
    [InlineData((byte)HostToolsMarkerPairResetPhase.PairAbsenceVerified, false)]
    public async Task Terminal_resume_rejects_persisted_join_or_fixed_time_authorization_before_os_absence(
        byte phaseCode,
        bool rejectJoin)
    {

        HostToolsMarkerPairResetPhase phase =
            (HostToolsMarkerPairResetPhase)phaseCode;

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-terminal-auth-first-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(phase);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            RecordingJoiner joiner = new(
                rejectJoin
                    ? new HostProcessToolsMarkerPairJoinResult(
                        HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                        null)
                    : new HostProcessToolsMarkerPairJoinResult(
                        HostProcessToolsMarkerPairDisposition.TaintedMatched,
                        pair));

            IFullInstallationResetRemediationAttestationVerifier verifier = rejectJoin
                ? new AuthorizingVerifier(Authorization(claim))
                : new RejectingVerifier();

            List<string> events = [];

            RecordingOsPort os = new(events);

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events),
                new HostToolsMarkerPairResetDatabase(
                    new RecordingMaintenanceConnections(
                        database.MaintenanceConnections(),
                        events),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                joiner,
                verifier,
                new RecordingFullResetLifecycle(),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, joiner.Calls);

            Assert.Equal(0, os.AbsenceCalls);

            Assert.DoesNotContain("database", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(
        true,
        (byte)HostToolsMarkerPairResetOsAbsenceStatus.Absent,
        true,
        true)]
    [InlineData(
        false,
        (byte)HostToolsMarkerPairResetOsAbsenceStatus.Absent,
        true,
        false)]
    [InlineData(
        true,
        (byte)HostToolsMarkerPairResetOsAbsenceStatus.Mismatch,
        false,
        false)]
    [InlineData(
        true,
        (byte)HostToolsMarkerPairResetOsAbsenceStatus.Unavailable,
        false,
        false)]
    public async Task Resume_from_os_deleted_requires_exact_database_and_os_absence(
        bool databaseIsClean,
        byte osAbsenceCode,
        bool expectedDatabaseOpen,
        bool expectedPairAbsencePublication)
    {

        HostToolsMarkerPairResetOsAbsenceStatus osAbsence =
            (HostToolsMarkerPairResetOsAbsenceStatus)osAbsenceCode;

        await using CovenantSchemaScratchDatabase database = databaseIsClean
            ? await CreateCleanMarkerDatabaseAsync()
            : await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-os-deleted-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            RecordingOsPort os = new(
                events,
                absenceStatus: osAbsence);

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    new RecordingMaintenanceConnections(
                        database.MaintenanceConnections(),
                        events),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, os.OpenCalls);

            Assert.Equal(0, os.ReopenCalls);

            Assert.Equal(1, os.AbsenceCalls);

            Assert.Equal(
                expectedDatabaseOpen,
                events.Contains("database"));

            Assert.Equal(
                expectedPairAbsencePublication,
                events.Contains("advance:PairAbsenceVerified"));

            if (expectedDatabaseOpen)
            {

                Assert.True(
                    events.IndexOf("os-absence") < events.IndexOf("database"));

            }

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Resume_from_pair_absence_verified_replays_no_pair_mutation()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-absent-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairAbsenceVerified);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            RecordingOsPort os = new(events);

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    new RecordingMaintenanceConnections(
                        database.MaintenanceConnections(),
                        events),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    events,
                    Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, os.OpenCalls);

            Assert.Equal(0, os.ReopenCalls);

            Assert.Equal(1, os.AbsenceCalls);

            Assert.Contains("database", events);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

            // No pair phase is republished. What this resume does publish is the Campaign cleanup
            // vector the checkpoint had not yet journaled, under the phase already proven — so every
            // advance it makes names the phase it started from.
            Assert.All(
                events.Where(entry => entry.StartsWith("advance:", StringComparison.Ordinal)),
                static entry => Assert.Equal("advance:PairAbsenceVerified", entry));

            Assert.Equal(
                HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                store.LastNext!.HostToolsMarkerPairReset!.Phase);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(HostProcessToolsMarkerPairDisposition.Clean, false, 0)]
    [InlineData(HostProcessToolsMarkerPairDisposition.PendingBlocked, false, 0)]
    [InlineData(HostProcessToolsMarkerPairDisposition.MismatchBlocked, false, 0)]
    [InlineData(HostProcessToolsMarkerPairDisposition.TaintedMatched, false, 0)]
    [InlineData(HostProcessToolsMarkerPairDisposition.TaintedMatched, true, 1)]
    public async Task Resume_reconstructs_persisted_evidence_and_requires_shared_joiner_tainted_matched_nonnull(
        HostProcessToolsMarkerPairDisposition disposition,
        bool carriesPair,
        int expectedVerifierCalls)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-join-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairAbsenceVerified);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            RecordingJoiner joiner = new(new HostProcessToolsMarkerPairJoinResult(
                disposition,
                carriesPair ? pair : null));

            AuthorizingVerifier verifier = new(Authorization(claim));

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current),
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                joiner,
                verifier,
                new RecordingFullResetLifecycle(
                    inventory: Inventory(claim.OperationId)),
                new RecordingOsPort());

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, joiner.Calls);

            Assert.True(DatabaseEvidenceEqual(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                joiner.Database!));

            Assert.True(OsEvidenceEqual(
                checkpoint.RestartProof.OsMarkerEvidence,
                joiner.OsMarker!));

            Assert.Equal(expectedVerifierCalls, verifier.Attestation is null ? 0 : 1);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Resume_never_falls_back_to_a_fresh_live_pair_admission_read_or_second_classifier()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-no-fresh-admission-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairAbsenceVerified);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            RecordingJoiner joiner = new(new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched,
                pair));

            RecordingFullResetLifecycle lifecycle = new(
                inventory: Inventory(claim.OperationId));

            RecordingOsPort os = new();

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current),
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                joiner,
                new AuthorizingVerifier(Authorization(claim)),
                lifecycle,
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, os.OpenCalls);

            Assert.Equal(0, os.ReopenCalls);

            Assert.Equal(1, os.AbsenceCalls);

            Assert.Equal(1, joiner.Calls);

            Assert.Equal(0, lifecycle.InventoryCalls);

            Assert.Equal(0, lifecycle.RevalidateCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Resume_rejects_zero_unknown_skipped_regressed_or_tampered_checkpoint_state(
        int mutation)
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-invalid-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = mutation switch
            {
                0 => CheckpointPublication((HostToolsMarkerPairResetPhase)0),
                1 => CheckpointPublication((HostToolsMarkerPairResetPhase)byte.MaxValue),
                3 => CheckpointPublication(
                    HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted),
                _ => CheckpointPublication(HostToolsMarkerPairResetPhase.PairJournaled),
            };

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            InstallationResetActivePublication supplied = mutation switch
            {
                2 => WithCheckpoint(
                    current,
                    checkpoint with
                    {
                        Phase = HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted,
                    }),
                3 => WithCheckpoint(
                    current,
                    checkpoint with
                    {
                        Phase = HostToolsMarkerPairResetPhase.PairJournaled,
                    }),
                4 => WithCheckpoint(
                    current,
                    checkpoint with
                    {
                        OwnerEffectDigest = Digest(0xF4),
                    }),
                _ => current,
            };

            List<string> events = [];

            RecordingOsPort os = new(events);

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events),
                new HostToolsMarkerPairResetDatabase(
                    new RecordingMaintenanceConnections(
                        database.MaintenanceConnections(),
                        events),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new HostProcessToolsMarkerPairJoiner(),
                new RejectingVerifier(),
                new RecordingFullResetLifecycle(),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                supplied,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, os.OpenCalls);

            Assert.Equal(0, os.ReopenCalls);

            Assert.Equal(0, os.AbsenceCalls);

            Assert.DoesNotContain("database", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Resume_rejects_offset_shifted_claim_restart_or_signed_projection_before_os_access(
        int mutation)
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-structural-offset-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            FullInstallationResetRestartProofV1 restart = checkpoint.RestartProof;

            FullInstallationResetSignedAttestationProjectionV1 signed =
                restart.SignedAttestation;

            InstallationResetActivePublication supplied = mutation switch
            {
                0 => WithClaim(
                    current,
                    claim with
                    {
                        AcceptedAtUtc = claim.AcceptedAtUtc.ToOffset(
                            TimeSpan.FromHours(1)),
                    }),
                1 => WithCheckpoint(
                    current,
                    checkpoint with
                    {
                        RestartProof = restart with
                        {
                            AcceptedAtUtc = restart.AcceptedAtUtc.ToOffset(
                                TimeSpan.FromHours(1)),
                        },
                    }),
                2 => WithCheckpoint(
                    current,
                    checkpoint with
                    {
                        RestartProof = restart with
                        {
                            SignedAttestation = signed with
                            {
                                IssuedAtUtc = signed.IssuedAtUtc.ToOffset(
                                    TimeSpan.FromHours(1)),
                            },
                        },
                    }),
                _ => WithCheckpoint(
                    current,
                    checkpoint with
                    {
                        RestartProof = restart with
                        {
                            SignedAttestation = signed with
                            {
                                ExpiresAtUtc = signed.ExpiresAtUtc.ToOffset(
                                    TimeSpan.FromHours(1)),
                            },
                        },
                    }),
            };

            List<string> events = [];

            RecordingOsPort os = new(events);

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events),
                new HostToolsMarkerPairResetDatabase(
                    new RecordingMaintenanceConnections(
                        database.MaintenanceConnections(),
                        events),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new HostProcessToolsMarkerPairJoiner(),
                new RejectingVerifier(),
                new RecordingFullResetLifecycle(),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                supplied,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, os.OpenCalls);

            Assert.Equal(0, os.ReopenCalls);

            Assert.Equal(0, os.AbsenceCalls);

            Assert.DoesNotContain("database", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Matching_recovered_recomputed_checkpoint_still_requires_fixed_time_verifier()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-recomputed-proof-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = WithRecomputedInventory(
                CheckpointPublication(
                    HostToolsMarkerPairResetPhase.PairAbsenceVerified));

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            RejectingVerifier verifier = new();

            RecordingOsPort os = new();

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current),
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                verifier,
                new RecordingFullResetLifecycle(),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, verifier.RecoveryCalls);

            Assert.Equal(0, os.AbsenceCalls);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Begin_refuses_an_exact_issue_121_predecessor_schema_before_pair_journal_or_marker_effect()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-begin-schema-121-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            List<string> events = [];

            RecordingJoiner joiner = new(new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched,
                new HostProcessToolsMatchedPair(
                    TaintedDatabaseEvidence(),
                    MatchedOsEvidence())));

            RecordingFullResetLifecycle lifecycle = new();

            FakeOsCapability capability = new();

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new RecordingReadiness(events, succeeds: false),
                joiner,
                new RejectingVerifier(),
                lifecycle,
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        MatchedOsEvidence(),
                        capability)));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(current.Payload.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, joiner.Calls);

            Assert.Equal(0, lifecycle.InventoryCalls);

            Assert.Equal(1, capability.DisposeCalls);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

            Assert.DoesNotContain(events, entry => entry.StartsWith(
                "advance:",
                StringComparison.Ordinal));

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Resume_refuses_missing_or_drifted_issue_122_cleanup_schema_before_pair_effect()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-schema-drift-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            List<string> events = [];

            RecordingJoiner joiner = new(new HostProcessToolsMarkerPairJoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched,
                new HostProcessToolsMatchedPair(
                    checkpoint.RestartProof.DatabaseMarkerEvidence,
                    checkpoint.RestartProof.OsMarkerEvidence)));

            FakeOsCapability capability = new();

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new RecordingReadiness(events, succeeds: false),
                joiner,
                new RejectingVerifier(),
                new RecordingFullResetLifecycle(),
                new RecordingOsPort(
                    events,
                    reopenResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        checkpoint.RestartProof.OsMarkerEvidence,
                        capability)));

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, joiner.Calls);

            Assert.Equal(1, capability.DisposeCalls);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

            Assert.DoesNotContain(events, entry => entry.StartsWith(
                "advance:",
                StringComparison.Ordinal));

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Exact_issue_122_core_schema_is_proven_on_the_same_connection_before_inventory_or_effect()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-schema-connection-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingReadiness readiness = new(events, succeeds: true);

            RecordingFullResetLifecycle lifecycle = new(
                events,
                Inventory(claim.OperationId));

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events),
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                readiness,
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                lifecycle,
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                heldLock,
                current,
                Attestation(claim.OperationId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Same(readiness.Connection, lifecycle.InventoryConnection);

            Assert.Same(readiness.Connection, lifecycle.RevalidateConnection);

            Assert.True(events.IndexOf("readiness") < events.IndexOf("inventory"));

            Assert.True(events.IndexOf("readiness") < events.IndexOf("revalidate"));

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Pair_journaled_clean_database_with_unchanged_os_recovers_the_database_effect_publication_gap()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-database-gap-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = true,
            };

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    inventory: Inventory(claim.OperationId)),
                new RecordingOsPort(
                    events,
                    reopenResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.DoesNotContain("database-effect", events);

            Assert.Contains("advance:DatabaseMarkerCompareDeleted", events);

            Assert.DoesNotContain("os-effect", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Pair_journaled_offset_shifted_fixed_time_authorization_refuses_before_database_effect_or_advance()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-offset-shift-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            List<string> events = [];

            FullInstallationResetRemediationAuthorization shifted = new(
                claim.OperationId,
                claim.InstallationId,
                claim.AttestationDigest,
                claim.NonceDigest,
                claim.IssuerDigest,
                claim.AcceptedAtUtc.ToOffset(TimeSpan.FromHours(1)));

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(shifted),
                new RecordingFullResetLifecycle(
                    inventory: Inventory(claim.OperationId)),
                new RecordingOsPort(
                    events,
                    reopenResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain(
                events,
                value => value.StartsWith("advance:", StringComparison.Ordinal));

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Database_deleted_clean_database_with_exact_os_absence_recovers_the_os_effect_publication_gap()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-os-gap-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            List<string> events = [];

            RecordingOsPort os = new(
                events,
                reopenResult: HostToolsMarkerPairResetOsOpenResult.Absent());

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    inventory: Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, os.ReopenCalls);

            Assert.Equal(1, os.AbsenceCalls);

            Assert.Null(os.DeleteCapability);

            Assert.Contains("advance:OsMarkerCompareDeleted", events);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Recovered_database_effect_reruns_wal_durability_before_advancing()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        await database.ExecuteAsync(
            "PRAGMA wal_autocheckpoint=0; UPDATE covenant_authority_state SET AuthorityEpoch = AuthorityEpoch + 1;",
            CancellationToken.None);

        string walPath = database.DatabasePath + "-wal";

        Assert.True(File.Exists(walPath));

        Assert.True(new FileInfo(walPath).Length > 0);

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-database-durability-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            bool durableBeforeAdvance = false;

            RecordingActiveStore store = new(guardedRoot, current)
            {
                AdvanceSucceeds = true,
                BeforeAdvance = phase =>
                {
                    if (phase
                        is HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted)
                    {

                        durableBeforeAdvance = !File.Exists(walPath)
                            || new FileInfo(walPath).Length == 0;

                    }
                },
            };

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    inventory: Inventory(claim.OperationId)),
                new RecordingOsPort(
                    reopenResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.True(durableBeforeAdvance);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Recovered_os_effect_reruns_platform_durability_and_second_absence_readback_before_advancing()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-os-durability-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            List<string> events = [];

            RecordingOsPort os = new(
                events,
                reopenResult: HostToolsMarkerPairResetOsOpenResult.Absent());

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    inventory: Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, os.AbsenceCalls);

            Assert.True(
                events.IndexOf("os-absence:1")
                    < events.IndexOf("advance:OsMarkerCompareDeleted"));

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovered_effect_barrier_failure_preserves_the_prior_authenticated_phase(
        bool databaseBarrierFailure)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        SqliteConnection? blockingReader = null;

        if (databaseBarrierFailure)
        {

            await database.ExecuteAsync(
                "PRAGMA wal_autocheckpoint=0;",
                CancellationToken.None);

            blockingReader = await database.OpenAdditionalConnectionAsync(
                CancellationToken.None);

            await ExecuteSqlAsync(blockingReader, "BEGIN;");

            await using (SqliteCommand read = blockingReader.CreateCommand())
            {

                read.CommandText = "SELECT COUNT(*) FROM covenant_authority_state;";

                _ = await read.ExecuteScalarAsync(CancellationToken.None);

            }

            await database.ExecuteAsync(
                "UPDATE covenant_authority_state SET AuthorityEpoch = AuthorityEpoch + 1;",
                CancellationToken.None);

        }

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-barrier-failure-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            HostToolsMarkerPairResetPhase phase = databaseBarrierFailure
                ? HostToolsMarkerPairResetPhase.PairJournaled
                : HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted;

            InstallationResetActivePublication current = CheckpointPublication(phase);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            List<string> events = [];

            RecordingOsPort os = databaseBarrierFailure
                ? new RecordingOsPort(
                    events,
                    reopenResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability()))
                : new RecordingOsPort(
                    events,
                    reopenResult: HostToolsMarkerPairResetOsOpenResult.Absent(),
                    absenceStatus: HostToolsMarkerPairResetOsAbsenceStatus.Unavailable);

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                new AuthorizingVerifier(Authorization(claim)),
                new RecordingFullResetLifecycle(
                    inventory: Inventory(claim.OperationId)),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.DoesNotContain(events, entry => entry.StartsWith(
                "advance:",
                StringComparison.Ordinal));

        }
        finally
        {

            if (blockingReader is not null)
            {

                await ExecuteSqlAsync(blockingReader, "ROLLBACK;");

                await blockingReader.DisposeAsync();

            }

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pair_journaled_os_absence_or_both_absent_is_out_of_order_and_blocks(
        bool databaseIsAlreadyClean)
    {

        await using CovenantSchemaScratchDatabase database = databaseIsAlreadyClean
            ? await CreateCleanMarkerDatabaseAsync()
            : await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-out-of-order-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            List<string> events = [];

            RecordingOsPort os = new(
                events,
                reopenResult: HostToolsMarkerPairResetOsOpenResult.Absent());

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    new RecordingMaintenanceConnections(
                        database.MaintenanceConnections(),
                        events),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new HostProcessToolsMarkerPairJoiner(),
                new RejectingVerifier(),
                new RecordingFullResetLifecycle(),
                os);

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, os.ReopenCalls);

            Assert.Equal(0, os.AbsenceCalls);

            Assert.DoesNotContain("database", events);

            Assert.DoesNotContain(events, entry => entry.StartsWith(
                "advance:",
                StringComparison.Ordinal));

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Any_missing_singleton_generic_read_failure_or_nonadjacent_phase_state_blocks(
        int failureKind)
    {

        await using CovenantSchemaScratchDatabase database = failureKind == 1
            ? await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None)
            : await CreateMarkerDatabaseAsync();

        if (failureKind == 0)
        {

            await database.ExecuteAsync(
                "DELETE FROM covenant_authority_state;",
                CancellationToken.None);

        }

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-read-failure-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            InstallationResetActivePublication supplied = failureKind == 2
                ? WithCheckpoint(
                    current,
                    checkpoint with
                    {
                        Phase = HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted,
                    })
                : current;

            List<string> events = [];

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new HostProcessToolsMarkerPairJoiner(),
                new RejectingVerifier(),
                new RecordingFullResetLifecycle(),
                new RecordingOsPort(
                    events,
                    reopenResult: HostToolsMarkerPairResetOsOpenResult.Opened(
                        checkpoint.RestartProof.OsMarkerEvidence,
                        new FakeOsCapability())));

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                supplied,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

            Assert.DoesNotContain(events, entry => entry.StartsWith(
                "advance:",
                StringComparison.Ordinal));

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_surviving_database_or_os_evidence_is_preserved_and_blocks(
        bool changeDatabase)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        if (changeDatabase)
        {

            await database.ExecuteAsync(
                "UPDATE covenant_authority_state SET TransitionId = '99999999-2222-4333-8444-555555555555';",
                CancellationToken.None);

        }

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-changed-survivor-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairJournaled);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            List<string> events = [];

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current, events)
                {
                    AdvanceSucceeds = true,
                },
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new HostProcessToolsMarkerPairJoiner(),
                new RejectingVerifier(),
                new RecordingFullResetLifecycle(),
                new RecordingOsPort(
                    events,
                    reopenResult: changeDatabase
                        ? HostToolsMarkerPairResetOsOpenResult.Opened(
                            checkpoint.RestartProof.OsMarkerEvidence,
                            new FakeOsCapability())
                        : HostToolsMarkerPairResetOsOpenResult.Mismatch()));

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.DoesNotContain("database-effect", events);

            Assert.DoesNotContain("os-effect", events);

            Assert.DoesNotContain(events, entry => entry.StartsWith(
                "advance:",
                StringComparison.Ordinal));

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    [Fact]
    public async Task Restart_after_statement_expiry_uses_only_authenticated_accepted_at_utc()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateCleanMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-resume-expired-statement-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActivePublication current = CheckpointPublication(
                HostToolsMarkerPairResetPhase.PairAbsenceVerified);

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            Assert.True(
                checkpoint.RestartProof.SignedAttestation.ExpiresAtUtc
                    < DateTimeOffset.UtcNow);

            HostProcessToolsMatchedPair pair = new(
                checkpoint.RestartProof.DatabaseMarkerEvidence,
                checkpoint.RestartProof.OsMarkerEvidence);

            AuthorizingVerifier verifier = new(Authorization(claim));

            HostToolsMarkerPairResetCoordinator subject = new(
                new RecordingActiveStore(guardedRoot, current),
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                verifier,
                new RecordingFullResetLifecycle(
                    inventory: Inventory(claim.OperationId)),
                new RecordingOsPort());

            Result<InstallationResetActivePublication> result = await subject.ResumeAsync(
                heldLock,
                current,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(claim.AcceptedAtUtc, verifier.AcceptedAtUtc);

            Assert.Equal(
                checkpoint.RestartProof.AcceptedAtUtc,
                verifier.AcceptedAtUtc);

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    private static async Task AssertAttemptRootReleaseBoundaryAsync(string failure)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        string guardedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-pair-release-{failure}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(guardedRoot);

        try
        {

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            using CancellationTokenSource callerCancellation = new();

            InstallationResetActivePublication current = Publication();

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current.Payload.FullInstallationResetRemediationClaim);

            List<string> events = [];

            RecordingActiveStore store = new(guardedRoot, current, events)
            {
                AdvanceSucceeds = failure is "cancellation" or "post-journal",
                HonorCancellation = failure == "cancellation",
            };

            CampaignPathFullInstallationResetInventory? inventory = failure switch
            {
                "owner" => Inventory(Guid.NewGuid()),
                "inventory-failure" => null,
                _ => Inventory(claim.OperationId),
            };

            RecordingFullResetLifecycle lifecycle = new(
                events,
                inventory,
                afterRevalidate: failure == "cancellation"
                    ? callerCancellation.Cancel
                    : null,
                failRevalidateOnCall: failure == "revalidation" ? 1 : null,
                throwOnInventory: failure == "inventory-exception");

            HostProcessToolsMatchedPair pair = new(
                TaintedDatabaseEvidence(),
                MatchedOsEvidence());

            IFullInstallationResetRemediationAttestationVerifier verifier =
                failure == "post-journal"
                    ? new FirstThenRejectingVerifier(Authorization(claim))
                    : new AuthorizingVerifier(Authorization(claim));

            HostToolsMarkerPairResetCoordinator subject = new(
                store,
                new HostToolsMarkerPairResetDatabase(
                    database.MaintenanceConnections(),
                    CovenantSqliteConnectionInitializer.Instance,
                    new RecordingDatabaseSeam(events)),
                new SuccessfulReadiness(),
                new RecordingJoiner(new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.TaintedMatched,
                    pair)),
                verifier,
                lifecycle,
                new RecordingOsPort(
                    events,
                    HostToolsMarkerPairResetOsOpenResult.Opened(
                        pair.OsMarker,
                        new FakeOsCapability())));

            FullInstallationResetExternalRemediationAttestation attestation =
                Attestation(current.Payload.OperationId);

            if (failure == "digest")
            {

                attestation = attestation with
                {
                    RemediationActionDigest = Digest(0xE7),
                };

            }

            if (failure == "cancellation")
            {

                OperationCanceledException canceled =
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        subject.BeginAsync(
                            heldLock,
                            current,
                            attestation,
                            callerCancellation.Token));

                Assert.Equal(callerCancellation.Token, canceled.CancellationToken);

            }
            else
            {

                Result<InstallationResetActivePublication> result = await subject.BeginAsync(
                    heldLock,
                    current,
                    attestation,
                    CancellationToken.None);

                Assert.True(result.IsFailure);

            }

            int expectedReleaseCalls = failure == "post-journal" ? 0 : 1;

            Assert.Equal(expectedReleaseCalls, lifecycle.ReleaseCalls);

            if (expectedReleaseCalls == 1)
            {

                Assert.Equal(claim.OperationId, lifecycle.ReleasedOwnerOperationId);

                Assert.Equal("release", events[^1]);

            }
            else
            {

                Assert.Equal(
                    HostToolsMarkerPairResetPhase.PairJournaled,
                    store.CurrentPublication.Payload.HostToolsMarkerPairReset!.Phase);

            }

        }
        finally
        {

            Directory.Delete(guardedRoot, recursive: true);

        }

    }

    private static InstallationResetActivePublication Publication(Guid? operationId = null)
    {

        Guid operation = operationId ?? Guid.NewGuid();

        Guid installation = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

        DateTimeOffset acceptedAtUtc = new(
            2026,
            8,
            22,
            12,
            0,
            0,
            TimeSpan.Zero);

        InstallationResetActiveRecord record = new(
            InstallationResetActiveStore.CurrentVersion,
            operation,
            "full-reset-plan",
            InstallationResetScope.All,
            new DataRetentionWorkspaceBinding(Guid.NewGuid(), "/workspace"),
            new InstallationResetAcceptedBinding("binding", [], [], [], [], []),
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: ErrorCodes.Data.RecoveryRequired,
            FullInstallationResetRemediationClaim: new FullInstallationResetRemediationClaimV1(
                1,
                operation,
                installation,
                Value(FullInstallationResetRemediationAttestationDigest.Calculate(
                    Attestation(operation))),
                Digest(0x45),
                Digest(0x46),
                acceptedAtUtc));

        InstallationResetActivePayloadV2 payload =
            InstallationResetActivePayloadV2.FromRecord(record);

        InstallationResetActiveLocation location = new(
            "/active",
            Digest(0x10),
            Digest(0x11),
            "reset.active",
            Digest(0x12));

        InstallationResetActiveEnvelopeV2 envelope = new(
            2,
            location.ProfileNamespaceDigest,
            installation,
            operation,
            1,
            Digest(0x13),
            location.Digest,
            InstallationResetScope.All,
            record.PlanId,
            "nonce",
            "ciphertext",
            "tag");

        CovenantDigest envelopeDigest = Digest(0x14);

        InstallationResetActiveAnchorV1 anchor = new(
            1,
            InstallationResetActiveAnchorState.Active,
            location.ProfileNamespaceDigest,
            installation,
            operation,
            1,
            envelopeDigest,
            location.Digest);

        return new InstallationResetActivePublication(
            location,
            envelope,
            envelopeDigest,
            payload,
            anchor);

    }

    private static InstallationResetActivePublication CheckpointPublication(
        HostToolsMarkerPairResetPhase phase)
    {

        InstallationResetActivePublication claimPublication = Publication();

        FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
            FullInstallationResetRemediationClaimV1>(
                claimPublication.Payload.FullInstallationResetRemediationClaim);

        FullInstallationResetExternalRemediationAttestation attestation =
            Attestation(claim.OperationId);

        HostProcessToolsMatchedPair pair = new(
            TaintedDatabaseEvidence(),
            MatchedOsEvidence());

        CampaignPathFullInstallationResetInventory inventory =
            Inventory(claim.OperationId);

        CovenantDigest signedDigest = Value(
            FullInstallationResetRemediationAttestationDigest.Calculate(attestation));

        CovenantDigest pairDigest = Value(
            FullInstallationResetMarkerPairResetDigests.PairEvidence(pair));

        CovenantDigest ownerEffect = Value(
            FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                claim.OperationId,
                claim.InstallationId,
                pair.Database.TransitionId!.Value,
                pair.Database.TaintMasterKeyVersion!.Value,
                pair.Database.TaintFingerprint!.Value,
                pair.Database.DatabaseMarkerDigest,
                pair.OsMarker.MarkerBytesDigest,
                attestation.RemediationActionDigest,
                inventory.InventoryDigest));

        HostToolsMarkerPairResetCheckpointV1 checkpoint = new(
            1,
            phase,
            new FullInstallationResetRestartProofV1(
                1,
                FullInstallationResetSignedAttestationProjectionV1.FromAttestation(
                    attestation),
                claim.AcceptedAtUtc,
                signedDigest,
                pair.Database,
                pair.OsMarker,
                pairDigest),
            inventory.Entries,
            inventory.InventoryDigest,
            ownerEffect,
            MarkerIntentCount: null,
            OrderedMarkerIntentIds: null,
            MarkerIntentVectorDigest: null,
            DeletedCount: null,
            OrphanCount: null);

        InstallationResetActiveRecord record =
            claimPublication.Payload.ToRecord() with
            {
                HostToolsMarkerPairReset = checkpoint,
            };

        CovenantDigest envelopeDigest = Digest(0x61);

        return new InstallationResetActivePublication(
            claimPublication.Location,
            claimPublication.Envelope with
            {
                Revision = claimPublication.Envelope.Revision + 1,
                PreviousEnvelopeDigest = claimPublication.EnvelopeDigest,
            },
            envelopeDigest,
            InstallationResetActivePayloadV2.FromRecord(record),
            claimPublication.Anchor with
            {
                Revision = claimPublication.Anchor.Revision + 1,
                EnvelopeDigest = envelopeDigest,
            });

    }

    private static FullInstallationResetExternalRemediationAttestation Attestation(
        Guid operationId) =>
        new FullInstallationResetExternalRemediationAttestation(
            1,
            operationId,
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            7,
            Digest(0x5A),
            TaintedDatabaseEvidence().DatabaseMarkerDigest,
            Digest(0x23),
            new CovenantDigest(Convert.FromHexString(
                "761e8536128080d5936070524da90a6558b8901ea46d93194646b413bb27a1d9")),
            Base64Url.EncodeToString(Enumerable.Repeat((byte)0x33, 16).ToArray()),
            "RetroDownfall.Remediation.v1",
            new DateTimeOffset(2026, 8, 22, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero),
            Base64Url.EncodeToString(Enumerable.Repeat((byte)0x44, 64).ToArray()));

    private static InstallationResetActivePublication WithCheckpoint(
        InstallationResetActivePublication publication,
        HostToolsMarkerPairResetCheckpointV1 checkpoint) =>
        new(
            publication.Location,
            publication.Envelope,
            publication.EnvelopeDigest,
            InstallationResetActivePayloadV2.FromRecord(
                publication.Payload.ToRecord() with
                {
                    HostToolsMarkerPairReset = checkpoint,
                }),
            publication.Anchor);

    private static InstallationResetActivePublication WithPayload(
        InstallationResetActivePublication publication,
        InstallationResetActivePayloadV2 payload) =>
        new(
            publication.Location,
            publication.Envelope,
            publication.EnvelopeDigest,
            payload,
            publication.Anchor);

    private static InstallationResetActivePublication WithClaim(
        InstallationResetActivePublication publication,
        FullInstallationResetRemediationClaimV1 claim) =>
        new(
            publication.Location,
            publication.Envelope,
            publication.EnvelopeDigest,
            InstallationResetActivePayloadV2.FromRecord(
                publication.Payload.ToRecord() with
                {
                    FullInstallationResetRemediationClaim = claim,
                }),
            publication.Anchor);

    private static InstallationResetActivePublication WithoutRemediationClaim(
        InstallationResetActivePublication publication) =>
        new(
            publication.Location,
            publication.Envelope,
            publication.EnvelopeDigest,
            InstallationResetActivePayloadV2.FromRecord(
                publication.Payload.ToRecord() with
                {
                    HostToolsMarkerPairReset = null,
                    FullInstallationResetRemediationClaim = null,
                }),
            publication.Anchor);

    private static InstallationResetActivePublication WithRecomputedInventory(
        InstallationResetActivePublication publication)
    {

        FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
            FullInstallationResetRemediationClaimV1>(
                publication.Payload.FullInstallationResetRemediationClaim);

        HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
            HostToolsMarkerPairResetCheckpointV1>(
                publication.Payload.HostToolsMarkerPairReset);

        System.Collections.Immutable.ImmutableArray<CampaignMarkerInventoryEntryV1>
            inventory =
            [
                new CampaignMarkerInventoryEntryV1(
                    Guid.Parse("91919191-1111-4111-8111-111111111111"),
                    1,
                    Digest(0x91),
                    Digest(0x92),
                    Digest(0x93),
                    Digest(0x94)),
            ];

        CovenantDigest inventoryDigest = Value(
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(inventory));

        FullInstallationResetExternalRemediationAttestation attestation =
            checkpoint.RestartProof.SignedAttestation.ToAttestation();

        HostProcessToolsDatabaseMarkerEvidence database =
            checkpoint.RestartProof.DatabaseMarkerEvidence;

        CovenantDigest ownerEffect = Value(
            FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                claim.OperationId,
                claim.InstallationId,
                database.TransitionId!.Value,
                database.TaintMasterKeyVersion!.Value,
                database.TaintFingerprint!.Value,
                database.DatabaseMarkerDigest,
                checkpoint.RestartProof.OsMarkerEvidence.MarkerBytesDigest,
                attestation.RemediationActionDigest,
                inventoryDigest));

        return WithCheckpoint(
            publication,
            checkpoint with
            {
                CampaignInventory = inventory,
                CampaignMarkerInventoryDigest = inventoryDigest,
                OwnerEffectDigest = ownerEffect,
            });

    }

    private static FullInstallationResetRemediationAuthorization Authorization(
        FullInstallationResetRemediationClaimV1 claim) =>
        new(
            claim.OperationId,
            claim.InstallationId,
            claim.AttestationDigest,
            claim.NonceDigest,
            claim.IssuerDigest,
            claim.AcceptedAtUtc);

    private static CampaignPathFullInstallationResetInventory Inventory(Guid operationId)
    {

        System.Collections.Immutable.ImmutableArray<CampaignMarkerInventoryEntryV1> entries = [];

        CovenantDigest digest = Value(
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(entries));

        return Value(CampaignPathFullInstallationResetInventory.Create(
            operationId,
            entries,
            digest));

    }

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;

    }

    private static HostProcessToolsOsMarkerEvidence OsEvidence() =>
        new(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            7,
            Digest(0x21),
            Digest(0x23),
            Digest(0x25));

    private static HostProcessToolsDatabaseMarkerEvidence TaintedDatabaseEvidence() =>
        new(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            RetroDownfall.Arcanum.Core.Security.CovenantHostToolsState.HostToolsTainted,
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            7,
            Digest(0x5A));

    private static HostProcessToolsOsMarkerEvidence MatchedOsEvidence() =>
        new(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            7,
            Digest(0x5A),
            Digest(0x23),
            Digest(0x25));

    private static async Task<CovenantSchemaScratchDatabase> CreateMarkerDatabaseAsync()
    {

        CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.ExecuteAsync(
            """
            CREATE TABLE covenant_authority_state (
                StateKey,
                InstallationIdentity,
                AuthorityEpoch,
                CurrentMasterKeyVersion,
                CurrentMasterKeyFingerprint,
                RecoveryEnvelopeEpoch,
                HostToolsStateCode,
                TransitionId,
                TaintTimeMasterVersion,
                TaintFingerprint
            );

            INSERT INTO covenant_authority_state (
                StateKey,
                InstallationIdentity,
                AuthorityEpoch,
                CurrentMasterKeyVersion,
                CurrentMasterKeyFingerprint,
                RecoveryEnvelopeEpoch,
                HostToolsStateCode,
                TransitionId,
                TaintTimeMasterVersion,
                TaintFingerprint)
            VALUES (
                1,
                'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
                1,
                1,
                zeroblob(32),
                1,
                3,
                '11111111-2222-4333-8444-555555555555',
                7,
                X'5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A');
            """,
            CancellationToken.None);

        return database;

    }

    private static async Task<CovenantSchemaScratchDatabase>
        CreateCleanMarkerDatabaseAsync()
    {

        CovenantSchemaScratchDatabase database =
            await CreateMarkerDatabaseAsync();

        await database.ExecuteAsync(
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 1,
                TransitionId = NULL,
                TaintTimeMasterVersion = NULL,
                TaintFingerprint = NULL;
            """,
            CancellationToken.None);

        return database;

    }

    private static CovenantDigest Digest(byte value) =>
        new(Enumerable.Repeat(value, 32).ToArray());

    private static async Task ExecuteSqlAsync(
        SqliteConnection connection,
        string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static bool DatabaseEvidenceEqual(
        HostProcessToolsDatabaseMarkerEvidence left,
        HostProcessToolsDatabaseMarkerEvidence right) =>
        left.InstallationIdentity == right.InstallationIdentity
        && left.State == right.State
        && left.TransitionId == right.TransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && left.TaintFingerprint == right.TaintFingerprint
        && left.DatabaseMarkerDigest == right.DatabaseMarkerDigest;

    private static bool OsEvidenceEqual(
        HostProcessToolsOsMarkerEvidence left,
        HostProcessToolsOsMarkerEvidence right) =>
        left.InstallationIdentity == right.InstallationIdentity
        && left.TransitionId == right.TransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && left.TaintFingerprint == right.TaintFingerprint
        && left.MarkerBytesDigest == right.MarkerBytesDigest
        && left.DurableIdentityDigest == right.DurableIdentityDigest;

    private sealed class RecordingActiveStore(
        string guardedRoot,
        InstallationResetActivePublication current,
        List<string>? events = null)
        : IInstallationResetActiveStore
    {

        public string GuardedRoot { get; } = guardedRoot;

        internal InstallationResetActiveRecord? LastNext { get; private set; }

        internal InstallationResetActivePublication CurrentPublication => current;

        internal bool AdvanceSucceeds { get; init; }

        internal bool HonorCancellation { get; init; }

        internal HostToolsMarkerPairResetPhase? CancelAfterAdvancePhase { get; init; }

        internal CancellationTokenSource? CancellationToSignal { get; init; }

        internal Func<
            int,
            InstallationResetActivePublication,
            InstallationResetActivePublication>? RecoveryProjection { get; init; }

        internal Action<HostToolsMarkerPairResetPhase?>? BeforeAdvance { get; init; }

        internal bool ThrowOnRecover { get; init; }

        internal int RecoverCalls { get; private set; }

        public Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {

            RecoverCalls++;

            if (ThrowOnRecover)
            {

                throw new InvalidOperationException(
                    "The active-store sentinel diagnostic must not escape.");

            }

            InstallationResetActivePublication recovered =
                RecoveryProjection?.Invoke(RecoverCalls, current) ?? current;

            return Task.FromResult(Result<InstallationResetActiveRecoveryState>.Success(
                new InstallationResetActiveRecoveryState(
                    InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
                    recovered,
                    LegacyRecord: null)));

        }

        public Task<Result<InstallationResetActivePublication>> BeginAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActivePublication>> AdvanceAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication currentPublication,
            InstallationResetActiveRecord next,
            CancellationToken cancellationToken = default)
        {

            if (HonorCancellation && cancellationToken.IsCancellationRequested)
            {

                return Task.FromCanceled<Result<InstallationResetActivePublication>>(
                    cancellationToken);

            }

            string phase = next.HostToolsMarkerPairReset?.Phase.ToString() ?? "none";

            BeforeAdvance?.Invoke(next.HostToolsMarkerPairReset?.Phase);

            events?.Add("advance:" + phase);

            LastNext = next;

            if (AdvanceSucceeds)
            {

                CovenantDigest digest = Digest(
                    checked((byte)(0x60 + current.Envelope.Revision)));

                InstallationResetActiveEnvelopeV2 envelope = current.Envelope with
                {
                    Revision = current.Envelope.Revision + 1,
                    PreviousEnvelopeDigest = current.EnvelopeDigest,
                };

                InstallationResetActiveAnchorV1 anchor = current.Anchor with
                {
                    Revision = current.Anchor.Revision + 1,
                    EnvelopeDigest = digest,
                };

                current = new InstallationResetActivePublication(
                    current.Location,
                    envelope,
                    digest,
                    InstallationResetActivePayloadV2.FromRecord(next),
                    anchor);

                if (CancelAfterAdvancePhase == next.HostToolsMarkerPairReset?.Phase)
                {

                    CancellationToSignal?.Cancel();

                }

                return Task.FromResult(
                    Result<InstallationResetActivePublication>.Success(current));

            }

            return Task.FromResult(
                Result<InstallationResetActivePublication>.Failure(new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "The test publication remains active.")));

        }

        public Task<Result<InstallationResetActiveRecoveryState>> InspectAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord expectedRecord,
            FileHandleIdentity expectedIdentity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> RetireAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> CompleteStartupCleanupAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingDatabaseSeam(List<string> events)
        : IHostToolsMarkerPairResetDatabaseTestSeam
    {

        internal CancellationToken MarkerClearToken { get; private set; }

        public void BeforeRollback()
        {
        }

        public ValueTask AfterMarkerClearAsync(
            CancellationToken callerCancellationToken)
        {

            events.Add("database-effect");

            MarkerClearToken = callerCancellationToken;

            return ValueTask.CompletedTask;

        }

        public ValueTask BeforeCommitAsync(
            CancellationToken checkpointCancellationToken) =>
            ValueTask.CompletedTask;

    }

    private sealed class FailingOpenDatabase : IHostToolsMarkerPairResetDatabase
    {

        public Task<Result<HostToolsMarkerPairResetDatabaseSession>> OpenAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<HostToolsMarkerPairResetDatabaseSession>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The sentinel database-open diagnostic must not escape.")));

    }

    private sealed class ThrowingDatabase : IHostToolsMarkerPairResetDatabase
    {

        public Task<Result<HostToolsMarkerPairResetDatabaseSession>> OpenAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The database sentinel diagnostic must not escape.");

    }

    private sealed class ThrowingOsPort : IHostToolsMarkerPairResetOsPort
    {

        public HostToolsMarkerPairResetOsOpenResult OpenExact() =>
            throw new InvalidOperationException(
                "The OS-open sentinel diagnostic must not escape.");

        public HostToolsMarkerPairResetOsOpenResult ReopenExact(
            HostProcessToolsOsMarkerEvidence expectedEvidence) =>
            throw new NotSupportedException();

        public Task<HostToolsMarkerPairResetOsDeleteStatus> CompareDeleteExactAsync(
            IHostToolsMarkerPairResetOsCapability capability,
            HostProcessToolsOsMarkerEvidence expectedEvidence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HostToolsMarkerPairResetOsAbsenceStatus> ProveExactAbsenceAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingOsPort : IHostToolsMarkerPairResetOsPort
    {

        private readonly List<string>? _events;

        private readonly HostToolsMarkerPairResetOsOpenResult _openResult;

        private readonly HostToolsMarkerPairResetOsOpenResult _reopenResult;

        private readonly HostToolsMarkerPairResetOsAbsenceStatus _absenceStatus;

        internal RecordingOsPort(
            List<string>? events = null,
            HostToolsMarkerPairResetOsOpenResult? openResult = null,
            HostToolsMarkerPairResetOsOpenResult? reopenResult = null,
            HostToolsMarkerPairResetOsAbsenceStatus absenceStatus =
                HostToolsMarkerPairResetOsAbsenceStatus.Absent)
        {

            _events = events;

            _openResult = openResult ?? HostToolsMarkerPairResetOsOpenResult.Unavailable();

            _reopenResult = reopenResult
                ?? HostToolsMarkerPairResetOsOpenResult.Unavailable();

            _absenceStatus = absenceStatus;

        }

        internal int OpenCalls { get; private set; }

        internal int ReopenCalls { get; private set; }

        internal int AbsenceCalls { get; private set; }

        internal CancellationToken DeleteToken { get; private set; }

        internal IHostToolsMarkerPairResetOsCapability? DeleteCapability
        {
            get;
            private set;
        }

        internal HostProcessToolsOsMarkerEvidence? DeleteExpectedEvidence
        {
            get;
            private set;
        }

        public HostToolsMarkerPairResetOsOpenResult OpenExact()
        {

            OpenCalls++;

            _events?.Add("os");

            return _openResult;

        }

        public HostToolsMarkerPairResetOsOpenResult ReopenExact(
            HostProcessToolsOsMarkerEvidence expectedEvidence)
        {

            ReopenCalls++;

            _events?.Add("os-reopen");

            return _reopenResult;

        }

        public Task<HostToolsMarkerPairResetOsDeleteStatus> CompareDeleteExactAsync(
            IHostToolsMarkerPairResetOsCapability capability,
            HostProcessToolsOsMarkerEvidence expectedEvidence,
            CancellationToken cancellationToken)
        {

            DeleteCapability = capability;

            DeleteExpectedEvidence = expectedEvidence;

            DeleteToken = cancellationToken;

            _events?.Add("os-effect");

            return Task.FromResult(HostToolsMarkerPairResetOsDeleteStatus.Deleted);

        }

        public Task<HostToolsMarkerPairResetOsAbsenceStatus> ProveExactAbsenceAsync(
            CancellationToken cancellationToken)
        {

            AbsenceCalls++;

            _events?.Add("os-absence");

            _events?.Add($"os-absence:{AbsenceCalls}");

            return Task.FromResult(_absenceStatus);

        }

    }

    private sealed class FakeOsCapability : IHostToolsMarkerPairResetOsCapability
    {

        internal int DisposeCalls { get; private set; }

        public void Dispose() => DisposeCalls++;

    }

    private sealed class RecordingMaintenanceConnections(
        ICovenantMaintenanceConnectionFactory inner,
        List<string> events)
        : ICovenantMaintenanceConnectionFactory
    {

        public string DatabasePath => inner.DatabasePath;

        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
        {

            events.Add("database");

            return inner.OpenAsync(cancellationToken);

        }

        public Task<SqliteConnection> OpenReadOnlyAsync(
            CancellationToken cancellationToken) =>
            inner.OpenReadOnlyAsync(cancellationToken);

        public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(
            CancellationToken cancellationToken) =>
            inner.OpenSidecarFreeReadOnlyAsync(cancellationToken);

        public Task<SqliteConnection> OpenSideFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            inner.OpenSideFileAsync(path, cancellationToken);

        public Task AttachSideFileAsync(
            SqliteConnection connection,
            string alias,
            string path,
            CancellationToken cancellationToken) =>
            inner.AttachSideFileAsync(connection, alias, path, cancellationToken);

    }

    private sealed class SuccessfulReadiness
        : IFullInstallationResetCampaignSchemaReadiness
    {

        public Task<Result> RequireExactAsync(
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

    }

    private sealed class ThrowingReadiness
        : IFullInstallationResetCampaignSchemaReadiness
    {

        public Task<Result> RequireExactAsync(
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The readiness sentinel diagnostic must not escape.");

    }

    private sealed class RecordingReadiness(
        List<string> events,
        bool succeeds)
        : IFullInstallationResetCampaignSchemaReadiness
    {

        internal SqliteConnection? Connection { get; private set; }

        public Task<Result> RequireExactAsync(
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken)
        {

            Connection = liveCoreConnection;

            events.Add("readiness");

            return Task.FromResult(succeeds
                ? Result.Success()
                : Result.Failure(new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "The test schema is not exact.")));

        }

    }

    private sealed class RecordingJoiner(HostProcessToolsMarkerPairJoinResult result)
        : IHostProcessToolsMarkerPairJoiner
    {

        internal int Calls { get; private set; }

        internal HostProcessToolsDatabaseMarkerEvidence? Database { get; private set; }

        internal HostProcessToolsOsMarkerEvidence? OsMarker { get; private set; }

        public HostProcessToolsMarkerPairJoinResult Join(
            HostProcessToolsDatabaseMarkerEvidence database,
            HostProcessToolsOsMarkerEvidence? osMarker)
        {

            Calls++;

            Database = database;

            OsMarker = osMarker;

            return result;

        }

    }

    private sealed class ThrowingJoiner : IHostProcessToolsMarkerPairJoiner
    {

        public HostProcessToolsMarkerPairJoinResult Join(
            HostProcessToolsDatabaseMarkerEvidence database,
            HostProcessToolsOsMarkerEvidence? osMarker) =>
            throw new InvalidOperationException(
                "The joiner sentinel diagnostic must not escape.");

    }

    private sealed class AuthorizingVerifier(
        FullInstallationResetRemediationAuthorization authorization)
        : IFullInstallationResetRemediationAttestationVerifier
    {

        internal FullInstallationResetExternalRemediationAttestation? Attestation { get; private set; }

        internal Guid InstallationId { get; private set; }

        internal HostProcessToolsMatchedPair? Pair { get; private set; }

        internal DateTimeOffset AcceptedAtUtc { get; private set; }

        public bool MatchesAuthenticatedClaim(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair,
            Guid acceptedOperationId,
            Guid acceptedInstallationId,
            CovenantDigest acceptedAttestationDigest,
            CovenantDigest acceptedNonceDigest,
            CovenantDigest acceptedIssuerDigest) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> Verify(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> VerifyAtAcceptedTime(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid authenticatedInstallationId,
            HostProcessToolsMatchedPair persistedPair,
            DateTimeOffset acceptedAtUtc)
        {

            Attestation = attestation;

            InstallationId = authenticatedInstallationId;

            Pair = persistedPair;

            AcceptedAtUtc = acceptedAtUtc;

            return authorization;

        }

    }

    private sealed class ThrowingVerifier
        : IFullInstallationResetRemediationAttestationVerifier
    {

        public bool MatchesAuthenticatedClaim(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair,
            Guid acceptedOperationId,
            Guid acceptedInstallationId,
            CovenantDigest acceptedAttestationDigest,
            CovenantDigest acceptedNonceDigest,
            CovenantDigest acceptedIssuerDigest) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> Verify(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> VerifyAtAcceptedTime(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid authenticatedInstallationId,
            HostProcessToolsMatchedPair persistedPair,
            DateTimeOffset acceptedAtUtc) =>
            throw new InvalidOperationException(
                "The verifier sentinel diagnostic must not escape.");

    }

    private sealed class FirstThenRejectingVerifier(
        FullInstallationResetRemediationAuthorization first)
        : IFullInstallationResetRemediationAttestationVerifier
    {

        internal int Calls { get; private set; }

        public bool MatchesAuthenticatedClaim(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair,
            Guid acceptedOperationId,
            Guid acceptedInstallationId,
            CovenantDigest acceptedAttestationDigest,
            CovenantDigest acceptedNonceDigest,
            CovenantDigest acceptedIssuerDigest) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> Verify(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> VerifyAtAcceptedTime(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid authenticatedInstallationId,
            HostProcessToolsMatchedPair persistedPair,
            DateTimeOffset acceptedAtUtc)
        {

            Calls++;

            return Calls == 1
                ? Result<FullInstallationResetRemediationAuthorization>.Success(first)
                : Result<FullInstallationResetRemediationAuthorization>.Failure(new Error(
                    ErrorCodes.Data.ExternalRemediationInvalid,
                    "The test rejects post-publication verification."));

        }

    }

    private sealed class RecordingFullResetLifecycle : ICampaignPathMarkerLifecycle
    {

        private readonly List<string>? _events;

        private readonly CampaignPathFullInstallationResetInventory? _inventory;

        private readonly Action? _afterRevalidate;

        private readonly int? _failRevalidateOnCall;

        private readonly bool _throwOnInventory;

        private readonly bool _throwOnRevalidate;

        private readonly Exception? _revalidateException;

        private readonly Exception? _releaseException;

        internal RecordingFullResetLifecycle(
            List<string>? events = null,
            CampaignPathFullInstallationResetInventory? inventory = null,
            Action? afterRevalidate = null,
            int? failRevalidateOnCall = null,
            bool throwOnInventory = false,
            bool throwOnRevalidate = false,
            Exception? revalidateException = null,
            Exception? releaseException = null)
        {

            _events = events;

            _inventory = inventory;

            _afterRevalidate = afterRevalidate;

            _failRevalidateOnCall = failRevalidateOnCall;

            _throwOnInventory = throwOnInventory;

            _throwOnRevalidate = throwOnRevalidate;

            _revalidateException = revalidateException;

            _releaseException = releaseException;

        }

        internal int InventoryCalls { get; private set; }

        internal int RevalidateCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        internal int PrepareCalls { get; private set; }

        internal int ReconcileCalls { get; private set; }

        /// <summary>The intent vector preparation commits, as the real seam would have journaled it.</summary>
        internal ImmutableArray<Guid> PreparedIntentIds { get; init; } = [];

        internal ulong ReconciledDeletedCount { get; init; }

        internal ulong ReconciledOrphanCount { get; init; }

        internal bool FailPrepare { get; init; }

        internal bool FailReconcile { get; init; }

        internal HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority?
            PrepareAuthority
        {
            get;
            private set;
        }

        internal HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority?
            ReconcileAuthority
        {
            get;
            private set;
        }

        internal CampaignPathFullInstallationResetCleanupReceipt? PrepareExpectedReceipt
        {
            get;
            private set;
        }

        internal CampaignPathFullInstallationResetCleanupReceipt? ReconcilePreparedReceipt
        {
            get;
            private set;
        }

        internal SqliteConnection? PrepareConnection { get; private set; }

        internal SqliteConnection? ReconcileConnection { get; private set; }

        /// <summary>The borrowed connection's state while reconciliation was live, not afterwards.</summary>
        internal System.Data.ConnectionState? ReconcileConnectionState { get; private set; }

        internal SqliteTransaction? PrepareTransaction { get; private set; }

        /// <summary>
        /// Whether the transaction belonged to the borrowed connection <em>while the call was live</em>.
        /// </summary>
        /// <remarks>
        /// Recorded here rather than asserted afterwards: a committed and disposed
        /// <see cref="SqliteTransaction"/> drops its connection reference, so a later comparison would
        /// read null and say nothing about what the seam was handed.
        /// </remarks>
        internal bool? PrepareTransactionBoundToConnection { get; private set; }

        internal Guid? ReleasedOwnerOperationId { get; private set; }

        internal SqliteConnection? InventoryConnection { get; private set; }

        internal SqliteConnection? RevalidateConnection { get; private set; }

        public Task<Result<CampaignPathFullInstallationResetInventory>>
            InventoryFullInstallationResetCleanupAsync(
                Guid ownerOperationId,
                SqliteConnection liveCoreConnection,
                CancellationToken cancellationToken)
        {

            InventoryCalls++;

            if (_throwOnInventory)
            {

                throw new InvalidOperationException(
                    "The inventory sentinel diagnostic must not escape.");

            }

            InventoryConnection = liveCoreConnection;

            _events?.Add("inventory");

            return Task.FromResult(_inventory is null
                ? Result<CampaignPathFullInstallationResetInventory>.Failure(new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "The test Campaign inventory is unavailable."))
                : Result<CampaignPathFullInstallationResetInventory>.Success(_inventory));

        }

        public Task<Result> RevalidateFullInstallationResetInventoryAsync(
            CampaignPathFullInstallationResetInventory inventory,
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken)
        {

            RevalidateCalls++;

            if (_throwOnRevalidate)
            {

                throw new InvalidOperationException(
                    "The inventory-revalidation sentinel diagnostic must not escape.");

            }

            RevalidateConnection = liveCoreConnection;

            _events?.Add("revalidate");

            _afterRevalidate?.Invoke();

            if (_revalidateException is not null)
            {

                throw _revalidateException;

            }

            return Task.FromResult(
                _failRevalidateOnCall == RevalidateCalls
                    ? Result.Failure(new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "The live Campaign inventory changed after journaling."))
                    : Result.Success());

        }

        public Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
            PrepareFullInstallationResetCleanupAsync(
                CampaignPathFullInstallationResetCleanupPreparation preparation,
                CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
                HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority authority,
                SqliteConnection liveCoreConnection,
                SqliteTransaction liveCoreTransaction,
                CancellationToken cancellationToken)
        {

            PrepareCalls++;

            PrepareAuthority = authority;

            PrepareExpectedReceipt = expectedReceipt;

            PrepareConnection = liveCoreConnection;

            PrepareTransaction = liveCoreTransaction;

            PrepareTransactionBoundToConnection =
                ReferenceEquals(liveCoreTransaction.Connection, liveCoreConnection);

            _events?.Add("prepare");

            if (FailPrepare)
            {

                return Task.FromResult(
                    Result<CampaignPathFullInstallationResetCleanupReceipt>.Failure(new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "The test Campaign cleanup preparation is unavailable.")));

            }

            // Built from the preparation the coordinator handed over, so the receipt carries that
            // journal's own owner and effect rather than one the test guessed.
            return Task.FromResult(
                CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
                    preparation.OwnerOperationId,
                    preparation.OwnerEffectDigest,
                    PreparedIntentIds,
                    Value(FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                        PreparedIntentIds))));

        }

        public Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
            ReconcileFullInstallationResetCleanupAsync(
                CampaignPathFullInstallationResetCleanupReceipt prepared,
                HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority authority,
                SqliteConnection liveCoreConnection,
                CancellationToken cancellationToken)
        {

            ReconcileCalls++;

            ReconcileAuthority = authority;

            ReconcilePreparedReceipt = prepared;

            ReconcileConnection = liveCoreConnection;

            ReconcileConnectionState = liveCoreConnection.State;

            _events?.Add("reconcile");

            if (FailReconcile)
            {

                return Task.FromResult(
                    Result<CampaignPathFullInstallationResetCleanupReceipt>.Failure(new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "The test Campaign cleanup reconciliation is unavailable.")));

            }

            return Task.FromResult(
                CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
                    prepared.OwnerOperationId,
                    prepared.OwnerEffectDigest,
                    prepared.OrderedMarkerIntentIds,
                    prepared.MarkerIntentVectorDigest,
                    ReconciledDeletedCount,
                    ReconciledOrphanCount));

        }

        public Task<Result<CampaignPathRestoreCleanupInventory>> InventoryRestoreCleanupAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathRestoreCleanupPreparationReceipt>>
            PrepareRestoreCleanupInStagedDatabaseAsync(
                CampaignPathRestoreCleanupPreparation preparation,
                SqliteConnection stagedConnection,
                SqliteTransaction stagedTransaction,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathMarkerGateCompletion>> ReconcileGateOwnedAsync(
            CampaignPathMarkerGateReconcileRequest request,
            ICovenantExclusiveOperationLease exclusiveLease,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ReleaseRetainedRootsAsync(Guid ownerOperationId)
        {

            ReleaseCalls++;

            ReleasedOwnerOperationId = ownerOperationId;

            _events?.Add("release");

            if (_releaseException is not null)
            {

                throw _releaseException;

            }

            return ValueTask.CompletedTask;

        }

    }

    private sealed class RejectingVerifier
        : IFullInstallationResetRemediationAttestationVerifier
    {

        internal int RecoveryCalls { get; private set; }

        public bool MatchesAuthenticatedClaim(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair,
            Guid acceptedOperationId,
            Guid acceptedInstallationId,
            CovenantDigest acceptedAttestationDigest,
            CovenantDigest acceptedNonceDigest,
            CovenantDigest acceptedIssuerDigest) =>
            false;

        public Result<FullInstallationResetRemediationAuthorization> Verify(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> VerifyAtAcceptedTime(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid authenticatedInstallationId,
            HostProcessToolsMatchedPair persistedPair,
            DateTimeOffset acceptedAtUtc)
        {

            RecoveryCalls++;

            return Result<FullInstallationResetRemediationAuthorization>.Failure(new Error(
                ErrorCodes.Data.ExternalRemediationInvalid,
                "The external remediation attestation could not be verified."));

        }

    }

}
