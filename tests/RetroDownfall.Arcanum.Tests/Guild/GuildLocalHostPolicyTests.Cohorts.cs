using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Explicit_cohort_delegation_is_retained_by_real_root_admission()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        GuildIssueRootRequest request = f.Request with
        {
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] },
        };

        Result<RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestStored> accepted = await f.IssueAsync(caller, request);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.Contains(GuildDelegationKind.Cohort, accepted.Value.Quest.ResolvedGrant.Delegation);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));
    }

    [Fact]
    public async Task Cohort_member_uses_one_envelope_funding_path_and_preserves_actual_issuer()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        GuildIssueRootRequest request = f.Request with
        {
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] },
        };

        var root = await f.IssueAsync(caller, request);

        Assert.True(root.IsSuccess, root.Error.Message);

        var recovery = await f.Policy.CreateRecoveryAsync(caller, root.Value.Quest.Id, root.Value.Quest.OperationId,
            root.Value.Quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.Error.Message);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        GuildBudgetGrant ceiling = new(new(GuildBudgetMode.Finite, 3m), new(GuildBudgetMode.Finite, 100));

        var cohort = await cohorts.CreateAsync(run.Value, new(Guid.NewGuid(), root.Value.Quest.Id, "Review",
            new(GuildConvergenceKind.All, null), new("text/plain"), new(Budget: new(true, ceiling))), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        Guid key = Guid.NewGuid();

        GuildIssuanceIdentity identity = new(f.Generation, root.Value.Quest.Id, null, key, key);

        GuildIssueChildRequest childRequest = new(key, root.Value.Quest.Revision, null, "Member", "Review the result", new());

        var child = await f.Store.IssueCohortMemberAsync(run.Value, identity, cohort.Value.Id, cohort.Value.Revision,
            childRequest, default);

        Assert.True(child.IsSuccess, child.Error.Message);

        Assert.Equal(root.Value.Quest.Id, child.Value.Quest.ParentQuestId);

        Assert.Equal(ceiling, child.Value.Quest.ResolvedGrant.Budget);

        Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_budget_accounts WHERE ParentAccountId='{cohort.Value.AllocationAccountId:D}' COLLATE NOCASE AND OwnerKindCode=0"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests WHERE AssociationKindCode=1"));

        var replay = await f.Store.IssueCohortMemberAsync(run.Value, identity, cohort.Value.Id, cohort.Value.Revision,
            childRequest, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(child.Value.Quest.Id, replay.Value.Quest.Id);

        Assert.True(replay.Value.Quest.Receipt!.Replayed);

        var currentCohort = await cohorts.GetAsync(run.Value, cohort.Value.Id, default);

        Assert.True(currentCohort.IsSuccess, currentCohort.Error.Message);

        Guid secondKey = Guid.NewGuid();

        var overallocated = await f.Store.IssueCohortMemberAsync(run.Value,
            new(f.Generation, root.Value.Quest.Id, null, secondKey, secondKey), cohort.Value.Id,
            currentCohort.Value.Revision, childRequest with { IdempotencyKey = secondKey }, default);

        Assert.Equal(ErrorCodes.Guild.GrantExceeded, overallocated.Error.Code);

        Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_members"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        var ordinary = await f.Store.IssueAsync(run.Value, identity, root.Value.Quest.Id, childRequest, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, ordinary.Error.Code);
    }


    [Fact]
    public async Task Cohort_ceiling_resolution_preserves_omitted_axes_and_refuses_explicit_widening()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildGrant live = f.Request.Grant with
        {
            Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 1000)),
            Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort],
        };

        GuildBudgetGrant ceiling = new(new(GuildBudgetMode.Finite, 2m), new(GuildBudgetMode.Finite, 100));

        await using var connection = await f.OpenAsync();

        await using var transaction = connection.BeginTransaction();

        var resolved = await f.Policy.ResolveCohortGrantWithinAsync(connection, transaction, live,
            new(Budget: new(true, ceiling)), default);

        Assert.True(resolved.IsSuccess, resolved.Error.Message);

        Assert.Equal(ceiling, resolved.Value.Budget);

        Assert.Equal<GuildDelegationKind>(live.Delegation, resolved.Value.Delegation);

        var widened = await f.Policy.ResolveCohortGrantWithinAsync(connection, transaction, live,
            new(Budget: new(true, new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)))), default);

        Assert.True(widened.IsFailure);
    }

}
