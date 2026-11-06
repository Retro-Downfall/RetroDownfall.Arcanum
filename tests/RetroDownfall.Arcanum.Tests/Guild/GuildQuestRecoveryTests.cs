using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildQuestRecoveryTests
{
    [Fact]
    public async Task Unstarted_quest_is_parked_without_dispatch_or_a_second_lifecycle_write()
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        var handler = Handler(scenario);

        var first = await handler.RecoverAsync(scenario.Operation, CancellationToken.None);

        var repeated = await handler.RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, first.State);

        Assert.Equal(first, repeated);

        Assert.Equal(0L, await scenario.ScalarAsync("SELECT COUNT(*) FROM guild_dispatches"));

        Assert.Equal(7L, await scenario.ScalarAsync("SELECT Revision FROM LongRunningOperations"));

        Assert.Equal(1L, await scenario.ScalarAsync("SELECT Revision FROM guild_quests"));
    }

    [Theory]
    [InlineData(3, LongRunningOperationState.Completed, null)]
    [InlineData(4, LongRunningOperationState.Failed, "Guild.ChildFailed")]
    [InlineData(5, LongRunningOperationState.Completed, "operation.cancelled")]
    [InlineData(6, LongRunningOperationState.Failed, "Guild.Rejected")]
    public async Task Matching_committed_terminal_receipt_returns_exact_ledger_outcome_and_retains_pending_delivery(int outcome, LongRunningOperationState state, string? code)
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.TerminalAsync(outcome, state, code);

        var recovered = await Handler(scenario).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Equal(state, recovered.State);

        Assert.Equal(code, recovered.ErrorCode);

        Assert.Equal(1L, await scenario.ScalarAsync("SELECT DeliveryStateCode FROM guild_result_outbox"));

        Assert.Equal(1L, await scenario.ScalarAsync("SELECT COUNT(*) FROM guild_result_receipts"));
    }

    [Theory]
    [InlineData("missing-quest")]
    [InlineData("run")]
    [InlineData("checkpoint")]
    [InlineData("stolen-lease")]
    [InlineData("expired-lease")]
    [InlineData("stale-revision")]
    public async Task Broken_linkage_or_lost_current_lease_never_recovers_as_success(string mismatch)
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        switch (mismatch)
        {
            case "missing-quest":
                await scenario.ExecuteAsync("DELETE FROM guild_quests");
                break;
            case "run":
                await scenario.ExecuteAsync("UPDATE LongRunningOperations SET RunId='FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF'");
                scenario.Operation = scenario.Operation with { RunId = Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF") };
                break;
            case "checkpoint":
                scenario.Operation = scenario.Operation with { CheckpointPayload = GuildQuestCheckpoint.Encode(Guid.NewGuid()) };
                await scenario.SetPayloadAsync(scenario.Operation.CheckpointPayload!);
                break;
            case "stolen-lease":
                await scenario.ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner='other'");
                break;
            case "expired-lease":
                scenario.Time.Advance(TimeSpan.FromMinutes(6));
                break;
            case "stale-revision":
                await scenario.ExecuteAsync("UPDATE LongRunningOperations SET Revision=8");
                break;
        }

        var recovered = await Handler(scenario).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);

        Assert.Equal(0L, await scenario.ScalarAsync("SELECT COUNT(*) FROM guild_dispatches"));
    }

    [Fact]
    public async Task A_terminal_receipt_with_a_nonterminal_ledger_is_an_atomicity_contradiction()
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.TerminalAsync(3, LongRunningOperationState.Completed, null);

        await scenario.ExecuteAsync("UPDATE LongRunningOperations SET State=1,LeaseOwner='recovery',LeaseExpiresAt='2026-11-06T12:05:00.0000000Z'");

        scenario.Operation = scenario.Operation with { State = LongRunningOperationState.Running, LeaseOwner = "recovery", LeaseExpiresAt = Now.AddMinutes(5) };

        var recovered = await Handler(scenario).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Dispatch_evidence_without_a_qualified_observer_never_authorizes_a_replacement_launch(int evidence)
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.DispatchAsync(evidence);

        var recovered = await Handler(scenario).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);

        Assert.Equal(1L, await scenario.ScalarAsync("SELECT COUNT(*) FROM guild_dispatches"));

        Assert.Equal(7L, await scenario.ScalarAsync("SELECT Revision FROM LongRunningOperations"));
    }

    [Fact]
    public async Task Awaiting_input_preserves_question_continuation_and_the_original_quest()
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.ExecuteAsync($"UPDATE guild_quests SET LastNonterminalStateCode=2,WaitReasonCode=0,QuestionId='{ResultId}',ContinuationId='{DispatchId}',Revision=2");

        await scenario.ExecuteAsync("UPDATE LongRunningOperations SET State=2");

        scenario.Operation = scenario.Operation with { State = LongRunningOperationState.Waiting };

        var recovered = await Handler(scenario).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);

        Assert.Equal(ResultId, await scenario.ScalarAsync("SELECT QuestionId FROM guild_quests"));

        Assert.Equal(DispatchId, await scenario.ScalarAsync("SELECT ContinuationId FROM guild_quests"));

        Assert.Equal(2L, await scenario.ScalarAsync("SELECT State FROM LongRunningOperations"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qualified_observer_reattaches_only_the_same_known_local_child_or_remote_task(bool remote)
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        Guid child = Guid.Parse("12345678-1234-1234-1234-123456789ABC");

        await scenario.DispatchAsync(3, remote ? null : child, remote ? "peer-task-17" : null);

        RecordingObserver observer = new();

        var handler = Handler(scenario, observer);

        var first = await handler.RecoverAsync(scenario.Operation, CancellationToken.None);

        var repeated = await handler.RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, first.State);

        Assert.Equal(first, repeated);

        Assert.Equal(2, observer.Targets.Count);

        foreach (GuildQuestRecoveryTarget target in observer.Targets)
        {
            Assert.Equal(Guid.Parse(QuestId), target.QuestId);

            Assert.Equal(Guid.Parse(OperationId), target.OperationId);

            Assert.Equal(Guid.Parse(RunId), target.RunId);

            Assert.Equal(Guid.Parse(DispatchId), target.DispatchId);

            Assert.Equal(remote ? (Guid?)null : child, target.LocalChildId);

            Assert.Equal(remote ? "peer-task-17" : null, target.RemoteTaskId);

            Assert.Equal(7, target.OperationRevision);
        }

        Assert.Equal(1L, await scenario.ScalarAsync("SELECT COUNT(*) FROM guild_dispatches"));

        Assert.Equal(0L, await scenario.ScalarAsync("SELECT COUNT(*) FROM guild_result_receipts"));

        Assert.Equal(7L, await scenario.ScalarAsync("SELECT Revision FROM LongRunningOperations"));
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("epoch")]
    public async Task A_successful_observer_response_after_a_lease_or_authority_generation_change_cannot_settle_work(string changed)
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.DispatchAsync(2, null, "same-peer-task");

        RecordingObserver observer = new(async _ =>
        {
            await scenario.ExecuteAsync(changed == "lease"
                ? "UPDATE LongRunningOperations SET LeaseOwner='other',Revision=8"
                : "UPDATE guild_signet_authority_state SET Epoch='12345678-1234-1234-1234-123456789ABC'");

            return Result.Success();
        });

        var recovered = await Handler(scenario, observer).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Single(observer.Targets);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);

        Assert.Equal("Guild.ReconciliationRequired", recovered.ErrorCode);

        Assert.Equal(0L, await scenario.ScalarAsync("SELECT COUNT(*) FROM guild_result_receipts"));

        Assert.Equal(1L, await scenario.ScalarAsync("SELECT COUNT(*) FROM guild_dispatches"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Prepared_or_proven_undispatched_work_is_not_sent_to_an_external_observer(int evidence)
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.DispatchAsync(evidence, null, "not-authorized-for-lookup");

        RecordingObserver observer = new();

        _ = await Handler(scenario, observer).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Empty(observer.Targets);
    }

    [Fact]
    public async Task A_pending_external_observation_does_not_hold_the_root_authority_gate_or_a_database_transaction()
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.DispatchAsync(3, null, "same-peer-task");

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        RecordingObserver observer = new(async _ =>
        {
            entered.SetResult();

            await release.Task;

            return Result.Success();
        });

        Task<LongRunningOperationRecoveryResult> recovery = Handler(scenario, observer).RecoverAsync(scenario.Operation, CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            var otherAdmission = await GuildSignetRootGate.RunAsync<bool>(scenario.Gate, Guid.Parse(QuestId), async _ =>
            {
                await scenario.ExecuteAsync("UPDATE guild_quests SET Revision=Revision+1");

                return Result<bool>.Success(true);
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(otherAdmission.IsSuccess);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await recovery).State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Maintenance_joins_the_pending_observer_without_admitting_another_recovery_page(bool callerCancels, bool observerFails)
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.DispatchAsync(3, null, "first-peer-task");

        await scenario.ExecuteAsync($"""
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,LocalChildId,RemoteTaskId,Revision,CreatedAtUtc,UpdatedAtUtc)
            SELECT 'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,LocalChildId,'second-peer-task',Revision,CreatedAtUtc,UpdatedAtUtc
            FROM guild_dispatches WHERE DispatchId='{DispatchId}';
            """);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        RecordingObserver observer = new(async _ =>
        {
            entered.SetResult();

            await release.Task;

            return observerFails
                ? Result.Failure(new(ErrorCodes.Guild.ReconciliationRequired, "The original observation requires reconciliation."))
                : Result.Success();
        });

        using CancellationTokenSource caller = new();

        Task<LongRunningOperationRecoveryResult> recovery = Handler(scenario, observer).RecoverAsync(scenario.Operation, caller.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        int readsBeforeMaintenance = scenario.Connections.Kinds.Count;

        try
        {
            CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
                new CovenantDigest(new byte[32]));

            Result<IGrimoireClosingOwner> begun = scenario.Gate.BeginOrResumeExclusive(owner);

            Assert.True(begun.IsSuccess);

            await using IGrimoireClosingOwner closing = begun.Value;

            Task<Result> drain = scenario.Gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            Assert.False(drain.IsCompleted);

            if (callerCancels)
                caller.Cancel();

            Assert.False(recovery.IsCompleted);

            release.SetResult();

            if (callerCancels)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await recovery);
            else
                Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await recovery).State);

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.Single(observer.Targets);

            Assert.Equal(readsBeforeMaintenance, scenario.Connections.Kinds.Count);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Maintenance_during_observer_qualification_stops_before_another_recovery_page()
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.DispatchAsync(3, null, "same-peer-task");

        IGrimoireClosingOwner? closing = null;

        RecordingObserver observer = new(canObserve: _ =>
        {
            CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
                new CovenantDigest(new byte[32]));

            Result<IGrimoireClosingOwner> begun = scenario.Gate.BeginOrResumeExclusive(owner);

            Assert.True(begun.IsSuccess);

            closing = begun.Value;

            return false;
        });

        LongRunningOperationRecoveryResult recovered = await Handler(scenario, observer).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.NotNull(closing);

        await using IGrimoireClosingOwner heldClosing = closing;

        Assert.Equal(ErrorCodes.Guild.MaintenanceDeferred, recovered.ErrorCode);

        Assert.Single(observer.Qualifications);

        Assert.Empty(observer.Targets);

        Assert.Single(scenario.Connections.Kinds);

        Assert.True((await scenario.Gate.DrainRequestAndWorkAsync(heldClosing, CancellationToken.None)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_reference_must_match_the_original_canonical_quest_locator(bool missing)
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.TerminalAsync(3, LongRunningOperationState.Completed, null);

        string? reference = missing ? null : Guid.NewGuid().ToString("D").ToUpperInvariant();

        await scenario.SetReferenceAsync(reference);

        scenario.Operation = scenario.Operation with { CheckpointReference = reference };

        var recovered = await Handler(scenario).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);
    }

    [Fact]
    public async Task Recovery_capture_cannot_materialize_a_later_corrupt_ledger_payload()
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        var handler = Handler(scenario);

        // Recovery also inspects the installed catalog. Measure that identical fixed work on a
        // healthy row, so this probe isolates allocations caused by the later corrupt cell.
        long healthyBefore = GC.GetAllocatedBytesForCurrentThread();

        Task<LongRunningOperationRecoveryResult> healthy = handler.RecoverAsync(scenario.Operation, CancellationToken.None);

        long baseline = GC.GetAllocatedBytesForCurrentThread() - healthyBefore;

        Assert.True(healthy.IsCompleted, "The allocation probe requires an inline SQLite fixture read.");

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await healthy).State);

        await scenario.ExecuteAsync("UPDATE LongRunningOperations SET CheckpointPayload=zeroblob(8388608)");

        long before = GC.GetAllocatedBytesForCurrentThread();

        Task<LongRunningOperationRecoveryResult> pending = handler.RecoverAsync(scenario.Operation, CancellationToken.None);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(pending.IsCompleted, "The allocation probe requires an inline SQLite fixture read.");

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await pending).State);

        Assert.True(allocated - baseline < 4 * 1024 * 1024,
            $"Corrupt recovery metadata added {allocated - baseline:N0} managed bytes beyond the {baseline:N0}-byte healthy baseline.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Recovery_park_reason_distinguishes_an_intact_offered_question_after_lease_acquisition(bool uncertainDispatch, bool malformedQuestion)
    {
        await using Scenario scenario = await Scenario.CreateAsync();

        await scenario.ExecuteAsync($"UPDATE guild_quests SET LastNonterminalStateCode=2,WaitReasonCode=0,QuestionId='{ResultId}',ContinuationId="
            + (malformedQuestion ? "NULL" : "'" + DispatchId + "'") + ",Revision=2");

        if (uncertainDispatch)
            await scenario.DispatchAsync(2);

        // Acquiring the existing LRO lease promotes Waiting to Running before the handler sees it.
        Assert.Equal(LongRunningOperationState.Running, scenario.Operation.State);

        var recovered = await Handler(scenario).RecoverAsync(scenario.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);

        Assert.Equal(uncertainDispatch || malformedQuestion ? ErrorCodes.Guild.ReconciliationRequired : ErrorCodes.Guild.AwaitingInput, recovered.ErrorCode);
    }

    private sealed class RecordingObserver(Func<GuildQuestRecoveryTarget, Task<Result>>? observe = null,
        Func<GuildQuestRecoveryTarget, bool>? canObserve = null) : IGuildQuestRecoveryObserver
    {
        internal List<GuildQuestRecoveryTarget> Qualifications { get; } = [];

        internal List<GuildQuestRecoveryTarget> Targets { get; } = [];

        public bool CanObserve(GuildQuestRecoveryTarget target)
        {
            Qualifications.Add(target);

            return canObserve?.Invoke(target) ?? true;
        }

        public Task<Result> ObserveAsync(GuildQuestRecoveryTarget target, CancellationToken cancellationToken)
        {
            Targets.Add(target);

            return observe?.Invoke(target) ?? Task.FromResult(Result.Success());
        }
    }

    private static GuildQuestRecoveryHandler Handler(Scenario scenario, params IGuildQuestRecoveryObserver[] observers) =>
        new(scenario.Connections, scenario.Gate, scenario.Time, observers);

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private const string QuestId = "11111111-1111-1111-1111-111111111111";

    private const string OperationId = "22222222222222222222222222222222";

    private const string RunId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";

    private const string ResultId = "EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE";

    private const string DispatchId = "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF";

    private sealed class Scenario(EvolutionScratchDatabase file, SqliteConnection connection) : IAsyncDisposable
    {
        internal FixtureOrdinaryConnectionFactory Connections { get; } = new(file.ConnectionString);

        internal FakeTimeProvider Time { get; } = new();

        internal GrimoireConnectionAdmissionGate Gate { get; } = new(TimeProvider.System);

        internal LongRunningOperation Operation { get; set; } = null!;

        internal static async Task<Scenario> CreateAsync()
        {
            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

            Scenario scenario = new(file, connection);

            scenario.Time.SetUtcNow(Now);

            byte[] checkpoint = GuildQuestCheckpoint.Encode(Guid.Parse(QuestId));

            scenario.Operation = new(Guid.Parse(OperationId), "guild-quest", LongRunningOperationState.Running,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete, null, null, null, Guid.Parse(RunId), null, null, null,
                Now, null, null, null, "recovery", Now.AddMinutes(5), 1, 1, checkpoint, QuestId, "Quest", null, 7);

            await scenario.ExecuteAsync($"""
                INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,RunId,CreatedAt,LeaseOwner,LeaseExpiresAt,AttemptCount,CheckpointVersion,Revision,PublicSummary)
                VALUES ('{OperationId}','guild-quest',1,2,'{RunId}','2026-11-06T12:00:00.0000000Z','recovery','2026-11-06T12:05:00.0000000Z',1,1,7,'Quest');
                INSERT INTO guild_quests(Id,OperationId,InstallationGeneration,IssuerIdentity,IssuerKindCode,IssuingStepId,LogicalCallId,MutationId,
                    IntentDigest,ResolutionDigest,RootQuestId,AgentId,CardId,CardRevision,RunId,SignetId,GoalContentId,GoalRevision,GoalDigest,
                    GoalMimeType,GoalByteLength,AllocationAccountId,CreatedAtUtc,UpdatedAtUtc,IssuedSequence,Revision,LastNonterminalStateCode,CancellationRequested,CancellationConverged,AccountingStateCode)
                VALUES ('{QuestId}','{OperationId}','33333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444444',0,
                    '55555555-5555-5555-5555-555555555555','66666666-6666-6666-6666-666666666666','77777777-7777-7777-7777-777777777777',
                    zeroblob(32),zeroblob(32),'{QuestId}','88888888-8888-8888-8888-888888888888','99999999-9999-9999-9999-999999999999',1,'{RunId}',
                    'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',1,zeroblob(32),'text/plain',0,
                    'DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','2026-11-06T12:00:00.0000000Z','2026-11-06T12:00:00.0000000Z',1,1,1,0,0,2);
                """);

            await scenario.SetPayloadAsync(checkpoint);

            return scenario;
        }

        internal async Task SetPayloadAsync(byte[] payload)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "UPDATE LongRunningOperations SET CheckpointPayload=$payload,CheckpointReference=$reference";

            command.Parameters.AddWithValue("$payload", payload);

            command.Parameters.AddWithValue("$reference", QuestId);

            _ = await command.ExecuteNonQueryAsync();
        }

        internal async Task SetReferenceAsync(string? reference)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "UPDATE LongRunningOperations SET CheckpointReference=$reference";

            command.Parameters.AddWithValue("$reference", (object?)reference ?? DBNull.Value);

            _ = await command.ExecuteNonQueryAsync();
        }

        internal async Task TerminalAsync(int outcome, LongRunningOperationState state, string? code)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"""
                INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,ReasonCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
                VALUES ('{QuestId}','{ResultId}',zeroblob(32),$outcome,$code,1,1,'44444444-4444-4444-4444-444444444444',2,'2026-11-06T12:00:00.0000000Z');
                INSERT INTO guild_result_outbox(QuestId,ResultId,Digest,DeliveryStateCode,Revision,CreatedAtUtc)
                VALUES ('{QuestId}','{ResultId}',zeroblob(32),1,1,'2026-11-06T12:00:00.0000000Z');
                UPDATE LongRunningOperations SET State=$state,TerminalErrorCode=$code,LeaseOwner=NULL,LeaseExpiresAt=NULL;
                """;

            command.Parameters.AddWithValue("$outcome", outcome);

            command.Parameters.AddWithValue("$state", (int)state);

            command.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);

            _ = await command.ExecuteNonQueryAsync();

            Operation = Operation with { State = state, TerminalErrorCode = code, LeaseOwner = null, LeaseExpiresAt = null };
        }

        internal Task DispatchAsync(int evidence, Guid? localChildId = null, string? remoteTaskId = null) => ExecuteAsync($"""
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,LocalChildId,RemoteTaskId,Revision,CreatedAtUtc,UpdatedAtUtc)
            VALUES ('{DispatchId}','{QuestId}',zeroblob(32),0,'44444444-4444-4444-4444-444444444444',{evidence},
                {(localChildId is null ? "NULL" : "'" + localChildId.Value.ToString("D").ToUpperInvariant() + "'")},
                {(remoteTaskId is null ? "NULL" : "'" + remoteTaskId.Replace("'", "''", StringComparison.Ordinal) + "'")},1,'2026-11-06T12:00:00.0000000Z','2026-11-06T12:00:00.0000000Z');
            """);

        internal async Task ExecuteAsync(string sql)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync();
        }

        internal async Task<object?> ScalarAsync(string sql)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();

            file.Dispose();
        }
    }
}
