using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("uncertain")]
    [InlineData("legacy-tool")]
    [InlineData("throw")]
    [InlineData("refused")]
    [InlineData("context-refused")]
    [InlineData("wait")]
    [InlineData("continue-port")]
    [InlineData("heartbeat")]
    [InlineData("cancel")]
    [InlineData("cancel-port")]
    public async Task Local_port_commits_once_or_retains_uncertain_work_without_replay(string outcome)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(), TimeProvider.System, new PortAdmission(), f.Readiness);

        Result<GuildQuestStored> issued = await store.IssueAsync(caller,
            new(f.Generation, null, caller.InvocationId, caller.InvocationId, f.Request.IdempotencyKey), f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        LongRunningOperation operation = (await operations.GetAsync(quest.OperationId))!;

        GuildLocalExecutionBinding binding;

        await using (SqliteTransaction transaction = connection.Connection.BeginTransaction())
        {
            var found = await GuildLocalExecutionStore.ReadWithinAsync(connection.Connection, transaction, quest.Id, default);

            Assert.True(found.IsSuccess, found.Error.Message);

            binding = found.Value;
        }

        DeterministicLocalEngine engine = new() { Outcome = outcome == "continue-port" ? "wait" : outcome == "cancel-port" ? "cancel" : outcome };

        PortClock clock = new();

        GuildLocalPort port = new(store, f.Policy, f.Authority, operations, engine, new UnusedInference(),
            new ReadyPortHost(), new(), clock);

        GuildDispatchRequest request = new(new(f.Request.IdempotencyKey, issued.Value.IntentDigest), quest.Id,
            quest.OperationId, quest.RunId, quest.Id, operation.Revision, binding.Input.Digest);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(outcome is "heartbeat" or "cancel" or "cancel-port" ? 20 : 10));

        Task<Result<GuildDispatchObservation>> dispatchTask = port.DispatchAsync(execution.Value, request, deadline.Token);

        if (outcome is "wait" or "continue-port")
        {
            GuildQuestStored waiting = issued.Value;

            while (!dispatchTask.IsCompleted && waiting.Quest.State != QuestState.InputRequired)
            {
                await Task.Delay(20, deadline.Token);

                waiting = (await store.GetAsync(caller, quest.Id, deadline.Token)).Value;
            }

            Assert.Equal(QuestState.InputRequired, waiting.Quest.State);

            await using SqliteCommand question = connection.Connection.CreateCommand();

            question.CommandText = "SELECT ContinuationId FROM guild_quests WHERE Id=$quest";

            question.Parameters.AddWithValue("$quest", quest.Id.ToString("D").ToUpperInvariant());

            Guid continuation = Guid.Parse((string)(await question.ExecuteScalarAsync(deadline.Token))!);

            GuildContinueRequest answer = new(Guid.NewGuid(), waiting.Quest.Revision, continuation, "text/plain", "Use the first option");

            var accepted = await store.AcceptContinuationAsync(caller, quest.Id, answer, deadline.Token);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            if (outcome == "continue-port")
            {
                var content = await store.GetContinuationAsync(caller, quest.Id, continuation, deadline.Token);

                Assert.True(content.IsSuccess, content.Error.Message);

                await using SqliteCommand digest = connection.Connection.CreateCommand();

                digest.CommandText = "SELECT Digest FROM guild_continuations WHERE ContinuationId=$continuation";

                digest.Parameters.AddWithValue("$continuation", continuation.ToString("D").ToUpperInvariant());

                GuildDispatchRequest continued = request with
                {
                    DispatchId = continuation,
                    Mutation = new(answer.IdempotencyKey, Convert.ToHexString((byte[])(await digest.ExecuteScalarAsync(deadline.Token))!)),
                    InvocationDigest = content.Value.ContentRef.Digest,
                    ExpectedOperationRevision = (await operations.GetAsync(quest.OperationId, deadline.Token))!.Revision,
                };

                Assert.True((await port.ContinueAsync(execution.Value, continued,
                    answer with { Body = "Different accepted answer" }, deadline.Token)).IsFailure);

                var resumed = await port.ContinueAsync(execution.Value, continued, answer, deadline.Token);

                Assert.True(resumed.IsSuccess, resumed.Error.Message);

                Assert.True((await port.ContinueAsync(execution.Value, continued, answer, deadline.Token)).IsSuccess);

                Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_continuations"));
            }
        }

        if (outcome is "cancel" or "cancel-port")
        {
            await engine.Entered.Task.WaitAsync(deadline.Token);

            GuildQuestStored current = (await store.GetAsync(caller, quest.Id, deadline.Token)).Value;

            GuildCancelQuestRequest cancel = new(Guid.NewGuid(), current.Quest.Revision, GuildCancellationReason.OperatorRequested);

            GuildMutationIdentity mutation = new(cancel.IdempotencyKey, GuildQuestIntent.Compute(quest.Id, cancel).Value);

            var canceled = await store.RequestCancellationAsync(caller, mutation, quest.Id, cancel, deadline.Token);

            Assert.True(canceled.IsSuccess, canceled.Error.Message);

            if (outcome == "cancel-port")
            {
                Assert.True((await port.RequestCancellationAsync(execution.Value,
                    new(Guid.NewGuid(), mutation.CanonicalRequestDigest), quest.Id, quest.Id, deadline.Token)).IsFailure);

                var stopping = await port.RequestCancellationAsync(execution.Value, mutation, quest.Id, quest.Id, deadline.Token);

                Assert.True(stopping.IsSuccess, stopping.Error.Message);

                Assert.True((await port.RequestCancellationAsync(execution.Value, mutation, quest.Id, quest.Id, deadline.Token)).IsSuccess);

                Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations"));
            }
        }

        if (outcome == "heartbeat")
        {
            try
            {
                await engine.Entered.Task.WaitAsync(deadline.Token);

                long revision = (await operations.GetAsync(quest.OperationId))!.Revision;

                clock.Advance(TimeSpan.FromSeconds(90));

                LongRunningOperation current = (await operations.GetAsync(quest.OperationId))!;

                while (current.Revision == revision && !dispatchTask.IsCompleted)
                {
                    await Task.Delay(25, deadline.Token);

                    current = (await operations.GetAsync(quest.OperationId))!;
                }

                Assert.True(current.Revision > revision);

                Assert.True(current.LeaseExpiresAt >= clock.GetUtcNow().AddSeconds(100));
            }
            finally
            {
                engine.Release.TrySetResult();
            }
        }

        Result<GuildDispatchObservation> dispatched = await dispatchTask;

        if (outcome is "uncertain" or "throw" or "legacy-tool")
        {
            Assert.True(dispatched.IsSuccess, dispatched.Error.Message);

            Assert.True(dispatched.Value.ReconciliationRequired);

            Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await operations.GetAsync(quest.OperationId))!.State);

            if (outcome == "legacy-tool")
                Assert.Equal(ErrorCodes.Guild.LegacyToolName, (await operations.GetAsync(quest.OperationId))!.TerminalErrorCode);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

            var retried = await port.DispatchAsync(execution.Value, request, default);

            Assert.True(retried.IsSuccess, retried.Error.Message);

            Assert.True(retried.Value.ReconciliationRequired);

            Assert.Equal(1, engine.Calls);

            return;
        }

        Assert.True(dispatched.IsSuccess, dispatched.Error.Message);

        QuestState expected = outcome is "cancel" or "cancel-port" ? QuestState.Canceled
            : outcome is "refused" or "context-refused" ? QuestState.Failed : QuestState.Completed;

        Assert.Equal(expected, dispatched.Value.State);

        if (outcome is "refused" or "context-refused")
        {
            Assert.False(dispatched.Value.ReconciliationRequired);

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts WHERE Closed=1"));

            var retained = await store.ReadLocalResultAsync(caller, quest.Id, false, default);

            Assert.True(retained.IsSuccess, retained.Error.Message);

            Assert.Equal(outcome == "context-refused" ? ErrorCodes.Hub.ContextBudgetExceeded : ErrorCodes.Guild.Unavailable,
                retained.Value.Receipt!.ReasonCode);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT NoDispatchProven FROM guild_result_receipts"));
        }

        Assert.Equal(binding.ApprenticeId, dispatched.Value.LocalChildId);

        Assert.Equal(1, engine.Calls);

        Assert.Equal(quest.SessionId, engine.Binding!.SessionId);

        Assert.Equal(quest.RunId, engine.Binding.RunId);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM Apprentices"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

        Result<GuildDispatchObservation> replay = await port.DispatchAsync(execution.Value, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(expected, replay.Value.State);

        Assert.Equal(1, engine.Calls);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_local_executions"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        Result<GuildDispatchObservation> observed = await port.ObserveAsync(execution.Value, quest.Id, quest.Id, default);

        Assert.True(observed.IsSuccess, observed.Error.Message);

        Assert.Equal(expected, observed.Value.State);

        Assert.Equal(1, engine.Calls);
    }

    private sealed class PortClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        internal void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
    }

    private sealed class PortAdmission : IGuildLocalExecutionAdmission
    {
        public Task<Result<GuildLocalExecutionInput>> ResolveWithinAsync(GuildCallerContext caller, GuildQuestStored quest,
            SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
            Task.FromResult(Result<GuildLocalExecutionInput>.Success(new(1, GuildLocalExecutionMode.Sterile,
                quest.Quest.GoalRef, "provider", "model", [], null)));
    }

    private sealed class ReadyPortHost : IGuildLocalQuestScheduler
    {
        public bool IsAvailable => true;

        public void Schedule(GuildQuestDto quest) { }
    }

    private sealed class UnusedInference : IGuildLocalInferenceRuntime
    {
        public Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The deterministic engine has no provider effect.");

        public Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
            GuildLocalInferenceTurn turn, CancellationToken cancellationToken) => throw new InvalidOperationException("The deterministic engine has no provider effect.");
    }

    private sealed class DeterministicLocalEngine : IGuildLocalExecutionEngine
    {
        internal int Calls;

        internal string Outcome = "success";

        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal GuildLocalExecutionBinding? Binding;

        public async Task<Result<PromptTurnResult>> ExecuteGuildAsync(GuildLocalExecutionBinding binding, GuildLocalExecutionInput input,
            GuildLocalInferenceExecution execution, IGuildLocalInferenceRuntime runtime, IGuildLocalEngineWait waitForInput,
            CancellationToken cancellationToken)
        {
            Calls++;

            Binding = binding;

            Assert.Equal(binding.QuestId, execution.Driver.Execution.QuestId);

            Assert.Equal(binding.RunId, execution.Driver.Execution.RunId);

            Assert.Equal(binding.ApprenticeId, execution.Dispatch.LocalChildId);

            if (Outcome == "cancel")
            {
                Entered.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new Error(ErrorCodes.Guild.ReconciliationRequired, "The awaited engine stopped without sending a physical call.");
                }
            }

            if (Outcome == "heartbeat")
            {
                Entered.TrySetResult();

                await Release.Task.WaitAsync(cancellationToken);
            }

            if (Outcome == "throw") throw new InvalidOperationException("An engine fault must preserve durable work for inspection.");

            if (Outcome == "legacy-tool") return new Error(ErrorCodes.Guild.LegacyToolName,
                "read_file_chunk → read_file; start a new Quest with a corrected plan.");

            if (Outcome == "uncertain") return new Error(ErrorCodes.Guild.ReconciliationRequired, "No terminal execution evidence is available.");

            if (Outcome == "refused") return new Error(ErrorCodes.Guild.Unavailable, "The configured provider cannot execute this request.");

            if (Outcome == "context-refused") return new Error(ErrorCodes.Hub.ContextBudgetExceeded,
                "The selected output and input exceed the configured context before physical admission.");

            if (Outcome == "wait")
            {
                GuildLocalWaitSnapshot snapshot = new(1, binding.Input.Digest, 0,
                    [new() { Index = 0, Description = "Choose an option", Attempts = 1 }],
                    new() { CurrentStep = 0, ParentApprenticeId = binding.ParentApprenticeId, LaunchRequested = true, EscalationReason = "Which option?" });

                var continued = await waitForInput.WaitAsync(execution, new(execution.Dispatch.DispatchId, snapshot), cancellationToken);

                if (continued.IsFailure) return continued.Error;

                Assert.Equal("Use the first option", continued.Value.Answer);

                Assert.True(continued.Value.Execution.Dispatch.IsContinuation);
            }

            return new PromptTurnResult("Completed without an external effect.", null);
        }
    }
}
