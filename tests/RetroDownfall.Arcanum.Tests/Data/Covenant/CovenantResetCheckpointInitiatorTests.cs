using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
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
        FakeLongRunningOperationStore store,
        ICovenantErasureEffectDigestCalculator? digests = null,
        CovenantHealthyCatalogErasureGuard? catalog = null,
        ICovenantOperationGate? gate = null) =>
        new(
            store,
            digests ?? new CovenantErasureEffectDigestCalculator(),
            catalog ?? UnusedCatalogGuard(),
            gate ?? new RecordingCovenantOperationGate(),
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

        await using CovenantSchemaScratchDatabase database = await HealthyCatalogAsync();

        (FakeLongRunningOperationStore store, LongRunningOperation operation) =
            await RunningFactoryAsync();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(
                store,
                catalog: CatalogGuard(database),
                gate: gate)
            .PrepareFactoryErasureInventoryAsync(
                operation,
                "owner-118",
                Effect(CovenantExclusiveOperation.HealthyCatalogFactoryErasure),
                requestedOperationId: null,
                CancellationToken.None);

        Assert.True(admitted.IsSuccess);

        LongRunningOperation stored = (await store.GetAsync(operation.Id))!;

        Assert.Equal(DataRetentionFactoryResetCheckpointV1.CurrentVersion, stored.CheckpointVersion);

        Result<DataRetentionFactoryResetCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryReset(stored.CheckpointPayload!);

        Assert.True(decoded.IsSuccess);

        Assert.Equal(CovenantResetPhase.InventoryPrepared, decoded.Value.Phase);

        Assert.Equal(
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            admitted.Value.Owner.Operation);

    }

    [Fact]
    public async Task Factory_requested_identity_is_verified_before_the_v1_checkpoint_is_published()
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

        Assert.Equal(DataRetentionFactoryResetCheckpointV1.CurrentVersion, stored.CheckpointVersion);

        Result<DataRetentionFactoryResetCheckpointV1> checkpoint =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryReset(stored.CheckpointPayload!);

        Assert.True(checkpoint.IsSuccess, checkpoint.IsFailure ? checkpoint.Error.Message : null);

        CovenantDigest expectedEffect = new CovenantErasureEffectDigestCalculator()
            .Compute(effect)
            .Value;

        Assert.Equal(
            CovenantRecoveryCheckpointCodec.EncodeEffectDigest(expectedEffect),
            checkpoint.Value.EffectDigest);

        Assert.Equal(operation.Id, admitted.Value.Owner.OperationId);

        Assert.NotEqual(requested, admitted.Value.Owner.OperationId);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Factory_requested_identity_mismatch_prevents_v1_checkpoint_publication(
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

            Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await Initiator(
                    store,
                    catalog: CatalogGuard(database),
                    gate: gate)
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

        await digests.Entered;

        Task<Result<CovenantExclusiveLease>> replacement = gate.AcquireExclusiveAsync(
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
                CancellationToken.None)
            .AsTask();

        Assert.False(replacement.IsCompleted);

        Assert.Equal(0, (await store.GetAsync(operation.Id))!.CheckpointVersion);

        digests.Release();

        Result<CovenantResetCheckpointInitiator.GateAdmission> admitted = await preparing;

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        Assert.Equal(
            DataRetentionFactoryResetCheckpointV1.CurrentVersion,
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
            database.MaintenanceConnections(),
            CovenantSqliteConnectionInitializer.Instance,
            new CovenantConnectionDrain(),
            new GrimoireSchemaManifestInspector(
                GrimoireSchemaTierOwnershipRegistry.CreateDefault()));

    private static CovenantHealthyCatalogErasureGuard UnusedCatalogGuard() =>
        new(
            new UnreachableMaintenanceConnectionFactory(),
            CovenantSqliteConnectionInitializer.Instance,
            new CovenantConnectionDrain(),
            new GrimoireSchemaManifestInspector(
                GrimoireSchemaTierOwnershipRegistry.CreateDefault()));

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

        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ManualResetEventSlim _release = new(initialState: false);

        private readonly CovenantErasureEffectDigestCalculator _inner = new();

        internal Task Entered => _entered.Task;

        internal void Release() => _release.Set();

        public Result<CovenantDigest> Compute(CovenantErasureEffectDigestInput input)
        {

            _ = _entered.TrySetResult(true);

            _release.Wait();

            return _inner.Compute(input);

        }

        public void Dispose() => _release.Dispose();

    }

    private sealed class UnreachableMaintenanceConnectionFactory : ICovenantMaintenanceConnectionFactory
    {

        public string DatabasePath => throw new NotSupportedException();

        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "Covenant memory-reset preparation must not inspect the factory catalog.");

        public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SqliteConnection> OpenSideFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AttachSideFileAsync(
            SqliteConnection connection,
            string alias,
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

}
