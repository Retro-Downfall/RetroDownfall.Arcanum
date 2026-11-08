using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, true, false, true, true)]
    public async Task Local_port_paid_completion_closes_account_and_preserves_unknown_price(bool unpriced, bool childPending, bool refuseAfterPhysical, bool paidChildPending, bool childRequiresReconciliation)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        BudgetReservationService daily = new(db, f.Settings);

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(daily), TimeProvider.System, new PortAdmission(), f.Readiness);

        var issued = await store.IssueAsync(caller, new(f.Generation, null, caller.InvocationId, caller.InvocationId,
            f.Request.IdempotencyKey), f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        LongRunningOperationStore operations = new(db, f.Connections);

        GuildLocalExecutionBinding binding;

        await using (SqliteTransaction transaction = connection.Connection.BeginTransaction())
            binding = (await GuildLocalExecutionStore.ReadWithinAsync(connection.Connection, transaction, quest.Id, default)).Value;

        GuildQuestStored? child = null;

        if (childPending)
        {
            Guid key = Guid.NewGuid();

            var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, key, default);

            Assert.True(run.IsSuccess, run.Error.Message);

            var current = await store.GetAsync(caller, quest.Id, default);

            Assert.True(current.IsSuccess, current.Error.Message);

            var created = await store.IssueAsync(run.Value, new(f.Generation, quest.Id, null, key, key), quest.Id,
                new(key, current.Value.Quest.Revision, null, "Async child", "Compute a contribution", null), default);

            Assert.True(created.IsSuccess, created.Error.Message);

            child = created.Value;
        }

        PortClock clock = new();

        PaidPortEngine engine = new(daily, unpriced) { RefuseAfterPhysical = refuseAfterPhysical };

        GuildLocalPort port = new(store, f.Policy, f.Authority, operations, engine, new UnusedInference(),
            new ReadyPortHost(), new(), clock);

        GuildDispatchRequest request = new(new(f.Request.IdempotencyKey, issued.Value.IntentDigest), quest.Id,
            quest.OperationId, quest.RunId, quest.Id, (await operations.GetAsync(quest.OperationId))!.Revision, binding.Input.Digest);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        Task<Result<GuildDispatchObservation>> running = port.DispatchAsync(execution.Value, request, deadline.Token);

        Task<Result<GuildDispatchObservation>>? childRunning = null;

        PaidPortEngine? childEngine = paidChildPending ? new(daily, false) { PauseAfterExposure = true, RefuseAfterPhysical = childRequiresReconciliation } : null;

        Result<GuildDispatchObservation> completed;

        try
        {
            if (child is not null)
            {
                await engine.Settled.Task.WaitAsync(deadline.Token);

                await Task.Delay(100, deadline.Token);

                Assert.False(running.IsCompleted);

                if (childEngine is null)
                {
                    long pendingRevision = (await operations.GetAsync(quest.OperationId, deadline.Token))!.Revision;

                    clock.Advance(TimeSpan.FromSeconds(90));

                    while ((await operations.GetAsync(quest.OperationId, deadline.Token))!.Revision == pendingRevision)
                        await Task.Delay(25, deadline.Token);
                }

                var childRecovery = await f.Policy.CreateRecoveryAsync(caller, child.Quest.Id, child.Quest.OperationId,
                    child.Quest.SignetRevision, deadline.Token);

                Assert.True(childRecovery.IsSuccess, childRecovery.Error.Message);

                var childExecution = await f.Authority.RebindAsync(childRecovery.Value, deadline.Token);

                Assert.True(childExecution.IsSuccess, childExecution.Error.Message);

                var pending = await store.ReadLocalPendingAsync(caller, child.Quest.Id, deadline.Token);

                Assert.True(pending.IsSuccess, pending.Error.Message);

                Assert.NotNull(pending.Value.Request);

                IGuildLocalExecutionEngine childExecutionEngine = childEngine is null ? new DeterministicLocalEngine() : childEngine;

                GuildLocalPort childPort = new(store, f.Policy, f.Authority, operations, childExecutionEngine,
                    new UnusedInference(), new ReadyPortHost(), new(), TimeProvider.System);

                childRunning = childPort.DispatchAsync(childExecution.Value, pending.Value.Request!, deadline.Token);

                if (childEngine is not null)
                {
                    await childEngine.Exposed.Task.WaitAsync(deadline.Token);

                    Assert.False(running.IsCompleted);

                    Assert.False(childRunning.IsCompleted);

                    Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

                    Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_budget_effects WHERE QuestId='{child.Quest.Id.ToString("D").ToUpperInvariant()}' AND StateCode=1"));

                    Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

                    long revision = (await operations.GetAsync(quest.OperationId, deadline.Token))!.Revision;

                    clock.Advance(TimeSpan.FromSeconds(90));

                    while ((await operations.GetAsync(quest.OperationId, deadline.Token))!.Revision == revision)
                        await Task.Delay(25, deadline.Token);

                    Assert.False(running.IsCompleted);

                    Assert.False(childRunning.IsCompleted);

                    Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

                    childEngine.Release.TrySetResult();
                }

                var childResult = await childRunning;

                Assert.True(childResult.IsSuccess, childResult.Error.Message);

                if (childRequiresReconciliation)
                {
                    Assert.True(childResult.Value.ReconciliationRequired);

                    Assert.Equal(ErrorCodes.Guild.LocalExecutionInterrupted, childResult.Value.ReasonCode);

                    Assert.Equal(LongRunningOperationState.ReconciliationRequired,
                        (await operations.GetAsync(child.Quest.OperationId, deadline.Token))!.State);
                }
                else
                {
                    Assert.Equal(QuestState.Completed, childResult.Value.State);
                }
            }

            // A paid parent must propagate an interrupted child's reconciliation frontier instead of renewing forever.
            // This bound observes completion; the outer finally cancels and joins both real dispatch owners on RED too.
            completed = childRequiresReconciliation
                ? await running.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token)
                : await running;
        }
        finally
        {
            childEngine?.Release.TrySetResult();

            try
            {
                await deadline.CancelAsync();
            }
            finally
            {
                if (childRunning is null)
                    _ = await running;
                else
                    _ = await Task.WhenAll(running, childRunning);
            }
        }

        Assert.True(completed.IsSuccess, completed.Error.Message);

        if (childRequiresReconciliation)
        {
            Assert.True(completed.Value.ReconciliationRequired);

            Assert.Equal(ErrorCodes.Guild.LocalExecutionInterrupted, completed.Value.ReasonCode);

            Assert.Equal(LongRunningOperationState.ReconciliationRequired,
                (await operations.GetAsync(quest.OperationId, default))!.State);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts WHERE Closed=1"));

            Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

            var replay = await port.DispatchAsync(execution.Value, request, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.True(replay.Value.ReconciliationRequired);

            Assert.Equal(1, engine.Calls);

            Assert.Equal(1, childEngine!.Calls);

            Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

            return;
        }

        if (refuseAfterPhysical)
        {
            Assert.True(completed.Value.ReconciliationRequired);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT Closed FROM guild_budget_accounts"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

            Assert.True((await port.DispatchAsync(execution.Value, request, default)).IsSuccess);

            Assert.Equal(1, engine.Calls);

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

            return;
        }

        Assert.Equal(QuestState.Completed, completed.Value.State);

        Assert.Equal(1, engine.Calls);

        Assert.Equal(1L, await f.ScalarAsync($"SELECT Closed FROM guild_budget_accounts WHERE OwnerKindCode=0 AND OwnerId='{quest.Id.ToString("D").ToUpperInvariant()}'"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT WorkStoppedProven FROM guild_result_receipts WHERE QuestId='{quest.Id.ToString("D").ToUpperInvariant()}'"));

        Assert.Equal(0L, await f.ScalarAsync($"SELECT NoDispatchProven FROM guild_result_receipts WHERE QuestId='{quest.Id.ToString("D").ToUpperInvariant()}'"));

        Assert.Equal(unpriced ? 1L : 0L, await f.ScalarAsync($"SELECT UnknownUsd FROM guild_budget_effects WHERE QuestId='{quest.Id.ToString("D").ToUpperInvariant()}'"));

        Assert.Equal(paidChildPending ? 2L : 1L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        if (childEngine is not null) Assert.Equal(1, childEngine.Calls);

        Assert.True((await port.DispatchAsync(execution.Value, request, default)).IsSuccess);

        Assert.Equal(1, engine.Calls);

        if (childEngine is not null) Assert.Equal(1, childEngine.Calls);

        Assert.Equal(paidChildPending ? 2L : 1L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));
    }

    private sealed class PaidPortEngine(BudgetReservationService daily, bool unpriced) : IGuildLocalExecutionEngine
    {
        internal int Calls;

        internal bool RefuseAfterPhysical;

        internal bool PauseAfterExposure;

        internal TaskCompletionSource Exposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<PromptTurnResult>> ExecuteGuildAsync(GuildLocalExecutionBinding binding,
            GuildLocalExecutionInput input, GuildLocalInferenceExecution execution, IGuildLocalInferenceRuntime runtime,
            IGuildLocalEngineWait waitForInput, CancellationToken cancellationToken)
        {
            Calls++;

            GuildBudgetEffectRequest effect = new(Guid.NewGuid(), binding.QuestId, execution.Dispatch.DispatchId,
                binding.RunId, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, unpriced ? null : 1m, 100);

            var called = await execution.Store.ExecuteProviderEffectAsync(execution, daily, effect,
                async token =>
                {
                    if (PauseAfterExposure)
                    {
                        Exposed.TrySetResult();

                        await Release.Task.WaitAsync(token);
                    }

                    return Result<string>.Success("Paid final answer");
                }, cancellationToken);

            if (called.IsFailure) return called.Error;

            BillableOperationRecord bill = new(binding.RunId, BillableOperationType.Chat, "provider", "model", "guild",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7, 3, 0, 0, "{}", unpriced ? 0m : 0.01m,
                BillableOperationStatus.Completed, null);

            var settled = await execution.Store.SettleProviderEffectAsync(execution, daily, effect, bill, cancellationToken);

            Settled.TrySetResult();

            if (settled.IsFailure) return settled.Error;

            return RefuseAfterPhysical
                ? new Error(ErrorCodes.Guild.Unavailable, "The next unit is unavailable after the physical call.")
                : Result<PromptTurnResult>.Success(new(called.Value, null));
        }
    }
}
