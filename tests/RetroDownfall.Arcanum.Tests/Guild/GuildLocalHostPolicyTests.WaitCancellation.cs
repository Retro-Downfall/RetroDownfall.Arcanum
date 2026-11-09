using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
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
    [InlineData("waiting", true)]
    [InlineData("parked-wait", true)]
    [InlineData("unpaid-parked-wait", true)]
    [InlineData("accepted-continuation", false)]
    [InlineData("uncertain-effect", false)]
    [InlineData("missing-checkpoint", false)]
    public async Task Persisted_paid_input_wait_cancels_after_restart_only_at_the_exact_stopped_frontier(string frontier, bool canceled)
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

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, quest.Id, default);

        Assert.True(run.IsSuccess, run.Error.Message);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var leased = await operations.TryAcquireLeaseAsync(quest.OperationId, "before-restart", now, now.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(execution.Value, "before-restart", leased.Operation.Revision, run.Value);

        GuildLocalExecutionBinding binding;

        await using (SqliteTransaction transaction = connection.Connection.BeginTransaction())
            binding = (await GuildLocalExecutionStore.ReadWithinAsync(connection.Connection, transaction, quest.Id, default)).Value;

        var dispatch = await store.PrepareDispatchAsync(driver, quest.Id, binding.Input.Digest, quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        GuildLocalInferenceExecution invocation = new(store, driver, dispatch.Value);

        PaidPortEngine paid = new(daily, true);

        bool paidCall = frontier != "unpaid-parked-wait";

        if (paidCall)
        {
            var paidResult = await paid.ExecuteGuildAsync(binding, (await store.ReadLocalInputAsync(driver, default)).Value,
                invocation, new UnusedInference(), null!, default);

            Assert.True(paidResult.IsSuccess, paidResult.Error.Message);
        }

        Assert.True((await store.ResolveLocalDispatchAsync(driver, quest.Id, default)).IsSuccess);

        Guid question = Guid.NewGuid(), continuation = Guid.NewGuid();

        GuildLocalWaitSnapshot snapshot = new(1, binding.Input.Digest, 0,
            [new() { Index = 0, Description = "Choose an option", Attempts = 1 }],
            new() { CurrentStep = 0, ParentApprenticeId = binding.ParentApprenticeId, LaunchRequested = true, EscalationReason = "Which option?" });

        var waiting = await store.WaitForInputAsync(driver, question, continuation, new(quest.Id, snapshot), default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        if (frontier == "accepted-continuation")
        {
            var accepted = await store.AcceptContinuationAsync(caller, quest.Id,
                new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Use the first option"), default);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);
        }

        if (frontier == "uncertain-effect")
        {
            await f.ExecuteAsync("""
                INSERT INTO guild_budget_effects
                SELECT 'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,
                    'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',NULL,UsdBound,TokenBound,'0',0,1,1,1,1
                FROM guild_budget_effects;
                """);
        }

        if (frontier == "missing-checkpoint") await f.ExecuteAsync("DELETE FROM guild_local_wait_checkpoints");

        if (frontier is "parked-wait" or "unpaid-parked-wait")
            await f.ExecuteAsync("UPDATE LongRunningOperations SET State=7,TerminalErrorCode='Guild.AwaitingInput',Revision=Revision+1");

        await f.ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner=NULL,LeaseExpiresAt=NULL,Revision=Revision+1");

        GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

        var restartedCaller = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.True(restartedCaller.IsSuccess, restartedCaller.Error.Message);

        using SignetAuthority restartedAuthority = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

        GuildQuestStore restartedStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority, restartedPolicy,
            new GuildBudgetLedger(daily), TimeProvider.System, new PortAdmission(), f.Readiness);

        var current = await restartedStore.GetAsync(restartedCaller.Value, quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var reboundContext = await restartedPolicy.CreateRecoveryAsync(restartedCaller.Value, quest.Id, quest.OperationId,
            current.Value.Quest.SignetRevision, default);

        Assert.True(reboundContext.IsSuccess, reboundContext.Error.Message);

        var rebound = await restartedAuthority.RebindAsync(reboundContext.Value, default);

        Assert.True(rebound.IsSuccess, rebound.Error.Message);

        current = await restartedStore.GetAsync(restartedCaller.Value, quest.Id, default);

        GuildCancelQuestRequest cancel = new(Guid.NewGuid(), current.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

        GuildMutationIdentity mutation = new(cancel.IdempotencyKey, GuildQuestIntent.Compute(quest.Id, cancel).Value);

        Assert.True((await restartedStore.RequestCancellationAsync(restartedCaller.Value, mutation, quest.Id, cancel, default)).IsSuccess);

        DeterministicLocalEngine engine = new();

        GuildLocalPort port = new(restartedStore, restartedPolicy, restartedAuthority, operations, engine, new UnusedInference(),
            new ReadyPortHost(), new(), TimeProvider.System);

        var outcome = await port.RequestCancellationAsync(rebound.Value, mutation, quest.Id, quest.Id, default);

        Assert.True(outcome.IsSuccess, outcome.Error.Message);

        Assert.Equal(canceled, outcome.Value.State == QuestState.Canceled);

        Assert.Equal(0, engine.Calls);

        Assert.Equal(paidCall ? 1 : 0, paid.Calls);

        Assert.Equal(paidCall ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        Assert.Equal(canceled ? 1L : 0L, await f.ScalarAsync($"SELECT count(*) FROM guild_result_receipts WHERE WorkStoppedProven=1 AND NoDispatchProven={(paidCall ? 0 : 1)}"));

        if (canceled)
        {
            var result = await restartedStore.GetAsync(restartedCaller.Value, quest.Id, default);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.True(result.Value.Quest.CancellationConverged);

            Assert.Equal(paidCall ? GuildAccountingState.Uncertain : GuildAccountingState.Settled, result.Value.Quest.AccountingState);
        }
    }
}
