using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Operations;
using RetroDownfall.Arcanum.Tests.Support;

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
        FakeLongRunningOperationStore store) =>
        new(store, new CovenantErasureEffectDigestCalculator(), Clock);

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

    [Fact]
    public async Task A_server_generated_reset_commits_inventory_prepared_before_it_yields_an_owner()
    {

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningMutationAsync();

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(store)
            .PrepareCovenantResetInventoryAsync(
                operation,
                "owner-118",
                Effect(),
                requestedOperationId: null,
                memoryScope: MemoryResetScope.Covenant,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        Assert.Equal(DataRetentionMutationCheckpointV3.CurrentVersion, stored.CheckpointVersion);

        Result<DataRetentionMutationCheckpointV3> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionMutation(stored.CheckpointPayload!);

        Assert.True(decoded.IsSuccess);

        Assert.Equal(CovenantResetPhase.InventoryPrepared, decoded.Value.Covenant!.Phase);

        Assert.Equal(operation.Id, decoded.Value.Covenant.OperationId);

        Assert.Equal(
            CovenantExclusiveOperation.CovenantReset,
            decoded.Value.Covenant.Operation);

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

        CovenantResetEffectArmV1 arm = CovenantRecoveryCheckpointCodec
            .DecodeDataRetentionMutation(stored.CheckpointPayload!)
            .Value
            .Covenant!;

        Assert.Equal(
            CovenantRecoveryCheckpointCodec.RecoveryOwner(arm).Value,
            admitted.Value.Owner);

    }

    /// <summary>
    /// A failed checkpoint commit yields no owner at all, so there is nothing to acquire the gate
    /// with. This is the whole point of the type: the ordering is structural rather than remembered.
    /// </summary>
    [Fact]
    public async Task A_lost_checkpoint_commit_yields_no_owner()
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

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, admitted.Error.Code);

        Assert.Equal(0, (await store.GetAsync(operation.Id))!.CheckpointVersion);

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
                .DecodeDataRetentionMutation((await store.GetAsync(operation.Id))!.CheckpointPayload!)
                .Value
                .Covenant!
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

    [Fact]
    public async Task A_healthy_catalog_factory_erasure_commits_its_own_v1_checkpoint()
    {

        FakeLongRunningOperationStore store = new(Clock);

        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.DataRetentionFactoryReset,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);

        _ = await store.TryAcquireLeaseAsync(
            seeded.Id,
            "owner-118",
            Clock.GetUtcNow(),
            Clock.GetUtcNow().AddMinutes(2));

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(store)
            .PrepareFactoryErasureInventoryAsync(
                (await store.GetAsync(seeded.Id))!,
                "owner-118",
                Effect(CovenantExclusiveOperation.HealthyCatalogFactoryErasure),
                requestedOperationId: null,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess);

        LongRunningOperation stored = (await store.GetAsync(seeded.Id))!;

        Assert.Equal(DataRetentionFactoryResetCheckpointV1.CurrentVersion, stored.CheckpointVersion);

        Result<DataRetentionFactoryResetCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryReset(stored.CheckpointPayload!);

        Assert.True(decoded.IsSuccess);

        Assert.Equal(CovenantResetPhase.InventoryPrepared, decoded.Value.Phase);

        Assert.Equal(
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            admitted.Value.Owner.Operation);

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

}
