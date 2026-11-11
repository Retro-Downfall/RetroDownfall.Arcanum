using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Cohort_creation_allocates_one_envelope_and_replays_without_a_second_account()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner, f.Request with
        {
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] },
        });

        Assert.True(root.IsSuccess, root.Error.Message);

        var q = root.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(owner, q.Id, q.OperationId, q.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var caller = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(caller.IsSuccess, caller.Error.Message);

        GuildCohortStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store, new(), f.Policy, TimeProvider.System);

        CreateCohortRequest request = new(Guid.NewGuid(), q.Id, "Panel", new(GuildConvergenceKind.All, null), new("text/plain"), null);

        var created = await store.CreateAsync(caller.Value, request, default);

        Assert.True(created.IsSuccess, created.Error.Message);

        var replay = await store.CreateAsync(caller.Value, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        var read = await store.GetAsync(caller.Value, created.Value.Id, default);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal(created.Value.Id, read.Value.Id);

        var emptyClose = await store.CloseAsync(caller.Value, created.Value.Id, new(Guid.NewGuid(), created.Value.Revision), default);

        Assert.True(emptyClose.IsFailure);

        var stillOpen = await store.GetAsync(caller.Value, created.Value.Id, default);

        Assert.True(stillOpen.IsSuccess, stillOpen.Error.Message);

        Assert.Equal(GuildCohortState.Open, stillOpen.Value.State);

        Assert.Equal(created.Value.Id, replay.Value.Id);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohorts"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts WHERE OwnerKindCode=1"));
    }
}
