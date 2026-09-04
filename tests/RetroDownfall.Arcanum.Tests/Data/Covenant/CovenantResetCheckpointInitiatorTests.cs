using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Operations;
using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Issue #118 — the one seam that makes exclusive-gate acquisition unreachable before the
/// <c>InventoryPrepared</c> checkpoint has actually committed.
/// </summary>
/// <remarks>
/// The gate identity is always the durable server operation id; an optional caller-supplied
/// requested id remains only the normalized replay key. When the caller supplied one, the digest
/// this initiator derives has to equal the one already written to the identity row — otherwise a
/// replayed request could adopt a closed scope for a plan it never named. When the caller supplied
/// none, no identity row exists and the checkpoint is the only durable effect-digest source, so
/// reading a row that was never written would turn an ordinary server-generated reset into a
/// recovery failure (§10.20.3).
/// </remarks>
public sealed class CovenantResetCheckpointInitiatorTests
{

    private static readonly Guid Dataset = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static readonly FakeTimeProvider Clock = NewClock();

    private static FakeTimeProvider NewClock()
    {

        FakeTimeProvider clock = new();

        clock.SetUtcNow(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));

        return clock;

    }

    private static CovenantErasureEffectDigestInput Effect(
        CovenantExclusiveOperation operation = CovenantExclusiveOperation.CovenantReset) =>
        new(
            operation,
            PlanId: "plan-118",
            Dataset,
            Rows: 9,
            ManagedFiles: 2,
            LocalArtifacts: 1,
            AffectedSessions: 4,
            PossibleDisclosures: 0,
            CovenantDisclosureCountKind.Exact);

    private static CovenantResetCheckpointInitiator Initiator(
        FakeLongRunningOperationStore store,
        ICovenantErasureEffectDigestCalculator? digests = null,
        CovenantHealthyCatalogErasureGuard? catalog = null,
        ICovenantOperationGate? gate = null,
        ICovenantErasureInventorySource? inventory = null) =>
        new(
            store,
            digests ?? new CovenantErasureEffectDigestCalculator(),
            catalog ?? UnusedCatalogGuard(),
            gate ?? new RecordingCovenantOperationGate(),
            inventory ?? new StubOfflineTransitionSource(),
            Clock);

    private static async Task<(FakeLongRunningOperationStore Store, LongRunningOperation Operation)>
        RunningMutationAsync()
    {

        FakeLongRunningOperationStore store = new(Clock);

        LongRunningOperation operation = store.Seed(
            LongRunningOperationKinds.DataRetentionMutation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete);

        _ = await store.TryAcquireLeaseAsync(
            operation.Id,
            "owner-118",
            Clock.GetUtcNow(),
            Clock.GetUtcNow().AddMinutes(2));

        return (store, (await store.GetAsync(operation.Id))!);

    }

    private static async Task<(FakeLongRunningOperationStore Store, LongRunningOperation Operation)>
        RunningFactoryAsync()
    {

        FakeLongRunningOperationStore store = new(Clock);

        LongRunningOperation operation = store.Seed(
            LongRunningOperationKinds.DataRetentionFactoryReset,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);

        _ = await store.TryAcquireLeaseAsync(
            operation.Id,
            "owner-118",
            Clock.GetUtcNow(),
            Clock.GetUtcNow().AddMinutes(2));

        return (store, (await store.GetAsync(operation.Id))!);

    }

    private static async Task<(
        FakeLongRunningOperationStore Store,
        LongRunningOperation Operation,
        Guid RequestedOperationId,
        CovenantErasureEffectDigestInput Effect)> RunningNamedFactoryAsync(
            CovenantDigest? storedEffect = null)
    {

        FakeLongRunningOperationStore store = new(Clock);

        Guid requested = Guid.Parse("33333333-3333-4333-8333-333333333333");

        CovenantErasureEffectDigestInput effect = Effect(
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure);

        CovenantDigest identityEffect = storedEffect
            ?? new CovenantErasureEffectDigestCalculator().Compute(effect).Value;

        LongRunningOperationRequestIdentityResult created = await store.ResolveOrCreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionFactoryReset,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Named healthy-catalog factory erasure",
                Clock.GetUtcNow()),
            new LongRunningOperationRequestIdentity(
                requested,
                new CovenantDigest(new byte[32]),
                identityEffect));

        LongRunningOperation operation = created.Operation!;

        _ = await store.TryAcquireLeaseAsync(
            operation.Id,
            "owner-118",
            Clock.GetUtcNow(),
            Clock.GetUtcNow().AddMinutes(2));

        return (store, (await store.GetAsync(operation.Id))!, requested, effect);

    }

    /// <summary>
    /// The row a server-generated reset commits before it yields an owner is the whole launch, and
    /// every field of it is authority a later process would otherwise have to infer.
    /// </summary>
    /// <remarks>
    /// Phase authority now lives in the authenticated journal, so the one thing the row still holds
    /// is the immutable statement of what was committed to: which operation, filed under which
    /// durable kind and recovery policy, against which canonical effect, bound to which exact source
    /// generation and epoch tuple, and advancing to which preselected target. A launch missing any
    /// one of those is a destructive plan a recovery pass would have to guess the rest of, and
    /// guessing is how a plan nobody made gets adopted — so the assertion here is the whole shape
    /// rather than the two fields the old checkpoint happened to carry.
    /// </remarks>
    [Fact]
    public async Task A_server_generated_reset_commits_inventory_prepared_before_it_yields_an_owner()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        StubOfflineTransitionSource inventory = new()
        {

            AcceleratorEpoch = 4,

            KeyReclamationEpoch = 9,

            EnvelopeKeyEpoch = 16,

        };

        long observedRevision = operation.Revision;

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(
                store,
                inventory: inventory)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                memoryScope: MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        Assert.Equal(CovenantResetPhase.InventoryPrepared, admitted.Value.Phase);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        Assert.Equal(CovenantOfflineTransitionLaunchV4.CurrentVersion, stored.CheckpointVersion);

        Result<CovenantOfflineTransitionLaunchV4> decoded = CovenantRecoveryCheckpointCodec
            .DecodeCovenantOfflineTransitionLaunch(stored.CheckpointPayload!);

        Assert.True(decoded.IsSuccess);

        CovenantOfflineTransitionLaunchV4 launch = decoded.Value;

        Assert.Equal(CovenantOfflineTransitionLaunchV4.CurrentVersion, launch.Version);

        Assert.Equal(operation.Id, launch.OperationId);

        Assert.Equal(LongRunningOperationKinds.DataRetentionMutation, launch.OperationKind);

        Assert.Equal(
            nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
            launch.RecoveryPolicy);

        Assert.Equal(CovenantExclusiveOperation.CovenantReset, launch.Operation);

        Assert.Equal(
            CovenantRecoveryCheckpointCodec.EncodeEffectDigest(
                new CovenantErasureEffectDigestCalculator().Compute(Effect()).Value),
            launch.EffectDigest);

        Assert.Equal(Dataset, launch.SourceDatasetGeneration);

        Assert.NotEqual(Guid.Empty, launch.TargetDatasetGeneration);

        Assert.NotEqual(launch.SourceDatasetGeneration, launch.TargetDatasetGeneration);

        Assert.Equal(new CovenantOfflineTransitionEpochsV1(4, 9, 16), launch.SourceEpochs);

        Assert.Equal(new CovenantOfflineTransitionEpochsV1(5, 10, 17), launch.TargetEpochs);

        Assert.Equal(observedRevision, launch.StartingRevision);

        Assert.Equal(observedRevision + 1, stored.Revision);

    }

    /// <summary>
    /// The owner the initiator yields and the owner recovery rebuilds from the committed checkpoint
    /// are the same value. They are the two halves of one adoption check.
    /// </summary>
    [Fact]
    public async Task The_admitted_owner_is_the_owner_recovery_rebuilds_from_the_checkpoint()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        CovenantOfflineTransitionLaunchV4 launch = CovenantRecoveryCheckpointCodec
            .DecodeCovenantOfflineTransitionLaunch(stored.CheckpointPayload!)
            .Value;

        Assert.Equal(
            CovenantRecoveryCheckpointCodec.RecoveryOwner(launch).Value,
            admitted.Value.Owner);

    }

    /// <summary>
    /// The admission carries the row's own reading of the launch, not a second assembly of it.
    /// </summary>
    /// <remarks>
    /// The owner is three of a launch's eleven fields, so an admission carrying only the owner cannot
    /// say which dataset generation and epoch tuple this erasure was admitted to replace. Every field
    /// is asserted against the committed bytes rather than against the values the test supplied,
    /// because a token assembled beside the payload rather than from it could agree with the test and
    /// still disagree with the row an operator would go and read.
    /// </remarks>
    [Fact]
    public async Task The_admitted_launch_is_the_launch_the_committed_row_carries()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        GrimoireOfflineTransitionLaunchBinding projected =
            GrimoireOfflineTransitionLaunch.FromCommittedCheckpoint(
                stored.CheckpointVersion,
                stored.CheckpointPayload!).Value;

        Assert.Equal(projected, admitted.Value.Launch);

        Assert.Equal(projected.Digest, admitted.Value.Launch.Digest);

        // The owner stays exactly what it was, because it is now derived from the launch rather than
        // stored beside it: two copies of the same three fields could disagree, and the one that
        // decides which closed scope is adopted has to be the one the launch records.
        Assert.Equal(
            CovenantRecoveryCheckpointCodec.RecoveryOwner(
                CovenantRecoveryCheckpointCodec
                    .DecodeCovenantOfflineTransitionLaunch(stored.CheckpointPayload!)
                    .Value).Value,
            admitted.Value.Owner);

    }

    /// <summary>
    /// A failed checkpoint commit yields no owner at all, so there is nothing to acquire the gate
    /// with. This is the whole point of the type: the ordering is structural rather than remembered.
    /// </summary>
    /// <remarks>
    /// The lost commit is driven by a row that already carries a checkpoint, because the launch
    /// commits on expected version 0 and a second caller under the same lease is exactly the race
    /// that compare-and-swap exists to lose. Every earlier refusal in the preparation is satisfied
    /// here on purpose — the plan is well formed, the lease is the caller's, and the source tuple
    /// agrees with the effect — so the only thing left to fail is the commit itself.
    /// </remarks>
    [Fact]
    public async Task A_lost_checkpoint_commit_yields_no_owner()
    {

        FakeLongRunningOperationStore store = new(Clock);

        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.DataRetentionMutation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            checkpointVersion: 1);

        _ = await store.TryAcquireLeaseAsync(
            seeded.Id,
            "owner-118",
            Clock.GetUtcNow(),
            Clock.GetUtcNow().AddMinutes(2));

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                (await store.GetAsync(seeded.Id))!,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, admitted.Error.Code);

        LongRunningOperation stored = (await store.GetAsync(seeded.Id))!;

        Assert.Equal(1, stored.CheckpointVersion);

        Assert.Null(stored.CheckpointPayload);

    }

    /// <summary>
    /// A caller that is not the lease holder is refused, and refused before anything is written.
    /// </summary>
    /// <remarks>
    /// The lease is re-read immediately before the launch commits rather than trusted from the
    /// instance the caller passed in, because a lease renewal advances the revision and a launch that
    /// recorded a stale one would name a revision the journal could never be bound past. That re-read
    /// is also where a caller who has lost the lease outright is caught, which is why this refusal is
    /// an integrity failure rather than the manual-recovery escalation a lost commit produces: nothing
    /// durable has been attempted, so there is nothing for an operator to reconcile.
    /// </remarks>
    [Fact]
    public async Task A_caller_that_does_not_hold_the_lease_commits_nothing()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "a-different-owner",
                Effect(),
                requestedOperationId: null,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, admitted.Error.Code);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        Assert.Equal(0, stored.CheckpointVersion);

        Assert.Null(stored.CheckpointPayload);

    }

    [Fact]
    public async Task The_all_null_requested_arm_never_reads_a_nonexistent_identity_row()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        _ = await Initiator(store).PrepareCovenantResetInventoryAsync(
            operation,
            "owner-118",
            Effect(),
            requestedOperationId: null,
            MemoryResetScope.Covenant,
            CancellationToken.None);

        Assert.Equal(0, store.RequestIdentityLookupCount);

    }

    [Fact]
    public async Task A_requested_reset_must_match_the_normalized_identity_row()
    {

        FakeLongRunningOperationStore store = new(Clock);

        Guid requested = Guid.Parse("88888888-8888-8888-8888-888888888888");

        CovenantDigest effect = new CovenantErasureEffectDigestCalculator()
            .Compute(Effect())
            .Value;

        LongRunningOperationRequestIdentityResult created = await store.ResolveOrCreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Covenant reset",
                Clock.GetUtcNow()),
            new LongRunningOperationRequestIdentity(
                requested,
                new CovenantDigest(new byte[32]),
                effect));

        LongRunningOperation operation = created.Operation!;

        _ = await store.TryAcquireLeaseAsync(
            operation.Id,
            "owner-118",
            Clock.GetUtcNow(),
            Clock.GetUtcNow().AddMinutes(2));

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                (await store.GetAsync(operation.Id))!,
                "owner-118",
                Effect(),
                requested,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess);

        Assert.Equal(1, store.RequestIdentityLookupCount);

        Assert.Equal(effect, admitted.Value.Owner.EffectDigest);

        // The gate identity is the durable server operation id even when the caller named one of
        // its own. A requested id that became the gate identity would let two different operations
        // under the same caller-chosen name adopt each other's closed scope.
        Assert.Equal(operation.Id, admitted.Value.Owner.OperationId);

        Assert.NotEqual(requested, admitted.Value.Owner.OperationId);

        Assert.Equal(
            operation.Id,
            CovenantRecoveryCheckpointCodec
                .DecodeCovenantOfflineTransitionLaunch(
                    (await store.GetAsync(operation.Id))!.CheckpointPayload!)
                .Value
                .OperationId);

    }

    [Fact]
    public async Task A_requested_reset_whose_row_names_a_different_effect_is_refused_before_any_checkpoint()
    {

        FakeLongRunningOperationStore store = new(Clock);

        Guid requested = Guid.Parse("99999999-9999-9999-9999-999999999999");

        LongRunningOperationRequestIdentityResult created = await store.ResolveOrCreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Covenant reset",
                Clock.GetUtcNow()),
            new LongRunningOperationRequestIdentity(
                requested,
                new CovenantDigest(new byte[32]),
                new CovenantDigest([.. Enumerable.Repeat((byte)0xAB, 32)])));

        LongRunningOperation operation = created.Operation!;

        _ = await store.TryAcquireLeaseAsync(
            operation.Id,
            "owner-118",
            Clock.GetUtcNow(),
            Clock.GetUtcNow().AddMinutes(2));

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                (await store.GetAsync(operation.Id))!,
                "owner-118",
                Effect(),
                requested,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, admitted.Error.Code);

        Assert.Equal(0, (await store.GetAsync(operation.Id))!.CheckpointVersion);

    }

    [Fact]
    public async Task A_requested_reset_with_no_identity_row_at_all_is_refused()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(),
                Guid.Parse("12121212-1212-1212-1212-121212121212"),
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsFailure);

        Assert.Equal(0, (await store.GetAsync(operation.Id))!.CheckpointVersion);

    }

    /// <summary>
    /// The factory arm commits a launch of its own shape, filed under its own durable kind, and every
    /// field of it says the same thing about which destructive plan is in flight.
    /// </summary>
    /// <remarks>
    /// The two transitions do the same thing to storage and differ only in what they preserve, which
    /// is exactly why they must not share a payload: a row whose meaning depended on the kind column
    /// it was read beside could authorize a Covenant reset from a factory erasure's launch. Pinning
    /// version, ledger kind, recovery policy name and exclusive operation together here is what makes
    /// the wrong one unreadable rather than merely unexpected.
    /// </remarks>
    [Fact]
    public async Task A_healthy_catalog_factory_erasure_commits_its_own_transition_launch()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyCatalogAsync();

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningFactoryAsync();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        StubOfflineTransitionSource inventory = new()
        {

            AcceleratorEpoch = 3,

            KeyReclamationEpoch = 25,

            EnvelopeKeyEpoch = 61,

        };

        long observedRevision = operation.Revision;

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(
                store,
                catalog: CatalogGuard(database),
                gate: gate,
                inventory: inventory)
            .PrepareFactoryErasureInventoryAsync(
                operation,
                "owner-118",
                Effect(CovenantExclusiveOperation.HealthyCatalogFactoryErasure),
                requestedOperationId: null,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        Assert.Equal(CovenantResetPhase.InventoryPrepared, admitted.Value.Phase);

        Assert.Equal(
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            admitted.Value.Owner.Operation);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        Assert.Equal(
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            stored.CheckpointVersion);

        Result<DataRetentionFactoryTransitionLaunchV2> decoded = CovenantRecoveryCheckpointCodec
            .DecodeDataRetentionFactoryTransitionLaunch(stored.CheckpointPayload!);

        Assert.True(decoded.IsSuccess);

        DataRetentionFactoryTransitionLaunchV2 launch = decoded.Value;

        Assert.Equal(DataRetentionFactoryTransitionLaunchV2.CurrentVersion, launch.Version);

        Assert.Equal(operation.Id, launch.OperationId);

        Assert.Equal(LongRunningOperationKinds.DataRetentionFactoryReset, launch.OperationKind);

        Assert.Equal(
            nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
            launch.RecoveryPolicy);

        Assert.Equal(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, launch.Operation);

        Assert.Equal(
            CovenantRecoveryCheckpointCodec.EncodeEffectDigest(
                new CovenantErasureEffectDigestCalculator()
                    .Compute(Effect(CovenantExclusiveOperation.HealthyCatalogFactoryErasure))
                    .Value),
            launch.EffectDigest);

        Assert.Equal(Dataset, launch.SourceDatasetGeneration);

        Assert.NotEqual(Guid.Empty, launch.TargetDatasetGeneration);

        Assert.NotEqual(launch.SourceDatasetGeneration, launch.TargetDatasetGeneration);

        Assert.Equal(new CovenantOfflineTransitionEpochsV1(3, 25, 61), launch.SourceEpochs);

        Assert.Equal(new CovenantOfflineTransitionEpochsV1(4, 26, 62), launch.TargetEpochs);

        Assert.Equal(observedRevision, launch.StartingRevision);

        Assert.Equal(observedRevision + 1, stored.Revision);

    }

    [Fact]
    public async Task Factory_requested_identity_is_verified_before_the_launch_is_published()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyCatalogAsync();

        (FakeLongRunningOperationStore store, LongRunningOperation operation, Guid requested, CovenantErasureEffectDigestInput effect) =
            await RunningNamedFactoryAsync();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(
                store,
                catalog: CatalogGuard(database),
                gate: gate)
            .PrepareFactoryErasureInventoryAsync(
                operation,
                "owner-118",
                effect,
                requested,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        Assert.Equal(1, store.RequestIdentityLookupCount);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        Assert.Equal(
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            stored.CheckpointVersion);

        Result<DataRetentionFactoryTransitionLaunchV2> launch = CovenantRecoveryCheckpointCodec
            .DecodeDataRetentionFactoryTransitionLaunch(stored.CheckpointPayload!);

        Assert.True(launch.IsSuccess, launch.IsFailure ? launch.Error.Message : null);

        CovenantDigest expectedEffect = new CovenantErasureEffectDigestCalculator()
            .Compute(effect)
            .Value;

        Assert.Equal(
            CovenantRecoveryCheckpointCodec.EncodeEffectDigest(expectedEffect),
            launch.Value.EffectDigest);

        Assert.Equal(operation.Id, admitted.Value.Owner.OperationId);

        Assert.NotEqual(requested, admitted.Value.Owner.OperationId);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Factory_requested_identity_mismatch_prevents_launch_publication(
        bool mismatchRequestedId)
    {

        await using CovenantSchemaScratchDatabase database = await HealthyCatalogAsync();

        CovenantDigest? storedEffect = mismatchRequestedId
            ? null
            : new CovenantDigest([.. Enumerable.Repeat((byte)0xAB, 32)]);

        (FakeLongRunningOperationStore store, LongRunningOperation operation, Guid requested, CovenantErasureEffectDigestInput effect) =
            await RunningNamedFactoryAsync(storedEffect);

        Guid suppliedRequestedId = mismatchRequestedId
            ? Guid.Parse("34343434-3434-4434-8434-343434343434")
            : requested;

        Result<CovenantResetCheckpointInitiator.GateAdmission> refused = await Initiator(
                store,
                catalog: CatalogGuard(database),
                gate: CovenantOperationGateFixture.CreateGate())
            .PrepareFactoryErasureInventoryAsync(
                operation,
                "owner-118",
                effect,
                suppliedRequestedId,
                CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, refused.Error.Code);

        Assert.Equal(1, store.RequestIdentityLookupCount);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        Assert.Equal(0, stored.CheckpointVersion);

        Assert.Null(stored.CheckpointPayload);

    }

    [Fact]
    public async Task Factory_preparation_uses_the_callers_exact_installation_snapshot_without_nesting()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyCatalogAsync();

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningFactoryAsync();

        RecordingCovenantOperationGate gate = new();

        CovenantInstallationReadLease readLease = (await gate
            .AcquireInstallationReadAsync(CancellationToken.None)).Value;

        await using (readLease.ConfigureAwait(false))
        {

            StubOfflineTransitionSource inventory = new()
            {

                DatasetGeneration = readLease.Snapshot.DatasetGeneration!.Value,

            };

            Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(
                    store,
                    catalog: CatalogGuard(database),
                    gate: gate,
                    inventory: inventory)
                .PrepareFactoryErasureInventoryAsync(
                    operation,
                    "owner-118",
                    Effect(CovenantExclusiveOperation.HealthyCatalogFactoryErasure) with
                    {

                        DatasetGeneration = readLease.Snapshot.DatasetGeneration!.Value,

                    },
                    requestedOperationId: null,
                    readLease,
                    CancellationToken.None);

            Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

            Assert.Equal(["installation-read"], gate.Acquisitions);

            Assert.Equal(1, gate.PeakConcurrentLeases);

            Assert.Equal(1, gate.LiveLeases);

        }

        Assert.Equal(0, gate.LiveLeases);

    }

    [Fact]
    public async Task Factory_preparation_refuses_a_different_planning_snapshot_before_catalog_or_checkpoint()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningFactoryAsync();

        RecordingCovenantOperationGate gate = new();

        CovenantInstallationReadLease readLease = (await gate
            .AcquireInstallationReadAsync(CancellationToken.None)).Value;

        await using (readLease.ConfigureAwait(false))
        {

            CountingDigestCalculator digests = new();

            Result<CovenantResetCheckpointInitiator.GateAdmission> refused = await Initiator(
                    store,
                    digests,
                    UnusedCatalogGuard(),
                    gate)
                .PrepareFactoryErasureInventoryAsync(
                    operation,
                    "owner-118",
                    Effect(CovenantExclusiveOperation.HealthyCatalogFactoryErasure),
                    requestedOperationId: null,
                    readLease,
                    CancellationToken.None);

            Assert.True(refused.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, refused.Error.Code);

            Assert.Equal(0, digests.ComputeCount);

            Assert.Equal(0, (await store.GetAsync(operation.Id))!.CheckpointVersion);

            Assert.Equal(["installation-read"], gate.Acquisitions);

        }

    }

    [Fact]
    public async Task Damaged_factory_catalog_refuses_before_digest_checkpoint_or_admission()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyCatalogAsync();

        await database.ExecuteAsync("DROP TRIGGER covenant_entries_guard_delete;", CancellationToken.None);

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningFactoryAsync();

        CountingDigestCalculator digests = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantResetCheckpointInitiator.GateAdmission> refused = await Initiator(
                store,
                digests,
                CatalogGuard(database),
                gate)
            .PrepareFactoryErasureInventoryAsync(
                operation,
                "owner-118",
                Effect(CovenantExclusiveOperation.HealthyCatalogFactoryErasure),
                requestedOperationId: null,
                CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, refused.Error.Code);

        Assert.Equal(0, digests.ComputeCount);

        Assert.Equal(0, (await store.GetAsync(operation.Id))!.CheckpointVersion);

        Result<CovenantExclusiveLease> acquired = await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.BackupRestore),
            CancellationToken.None);

        Assert.True(acquired.IsSuccess);

        await using CovenantExclusiveLease lease = acquired.Value;

        Assert.True((await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task Exclusive_replacement_cannot_win_between_catalog_proof_and_checkpoint_commit()
    {

        await using CovenantSchemaScratchDatabase database = await HealthyCatalogAsync();

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningFactoryAsync();

        using BlockingDigestCalculator digests = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Task<Result<CovenantResetCheckpointInitiator.GateAdmission>> preparing = Task.Run(
            async () => await Initiator(store, digests, CatalogGuard(database), gate)
                .PrepareFactoryErasureInventoryAsync(
                    operation,
                    "owner-118",
                    Effect(CovenantExclusiveOperation.HealthyCatalogFactoryErasure),
                    requestedOperationId: null,
                    CancellationToken.None));

        await digests.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Task<Result<CovenantExclusiveLease>> replacement = gate.AcquireExclusiveAsync(
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
                CancellationToken.None)
            .AsTask();

        try
        {

            Assert.False(replacement.IsCompleted);

            Assert.Equal(0, (await store.GetAsync(operation.Id))!.CheckpointVersion);

        }
        finally
        {
            digests.Release();
        }

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await preparing;

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        Assert.Equal(
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            (await store.GetAsync(operation.Id))!.CheckpointVersion);

        Result<CovenantExclusiveLease> replaced = await replacement;

        Assert.True(replaced.IsSuccess, replaced.IsFailure ? replaced.Error.Message : null);

        await using CovenantExclusiveLease lease = replaced.Value;

        Assert.True((await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task Covenant_memory_reset_does_not_acquire_the_factory_catalog_read_lease()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        RecordingCovenantOperationGate gate = new();

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(
                store,
                gate: gate)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess);

        Assert.Empty(gate.Acquisitions);

    }

    /// <summary>
    /// Each path may only prepare its own operation. Passing the other one's effect input would mint
    /// an owner whose code and whose checkpoint disagreed about what the operation is.
    /// </summary>
    [Fact]
    public async Task Neither_path_accepts_the_other_operations_effect_input()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        Result<CovenantResetCheckpointInitiator.GateAdmission> crossed = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(CovenantExclusiveOperation.HealthyCatalogFactoryErasure),
                requestedOperationId: null,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(crossed.IsFailure);

        Assert.Equal(0, (await store.GetAsync(operation.Id))!.CheckpointVersion);

    }

    /// <summary>
    /// Only the Covenant memory scope has a reset arm. Every other scope is an ordinary retention
    /// mutation and never closes admission.
    /// </summary>
    [Theory]
    [InlineData(MemoryResetScope.Entry)]
    [InlineData(MemoryResetScope.Attachments)]
    [InlineData(MemoryResetScope.Workspace)]
    [InlineData(MemoryResetScope.Saga)]
    [InlineData(MemoryResetScope.Lexicon)]
    public async Task No_other_memory_scope_can_prepare_a_covenant_reset(MemoryResetScope scope)
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        Result<CovenantResetCheckpointInitiator.GateAdmission> refused = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                scope,
                CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(0, (await store.GetAsync(operation.Id))!.CheckpointVersion);

    }

    [Fact]
    public async Task Preparing_the_same_inventory_twice_neither_advances_nor_rewrites_the_phase()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        _ = await Initiator(store).PrepareCovenantResetInventoryAsync(
            operation,
            "owner-118",
            Effect(),
            requestedOperationId: null,
            MemoryResetScope.Covenant,
            CancellationToken.None);

        LongRunningOperation afterFirst = (await store.GetAsync(operation.Id))!;

        Result<CovenantResetCheckpointInitiator.GateAdmission> second = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                afterFirst,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(second.IsFailure);

        LongRunningOperation afterSecond = (await store.GetAsync(operation.Id))!;

        Assert.Equal(afterFirst.CheckpointVersion, afterSecond.CheckpointVersion);

        Assert.Equal(afterFirst.CheckpointPayload, afterSecond.CheckpointPayload);

    }

    /// <summary>
    /// A launch may only bind the same dataset its effect digest was computed over.
    /// </summary>
    /// <remarks>
    /// The plan and the canonical state it launches against describe one transition or they describe
    /// two. Reading the source tuple here rather than accepting it from the caller is what makes the
    /// disagreement observable at all — and it has to be a refusal, because a launch that bound one
    /// generation while its digest named another would later let a recovery pass verify a replaced
    /// family against a plan nobody made and call the mismatch its own work. Refusing before the
    /// checkpoint commits keeps the answer cheap: nothing durable has been said yet, and admission is
    /// still open.
    /// </remarks>
    [Fact]
    public async Task A_source_state_naming_a_different_dataset_is_refused_before_any_checkpoint()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        StubOfflineTransitionSource elsewhere = new()
        {

            DatasetGeneration = Guid.Parse("55555555-5555-4555-8555-555555555555"),

        };

        Result<CovenantResetCheckpointInitiator.GateAdmission> refused = await Initiator(
                store,
                inventory: elsewhere)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, refused.Error.Code);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        Assert.Equal(0, stored.CheckpointVersion);

        Assert.Null(stored.CheckpointPayload);

    }

    /// <summary>
    /// Each preselected target epoch is the successor of its own source, and of no other.
    /// </summary>
    /// <remarks>
    /// The canonical transaction advances all three counters by one in the same statement, so a
    /// launch whose accelerator and envelope targets were transposed would still satisfy any rule
    /// asking only whether every target is one past some source — and would then verify a replaced
    /// family against epochs belonging to the wrong counter. Three sources far apart from one another
    /// is what turns that transposition into a failing assertion rather than a coincidence: with
    /// 1/1/1 sources the correct pairing and every wrong one produce the same tuple.
    /// </remarks>
    [Fact]
    public async Task Each_preselected_target_epoch_is_the_successor_of_its_own_source()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        StubOfflineTransitionSource inventory = new()
        {

            AcceleratorEpoch = 7,

            KeyReclamationEpoch = 41,

            EnvelopeKeyEpoch = 900,

        };

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(
                store,
                inventory: inventory)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        CovenantOfflineTransitionLaunchV4 launch = CovenantRecoveryCheckpointCodec
            .DecodeCovenantOfflineTransitionLaunch(
                (await store.GetAsync(operation.Id))!.CheckpointPayload!)
            .Value;

        Assert.Equal(new CovenantOfflineTransitionEpochsV1(7, 41, 900), launch.SourceEpochs);

        Assert.Equal(8UL, launch.TargetEpochs.AcceleratorEpoch);

        Assert.Equal(42UL, launch.TargetEpochs.KeyReclamationEpoch);

        Assert.Equal(901UL, launch.TargetEpochs.EnvelopeKeyEpoch);

    }

    private static async Task<CovenantSchemaScratchDatabase> HealthyCatalogAsync()
    {

        CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        try
        {

            await database.InstallHealthyCovenantCatalogAsync(
                withAccelerator: true,
                CancellationToken.None);

            return database;

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

    }

    private static CovenantHealthyCatalogErasureGuard CatalogGuard(
        CovenantSchemaScratchDatabase database) =>
        new(
            new RecordingFreshOrdinaryConnectionFactory(
                database.Connection.ConnectionString),
            new GrimoireSchemaManifestInspector(
                GrimoireSchemaTierOwnershipRegistry.CreateDefault()));

    private static CovenantHealthyCatalogErasureGuard UnusedCatalogGuard() =>
        new(
            new UnreachableOrdinaryConnectionFactory(),
            new GrimoireSchemaManifestInspector(
                GrimoireSchemaTierOwnershipRegistry.CreateDefault()));

    /// <summary>
    /// The canonical source tuple a launch preselects its target against, under the test's control.
    /// </summary>
    /// <remarks>
    /// The generation defaults to the one <see cref="Effect"/> names, because the initiator refuses a
    /// plan whose digest was computed over a different dataset than the source state it would launch
    /// against — a stub returning a fresh generation would turn every preparation in this file into
    /// an integrity refusal and hide whatever each test was actually about. It is settable so the one
    /// test that means to drive that refusal can, and so the exact-snapshot arm can hand back the
    /// generation its planning lease is holding.
    ///
    /// <para>Every other member of the interface throws rather than returning a stub success:
    /// checkpoint preparation reads the source tuple and nothing else, and an initiator that reached
    /// for an inventory page or a disclosure count would be doing work that belongs after admission
    /// closes. Failing loudly makes that a red test instead of a silent pass.</para>
    /// </remarks>
    private sealed class StubOfflineTransitionSource : ICovenantErasureInventorySource
    {

        internal Guid DatasetGeneration { get; init; } = Dataset;

        internal ulong AcceleratorEpoch { get; init; } = 1;

        internal ulong KeyReclamationEpoch { get; init; } = 1;

        internal ulong EnvelopeKeyEpoch { get; init; } = 1;

        public Task<Result<CovenantOfflineTransitionSourceState>> ReadOfflineTransitionSourceStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantOfflineTransitionSourceState>.Success(
                    new CovenantOfflineTransitionSourceState(
                        DatasetGeneration,
                        AcceleratorEpoch,
                        KeyReclamationEpoch,
                        EnvelopeKeyEpoch)));

        public Task<Result<CovenantErasureInventorySummary>> PreflightBeforeCanonicalAsync(
            CovenantExclusiveOperation operation,
            Guid datasetGeneration,
            CovenantClosedPeriodAuthority authority,
            CancellationToken cancellationToken) =>
            throw OutsidePreparation();

        public Task<Result> PreflightRemainingManagedAsync(
            CovenantClosedPeriodAuthority authority,
            CancellationToken cancellationToken) =>
            throw OutsidePreparation();

        public Task<Result<CovenantDatabaseErasureBatch>> ReadNextDatabaseBatchAsync(
            Guid datasetGeneration,
            Guid? afterLabelId,
            CovenantClosedPeriodAuthority authority,
            CancellationToken cancellationToken) =>
            throw OutsidePreparation();

        public Task<Result<CovenantManagedFileErasureBatch>> ReadNextManagedFileBatchAsync(
            Guid operationId,
            Guid? afterLabelId,
            CovenantClosedPeriodAuthority authority,
            CancellationToken cancellationToken) =>
            throw OutsidePreparation();

        public Task<Result<CovenantDisclosureExposure>> ReadDisclosureExposureAsync(
            CovenantClosedPeriodAuthority authority,
            CancellationToken cancellationToken) =>
            throw OutsidePreparation();

        private static NotSupportedException OutsidePreparation() =>
            new("Checkpoint preparation reads only the offline-transition source state.");

    }

    private sealed class CountingDigestCalculator : ICovenantErasureEffectDigestCalculator
    {

        private readonly CovenantErasureEffectDigestCalculator _inner = new();

        internal int ComputeCount { get; private set; }

        public Result<CovenantDigest> Compute(CovenantErasureEffectDigestInput input)
        {

            ComputeCount++;

            return _inner.Compute(input);

        }

    }

    private sealed class BlockingDigestCalculator : ICovenantErasureEffectDigestCalculator, IDisposable
    {

        private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ManualResetEventSlim _release = new(initialState: false);

        private readonly CovenantErasureEffectDigestCalculator _inner = new();

        internal Task Entered => _entered.Task;

        internal void Release() => _release.Set();

        public Result<CovenantDigest> Compute(CovenantErasureEffectDigestInput input)
        {

            _ = _entered.TrySetResult(true);

            // The production caller parks here while it holds its lock. A test that fails before it calls
            // Release() disposes its harness under that same lock, so an unbounded wait would hang the
            // whole run; the bound turns that into a red test instead.
            _release.Wait(ReleaseTimeout);

            return _inner.Compute(input);

        }

        public void Dispose()
        {

            _release.Set();

            _release.Dispose();

        }

    }

    private sealed class UnreachableOrdinaryConnectionFactory
        : IGrimoireOrdinaryConnectionFactory
    {

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "Covenant memory-reset preparation must not inspect the factory catalog.");

    }

}
