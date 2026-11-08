using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Local_port_cancellation_finishes_committed_pending_work_while_feature_is_off_without_engine_entry()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(), TimeProvider.System, new PortAdmission(), f.Readiness);

        var issued = await store.IssueAsync(caller, new(f.Generation, null, caller.InvocationId, caller.InvocationId,
            f.Request.IdempotencyKey), f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var current = await store.GetAsync(caller, quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        GuildCancelQuestRequest request = new(Guid.NewGuid(), current.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

        GuildMutationIdentity mutation = new(request.IdempotencyKey, GuildQuestIntent.Compute(quest.Id, request).Value);

        f.Settings.CurrentValue.Features.Guild = false;

        Assert.True((await store.RequestCancellationAsync(caller, mutation, quest.Id, request, default)).IsSuccess);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        DeterministicLocalEngine engine = new();

        GuildLocalPort port = new(store, f.Policy, f.Authority, operations, engine, new UnusedInference(),
            new ReadyPortHost(), new(), TimeProvider.System);

        var canceled = await port.RequestCancellationAsync(execution.Value, mutation, quest.Id, quest.Id, default);

        Assert.True(canceled.IsSuccess, canceled.Error.Message);

        Assert.Equal(QuestState.Canceled, canceled.Value.State);

        Assert.Equal(0, engine.Calls);

        Assert.Equal(1L, await f.ScalarAsync("SELECT Closed FROM guild_budget_accounts"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT NoDispatchProven FROM guild_result_receipts"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT WorkStoppedProven FROM guild_result_receipts"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM Apprentices"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        Assert.True((await port.RequestCancellationAsync(execution.Value, mutation, quest.Id, quest.Id, default)).IsSuccess);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));
    }
}
