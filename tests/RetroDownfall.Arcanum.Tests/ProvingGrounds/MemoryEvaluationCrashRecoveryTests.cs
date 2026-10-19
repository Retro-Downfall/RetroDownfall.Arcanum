using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using static RetroDownfall.Arcanum.Tests.ProvingGrounds.MemoryEvaluationEvidenceStoreTests;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationCrashRecoveryTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_v0_creation_gap_reaches_real_recovery_without_fabricating_report(bool acquire)
    {
        await using var h = await SagaStoreHarness.CreateAsync();

        var store = new LongRunningOperationStore(h.Context, TestOrdinaryConnectionFactory.For(h.Context));

        var registration = Registration([Source()]);

        var identity = new LongRunningOperationRequestIdentity(registration.EvaluationId, registration.RequestDigest, registration.PlanDigest);

        var request = new LongRunningOperationCreateRequest("memory-evaluation", LongRunningOperationRecoveryPolicy.AbandonSafely, "Memory evaluation", DateTimeOffset.UtcNow.AddMinutes(-5));

        var created = await store.ResolveOrCreateAsync(request, identity);

        var operation = created.Operation!;

        Assert.NotEqual(registration.EvaluationId, operation.Id);

        if (acquire)
            await store.TryAcquireLeaseAsync(operation.Id, "dead-request", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4));

        var evidence = new MemoryEvaluationEvidenceStore(new ConnectionSource((SqliteConnection)h.Connection));

        var handler = new MemoryEvaluationRecoveryHandler(store, evidence, new ConnectionSource((SqliteConnection)h.Connection), new TurnRunWriter(h.Context), new BudgetReservationService(h.Context, h.Options), TimeProvider.System);

        var reconciler = new LongRunningOperationReconciler(store, [handler], TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        var summary = await reconciler.ReconcileNowAsync("recovery");

        var recovered = (await store.GetAsync(operation.Id))!;

        Assert.Equal(LongRunningOperationState.Abandoned, recovered.State);

        Assert.Equal("MemoryEvaluation.NotPrepared", recovered.TerminalErrorCode);

        Assert.Equal(1, summary.Abandoned);

        Assert.True((await evidence.GetAsync(registration.EvaluationId, default)).IsFailure);

        Assert.Equal(identity, await store.FindRequestIdentityAsync(operation.Id));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_v0_registration_gap_interrupts_once_and_preserves_existing_terminal_report(bool completed)
    {
        await using var h = await SagaStoreHarness.CreateAsync();

        var store = new LongRunningOperationStore(h.Context, TestOrdinaryConnectionFactory.For(h.Context));

        var r = Registration([Source()]);

        var created = await store.ResolveOrCreateAsync(new("memory-evaluation", LongRunningOperationRecoveryPolicy.AbandonSafely, "Memory evaluation", DateTimeOffset.UtcNow.AddMinutes(-5)), new(r.EvaluationId, r.RequestDigest, r.PlanDigest));

        r = r with { OperationId = created.Operation!.Id };

        var connections = new ConnectionSource((SqliteConnection)h.Connection);

        var evidence = new MemoryEvaluationEvidenceStore(connections);

        Assert.True((await evidence.RegisterAsync(r, [Source()], default)).IsSuccess);

        MemoryEvaluationStoredEvidence? before = null;

        if (completed)
            before = (await evidence.CompleteAsync(r.EvaluationId, Report(r, [Source()]), default)).Value;

        var handler = new MemoryEvaluationRecoveryHandler(store, evidence, connections, new TurnRunWriter(h.Context), new BudgetReservationService(h.Context, h.Options), TimeProvider.System);

        var reconciler = new LongRunningOperationReconciler(store, [handler], TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        await reconciler.ReconcileNowAsync("recovery");

        var retained = (await evidence.GetAsync(r.EvaluationId, default)).Value;

        Assert.NotNull(retained.Report);

        Assert.Equal(MemoryEvaluationOutcome.InsufficientEvidence, retained.Report.Decision.Outcome);

        Assert.Equal(MemoryEvaluationReason.Interrupted, retained.Report.Decision.Reason);

        Assert.Null(retained.Report.Cost);

        Assert.Null(retained.Report.InputTokens);

        Assert.Equal(0, retained.Report.Aggregate.CompletedPairs);

        await handler.RecoverAsync((await store.GetAsync(r.OperationId))!, default);

        Assert.Equal(retained.ReportDigest, (await evidence.GetAsync(r.EvaluationId, default)).Value.ReportDigest);

        if (before is not null) Assert.Equal(before.ReportDigest, retained.ReportDigest);

        Assert.Equal(completed ? LongRunningOperationState.Completed : LongRunningOperationState.Abandoned, (await store.GetAsync(r.OperationId))!.State);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Committed_v1_crash_progress_never_turns_a_paid_prefix_into_complete_spend_or_scores(int physicalSends)
    {
        await using var h = await SagaStoreHarness.CreateAsync();

        var store = new LongRunningOperationStore(h.Context, TestOrdinaryConnectionFactory.For(h.Context));

        var clock = new ManualWallClockTimeProvider();

        var coordinator = new LongRunningOperationCoordinator(store, clock);

        var r = Registration([Source()]);

        var started = await coordinator.StartWithRequestIdentityAsync(new("memory-evaluation", LongRunningOperationRecoveryPolicy.AbandonSafely, "Memory evaluation", clock.GetUtcNow()), new(r.EvaluationId, r.RequestDigest, r.PlanDigest), "dead-request", TimeSpan.FromMinutes(2));

        Assert.True(started.IsSuccess);

        r = r with { OperationId = started.Value.Operation!.Id };

        var connections = new ConnectionSource((SqliteConnection)h.Connection);

        var evidence = new MemoryEvaluationEvidenceStore(connections);

        Assert.True((await evidence.RegisterAsync(r, [Source()], default)).IsSuccess);

        int pairs = physicalSends / 2;

        var aggregate = Report(r, [Source()]).Aggregate with
        {
            CompletedPairs = pairs,
            ValidPairs = pairs,
            EquivalentPairs = pairs,
            FailureReason = MemoryEvaluationReason.None,
            Checks = [Report(r, [Source()]).Aggregate.Checks[0] with { PassPass = pairs }]
        };

        var checkpoint = new MemoryEvaluationCheckpoint(r.EvaluationId, r.OperationId, r.RequestDigest, r.PlanDigest, MemoryEvaluationPhase.Evaluating, 3, physicalSends, aggregate, physicalSends * 10L, physicalSends, physicalSends * 0.01m, 1);

        Assert.True(await coordinator.CheckpointAsync(r.OperationId, "dead-request", 0, 1, MemoryEvaluationCheckpointCodec.Encode(checkpoint), null, "Memory evaluation"));

        clock.Advance(TimeSpan.FromMinutes(3));

        var handler = new MemoryEvaluationRecoveryHandler(store, evidence, connections, new TurnRunWriter(h.Context), new BudgetReservationService(h.Context, h.Options), clock);

        var reconciler = new LongRunningOperationReconciler(store, [handler], clock, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        Assert.Equal(1, (await reconciler.ReconcileNowAsync("recovery")).Abandoned);

        var retained = (await evidence.GetAsync(r.EvaluationId, default)).Value;

        Assert.Equal(MemoryEvaluationOutcome.InsufficientEvidence, retained.Report!.Decision.Outcome);

        Assert.Equal(MemoryEvaluationReason.Interrupted, retained.Report.Decision.Reason);

        Assert.Equal(pairs, retained.Report.Aggregate.ValidPairs);

        Assert.Null(retained.Report.Cost);

        Assert.Null(retained.Report.InputTokens);

        Assert.Null(retained.Report.OutputTokens);

        await handler.RecoverAsync((await store.GetAsync(r.OperationId))!, default);

        Assert.Equal(retained.ReportDigest, (await evidence.GetAsync(r.EvaluationId, default)).Value.ReportDigest);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Corrupt_checkpoint_fails_closed_before_writing_evidence(int version)
    {
        await using var h = await SagaStoreHarness.CreateAsync();

        var store = new LongRunningOperationStore(h.Context, TestOrdinaryConnectionFactory.For(h.Context));

        var r = Registration([Source()]);

        var operation = (await store.ResolveOrCreateAsync(new("memory-evaluation", LongRunningOperationRecoveryPolicy.AbandonSafely, "Memory evaluation", DateTimeOffset.UtcNow), new(r.EvaluationId, r.RequestDigest, r.PlanDigest))).Operation!;

        var connections = new ConnectionSource((SqliteConnection)h.Connection);

        var evidence = new MemoryEvaluationEvidenceStore(connections);

        var handler = new MemoryEvaluationRecoveryHandler(store, evidence, connections, new TurnRunWriter(h.Context), new BudgetReservationService(h.Context, h.Options), TimeProvider.System);

        var outcome = await handler.RecoverAsync(operation with { CheckpointVersion = version, CheckpointPayload = "{\"unexpected\":true}"u8.ToArray() }, default);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, outcome.State);

        Assert.Equal(LongRunningOperationErrorCodes.CorruptCheckpoint, outcome.ErrorCode);

        Assert.True((await evidence.GetAsync(r.EvaluationId, default)).IsFailure);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Erased_v1_evidence_recovers_exact_arm_runs_without_restoring_report_or_guessing_usage(bool duplicate)
    {
        await using var h = await SagaStoreHarness.CreateAsync();

        var store = new LongRunningOperationStore(h.Context, TestOrdinaryConnectionFactory.For(h.Context));

        var r = Registration([Source()]);

        var operation = (await store.ResolveOrCreateAsync(new("memory-evaluation", LongRunningOperationRecoveryPolicy.AbandonSafely, "Memory evaluation", DateTimeOffset.UtcNow), new(r.EvaluationId, r.RequestDigest, r.PlanDigest))).Operation!;

        r = r with { OperationId = operation.Id };

        var connections = new ConnectionSource((SqliteConnection)h.Connection);

        var evidence = new MemoryEvaluationEvidenceStore(connections);

        Assert.True((await evidence.RegisterAsync(r, [Source()], default)).IsSuccess);

        var terminal = (await evidence.CompleteAsync(r.EvaluationId, Report(r, [Source()]), default)).Value;

        Assert.True((await evidence.DeleteAsync(new(r.EvaluationId, terminal.ReportDigest!.Value, true), default)).IsSuccess);

        var runs = new TurnRunWriter(h.Context);

        var last = await runs.StartRunAsync(new($"{r.EvaluationId:N}:5", null, "memory-evaluation", "counterfactual-arm", null, DateTimeOffset.UtcNow));

        var lookalike = await runs.StartRunAsync(new($"{r.EvaluationId:N}:50", null, "memory-evaluation", "counterfactual-arm", null, DateTimeOffset.UtcNow));

        var session = await runs.StartRunAsync(new($"{r.EvaluationId:N}:0", Guid.NewGuid(), "memory-evaluation", "counterfactual-arm", null, DateTimeOffset.UtcNow));

        if (duplicate) await runs.StartRunAsync(new($"{r.EvaluationId:N}:5", null, "memory-evaluation", "counterfactual-arm", null, DateTimeOffset.UtcNow));

        var checkpoint = new MemoryEvaluationCheckpoint(r.EvaluationId, r.OperationId, r.RequestDigest, r.PlanDigest, MemoryEvaluationPhase.Sending, 3, 0, Report(r, [Source()]).Aggregate, null, null, null, 0);

        operation = operation with { CheckpointVersion = 1, CheckpointPayload = JsonSerializer.SerializeToUtf8Bytes(checkpoint, GrimoireJsonContext.Default.MemoryEvaluationCheckpoint) };

        var handler = new MemoryEvaluationRecoveryHandler(store, evidence, connections, runs, new BudgetReservationService(h.Context, h.Options), TimeProvider.System);

        var recovered = await handler.RecoverAsync(operation, default);

        Assert.Equal(duplicate ? LongRunningOperationState.ReconciliationRequired : LongRunningOperationState.Abandoned, recovered.State);

        Assert.True((await evidence.GetAsync(r.EvaluationId, default)).IsFailure);

        Assert.Equal(duplicate ? 0L : 3L, await Status(last));

        Assert.Equal(0L, await Status(lookalike));

        Assert.Equal(0L, await Status(session));

        await using var bills = h.Connection.CreateCommand();

        bills.CommandText = "SELECT COUNT(*) FROM BillableOperations";

        Assert.Equal(0L, await bills.ExecuteScalarAsync());

        async Task<object?> Status(Guid id)
        {
            await using var c = h.Connection.CreateCommand();

            c.CommandText = "SELECT Status FROM InferenceRuns WHERE Id=$id";

            var parameter = c.CreateParameter(); parameter.ParameterName = "$id"; parameter.Value = id.ToString("N"); c.Parameters.Add(parameter);

            return await c.ExecuteScalarAsync();
        }
    }

    [SkippableFact]
    public async Task Initial_start_grace_is_exact_and_stale_discovery_cannot_steal_a_live_or_claimed_owner()
    {
        await using var h = await SagaStoreHarness.CreateAsync();

        var store = new LongRunningOperationStore(h.Context, TestOrdinaryConnectionFactory.For(h.Context));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var r = Registration([Source()]);

        var operation = (await store.ResolveOrCreateAsync(new("memory-evaluation", LongRunningOperationRecoveryPolicy.AbandonSafely, "Memory evaluation", now), new(r.EvaluationId, r.RequestDigest, r.PlanDigest))).Operation!;

        var other = await store.CreateAsync(new(LongRunningOperationKinds.Subagent, LongRunningOperationRecoveryPolicy.AbandonSafely, "Unstarted child", now.AddHours(-1)));

        Assert.Empty(await store.FindExpiredAsync(now + MemoryEvaluationLimits.InitialStartGrace - TimeSpan.FromTicks(1), 10));

        var stale = Assert.Single(await store.FindExpiredAsync(now + MemoryEvaluationLimits.InitialStartGrace, 10));

        Assert.Equal(operation.Id, stale.Id);

        Assert.NotEqual(other.Id, stale.Id);

        var live = await store.TryAcquireLeaseAsync(operation.Id, "live", now + MemoryEvaluationLimits.InitialStartGrace, now.AddMinutes(4));

        Assert.True(live.Acquired);

        var stolen = await ((ILongRunningOperationClassifiedRecoveryLeaseAcquisition)store).TryAcquireClassifiedRecoveryLeaseAsync(
            new(stale.Id, stale.Kind, stale.CheckpointVersion, stale.Revision), "recovery", now.AddMinutes(2), now.AddMinutes(4), default);

        Assert.False(stolen.Acquired);

        var clock = new RetroDownfall.Arcanum.Tests.Support.FakeTimeProvider();

        clock.SetUtcNow(now.AddMinutes(5));

        var ownership = new LongRunningOperationOwnership();

        Assert.True(ownership.TryClaim(operation.Id, out var claim));

        var connections = new ConnectionSource((SqliteConnection)h.Connection);

        var evidence = new MemoryEvaluationEvidenceStore(connections);

        var handler = new MemoryEvaluationRecoveryHandler(store, evidence, connections, new TurnRunWriter(h.Context), new BudgetReservationService(h.Context, h.Options), clock);

        var reconciler = new LongRunningOperationReconciler(store, [handler], clock, NullLogger<LongRunningOperationReconciler>.Instance, ownership);

        await reconciler.ReconcileNowAsync("recovery");

        Assert.Equal("live", (await store.GetAsync(operation.Id))!.LeaseOwner);

        Assert.True(ownership.Release(operation.Id, claim));

        await reconciler.ReconcileNowAsync("recovery");

        Assert.Equal(LongRunningOperationState.Abandoned, (await store.GetAsync(operation.Id))!.State);
    }

    [SkippableFact]
    public async Task Recovery_reconciles_only_measured_exact_arm_bills_and_keeps_unknown_estimates()
    {
        await using var h = await SagaStoreHarness.CreateAsync();

        h.Options.CurrentValue.Cost.Budget = new() { Enabled = true, DailyLimitUsd = 100m };

        var store = new LongRunningOperationStore(h.Context, TestOrdinaryConnectionFactory.For(h.Context));

        var r = Registration([Source()]);

        var operation = (await store.ResolveOrCreateAsync(new("memory-evaluation", LongRunningOperationRecoveryPolicy.AbandonSafely, "Memory evaluation", DateTimeOffset.UtcNow), new(r.EvaluationId, r.RequestDigest, r.PlanDigest))).Operation!;

        var connections = new ConnectionSource((SqliteConnection)h.Connection);

        var evidence = new MemoryEvaluationEvidenceStore(connections);

        var runs = new TurnRunWriter(h.Context);

        var reservations = new BudgetReservationService(h.Context, h.Options);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var known = await runs.StartRunAsync(new($"{r.EvaluationId:N}:5", null, "memory-evaluation", "counterfactual-arm", null, now));

        var unknown = await runs.StartRunAsync(new($"{r.EvaluationId:N}:0", null, "memory-evaluation", "counterfactual-arm", null, now));

        var unrelated = await runs.StartRunAsync(new($"{r.EvaluationId:N}:6", null, "memory-evaluation", "counterfactual-arm", null, now));

        foreach (var id in new[] { known, unknown, unrelated })
        {
            var reserved = (await reservations.ReserveAsync(new(id, 3m, now.AddMinutes(1), now.ToString("yyyy-MM-dd")))).Value;

            await reservations.MarkPossiblyDispatchedAsync(reserved.Id, id);
        }

        await runs.RecordBillableOperationAsync(new(known, BillableOperationType.Chat, "provider", "model", "counterfactual-arm", now, now, 10, 2, 0, 0, "{}", 2m, BillableOperationStatus.Completed, null));

        await runs.CompleteRunAsync(known, InferenceRunStatus.Completed);

        var checkpoint = new MemoryEvaluationCheckpoint(r.EvaluationId, operation.Id, r.RequestDigest, r.PlanDigest, MemoryEvaluationPhase.Sending, 3, 0, Report(r, [Source()]).Aggregate, 0, 0, 0, 0);

        operation = operation with { CheckpointVersion = 1, CheckpointPayload = MemoryEvaluationCheckpointCodec.Encode(checkpoint) };

        var handler = new MemoryEvaluationRecoveryHandler(store, evidence, connections, runs, reservations, TimeProvider.System);

        Assert.Equal(LongRunningOperationState.Abandoned, (await handler.RecoverAsync(operation, default)).State);

        Assert.Equal(LongRunningOperationState.Abandoned, (await handler.RecoverAsync(operation, default)).State);

        await using var command = h.Connection.CreateCommand();

        command.CommandText = "SELECT Status, ReconciledUsd FROM BudgetReservations WHERE RunId=$run";

        var parameter = command.CreateParameter(); parameter.ParameterName = "$run"; command.Parameters.Add(parameter);

        foreach (var id in new[] { known, unknown, unrelated })
        {
            parameter.Value = id.ToString("N");

            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            Assert.Equal(id == known ? 1 : 4, reader.GetInt32(0));

            Assert.Equal(id == known ? "2" : "0", reader.GetString(1));
        }

        Assert.True((await evidence.GetAsync(r.EvaluationId, default)).IsFailure);

        command.CommandText = "SELECT Status FROM InferenceRuns WHERE Id=$run";

        parameter.Value = known.ToString("N");

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        parameter.Value = unknown.ToString("N");

        Assert.Equal(3L, await command.ExecuteScalarAsync());

        parameter.Value = unrelated.ToString("N");

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public void Checkpoint_refuses_duplicate_members_and_out_of_bounds_progress()
    {
        var r = Registration([Source()]);

        var checkpoint = new MemoryEvaluationCheckpoint(r.EvaluationId, r.OperationId, r.RequestDigest, r.PlanDigest, MemoryEvaluationPhase.Prepared, 3, 0, Report(r, [Source()]).Aggregate, null, null, null, 0);

        string json = System.Text.Encoding.UTF8.GetString(MemoryEvaluationCheckpointCodec.Encode(checkpoint));

        Assert.Null(MemoryEvaluationCheckpointCodec.Decode(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"pairCount\":3", "\"pairCount\":3,\"pairCount\":3"))));

        foreach (int count in new[] { -1, 0, 2, 17, int.MaxValue })
            Assert.False(MemoryEvaluationCheckpointCodec.Valid(checkpoint with { PairCount = count }));

        Assert.False(MemoryEvaluationCheckpointCodec.Valid(checkpoint with { PhysicalSends = 7 }));

        Assert.False(MemoryEvaluationCheckpointCodec.Valid(checkpoint with { PhysicalSends = -1 }));

        Assert.True(MemoryEvaluationCheckpointCodec.Valid(checkpoint with { Aggregate = checkpoint.Aggregate with { Checks = default } }));

        Assert.False(MemoryEvaluationCheckpointCodec.Valid(checkpoint with
        {
            PhysicalSends = 2,
            Aggregate = checkpoint.Aggregate with { Checks = default, CompletedPairs = 1, ValidPairs = 1, EquivalentPairs = 1 }
        }));
    }

    private sealed class ConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken ct) => ValueTask.FromResult(connection);

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken ct) => ValueTask.FromResult(connection);
    }
}
