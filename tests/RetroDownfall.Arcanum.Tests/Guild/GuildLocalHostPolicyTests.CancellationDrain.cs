using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_live_parent_retains_ownership_until_its_child_has_stopped(bool childRunning)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Apprentices = true;

        var caller = await f.OperatorAsync();

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(), TimeProvider.System, new PortAdmission(), f.Readiness);

        var issued = await store.IssueAsync(caller,
            new(f.Generation, null, caller.InvocationId, caller.InvocationId, f.Request.IdempotencyKey), f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        var parent = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, parent.Id, parent.OperationId, parent.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        LongRunningOperationOwnership ownership = new();

        DeterministicLocalEngine engine = new() { Outcome = "heartbeat" };

        GuildLocalPort port = new(store, f.Policy, f.Authority, operations, engine, new UnusedInference(),
            new ReadyPortHost(), ownership, TimeProvider.System);

        var pending = await store.ReadLocalPendingAsync(caller, parent.Id, default);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        var dispatch = port.DispatchAsync(execution.Value, pending.Value.Request!, deadline.Token);

        DeterministicLocalEngine? childEngine = null;

        Task<Result<GuildDispatchObservation>>? childDispatch = null;

        try
        {
            await engine.Entered.Task.WaitAsync(deadline.Token);

            Guid childKey = Guid.NewGuid();

            var issuer = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, childKey, deadline.Token);

            Assert.True(issuer.IsSuccess, issuer.Error.Message);

            var current = await store.GetAsync(caller, parent.Id, deadline.Token);

            var child = await store.IssueAsync(issuer.Value, new(f.Generation, parent.Id, null, childKey, childKey), parent.Id,
                new(childKey, current.Value.Quest.Revision, null, "Child awaiting cancellation", "Stop when parent stops", null), deadline.Token);

            Assert.True(child.IsSuccess, child.Error.Message);

            if (childRunning)
            {
                var recoveryChild = await f.Policy.CreateRecoveryAsync(caller, child.Value.Quest.Id,
                    child.Value.Quest.OperationId, child.Value.Quest.SignetRevision, deadline.Token);

                Assert.True(recoveryChild.IsSuccess, recoveryChild.Error.Message);

                var executionChild = await f.Authority.RebindAsync(recoveryChild.Value, deadline.Token);

                Assert.True(executionChild.IsSuccess, executionChild.Error.Message);

                var pendingChild = await store.ReadLocalPendingAsync(caller, child.Value.Quest.Id, deadline.Token);

                Assert.True(pendingChild.IsSuccess, pendingChild.Error.Message);

                childEngine = new() { Outcome = "heartbeat" };

                GuildLocalPort childPort = new(store, f.Policy, f.Authority, operations, childEngine, new UnusedInference(),
                    new ReadyPortHost(), ownership, TimeProvider.System);

                childDispatch = childPort.DispatchAsync(executionChild.Value, pendingChild.Value.Request!, deadline.Token);

                await childEngine.Entered.Task.WaitAsync(deadline.Token);
            }

            current = await store.GetAsync(caller, parent.Id, deadline.Token);

            GuildCancelQuestRequest cancel = new(Guid.NewGuid(), current.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

            var accepted = await store.RequestCancellationAsync(caller,
                new(cancel.IdempotencyKey, GuildQuestIntent.Compute(parent.Id, cancel).Value), parent.Id, cancel, deadline.Token);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            engine.Release.TrySetResult();

            await Task.Delay(150, deadline.Token);

            Assert.False(dispatch.IsCompleted);

            Assert.True(ownership.IsClaimed(parent.OperationId));

            if (childRunning)
            {
                // No child cancel receipt is inserted by the test: the live child observes only
                // its ancestor frontier and must durably request its own ordinary cancellation.
                childEngine!.Release.TrySetResult();

                var stopped = await childDispatch!.WaitAsync(deadline.Token);

                Assert.True(stopped.IsSuccess, stopped.Error.Message);

                Assert.Equal(QuestState.Canceled, stopped.Value.State);
            }
            else
            {
                var childQuest = child.Value.Quest;

                GuildCancelQuestRequest cancelChild = new(Guid.NewGuid(), childQuest.Revision, GuildCancellationReason.OperatorRequested);

                GuildMutationIdentity childMutation = new(cancelChild.IdempotencyKey, GuildQuestIntent.Compute(childQuest.Id, cancelChild).Value);

                Assert.True((await store.RequestCancellationAsync(caller, childMutation, childQuest.Id, cancelChild, deadline.Token)).IsSuccess);

                var childRecovery = await f.Policy.CreateRecoveryAsync(caller, childQuest.Id, childQuest.OperationId, childQuest.SignetRevision, deadline.Token);

                Assert.True(childRecovery.IsSuccess, childRecovery.Error.Message);

                var childExecution = await f.Authority.RebindAsync(childRecovery.Value, deadline.Token);

                Assert.True(childExecution.IsSuccess, childExecution.Error.Message);

                var stopped = await port.RequestCancellationAsync(childExecution.Value, childMutation, childQuest.Id, childQuest.Id, deadline.Token);

                Assert.True(stopped.IsSuccess, stopped.Error.Message);

                Assert.Equal(QuestState.Canceled, stopped.Value.State);
            }

            var completed = await dispatch.WaitAsync(deadline.Token);

            Assert.True(completed.IsSuccess, completed.Error.Message);

            Assert.Equal(QuestState.Canceled, completed.Value.State);

            Assert.False(ownership.IsClaimed(parent.OperationId));

            Assert.Equal(1, engine.Calls);
        }
        finally
        {
            await deadline.CancelAsync();

            engine.Release.TrySetResult();

            childEngine?.Release.TrySetResult();

            if (childDispatch is not null) _ = await childDispatch;

            _ = await dispatch;
        }
    }
}
