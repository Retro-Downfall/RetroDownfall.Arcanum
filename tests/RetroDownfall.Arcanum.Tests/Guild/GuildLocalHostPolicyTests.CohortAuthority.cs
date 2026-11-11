using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cohort_withdrawal_refuses_member_and_descendant_effects_without_inheriting_peer_membership(bool descendant)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await CreateCohortAuthorityAsync(f, descendant);

        int effects = 0;

        var before = await f.Authority.AdmitToolAsync(setup.Execution, CohortAuthorityTool, true,
            (_, _) => Task.FromResult(Result<bool>.Success(++effects > 0)), default);

        Assert.True(before.IsSuccess, before.Error.Message);

        await f.ExecuteAsync($"""
            UPDATE guild_cohorts SET MembershipVersion=MembershipVersion+1,Revision=Revision+1 WHERE Id='{setup.Cohort.Id:D}' COLLATE NOCASE;
            UPDATE guild_cohort_members SET StateCode=2,RemovalSequence=2 WHERE CohortId='{setup.Cohort.Id:D}' COLLATE NOCASE;
            UPDATE guild_cohort_member_quests SET Active=0;
            """);

        var after = await f.Authority.AdmitToolAsync(setup.Execution, CohortAuthorityTool, true,
            (_, _) => Task.FromResult(Result<bool>.Success(++effects > 0)), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, after.Error.Code);

        Assert.Equal(1, effects);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests"));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task Cohort_close_preserves_existing_effects_but_selection_seals_them(int state, bool permitted)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await CreateCohortAuthorityAsync(f, false);

        await f.ExecuteAsync($"UPDATE guild_cohorts SET StateCode={state},ClosedMembershipVersion=MembershipVersion,Revision=Revision+1 WHERE Id='{setup.Cohort.Id:D}' COLLATE NOCASE");

        int effects = 0;

        var result = await f.Authority.AdmitToolAsync(setup.Execution, CohortAuthorityTool, true,
            (_, _) => Task.FromResult(Result<bool>.Success(++effects > 0)), default);

        Assert.Equal(permitted, result.IsSuccess);

        Assert.Equal(permitted ? 1 : 0, effects);
    }

    private static GuildToolGrant CohortAuthorityTool => new(GuildToolSourceKind.InternalMcp, "arcanum", "list_agents");

    private static async Task<(CohortDto Cohort, GuildExecutionHandle Execution)> CreateCohortAuthorityAsync(Fixture f, bool descendant)
    {
        GuildCallerContext caller = await f.OperatorAsync();

        var root = await f.IssueAsync(caller, f.Request with
        {
            Grant = f.Request.Grant with { Tools = [CohortAuthorityTool], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] },
        });

        Assert.True(root.IsSuccess, root.Error.Message);

        var rootRun = await BindCohortRunAsync(f, caller, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        var cohort = await cohorts.CreateAsync(rootRun, new(Guid.NewGuid(), root.Value.Quest.Id, "Authority",
            new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        Guid key = Guid.NewGuid();

        var member = await f.Store.IssueCohortMemberAsync(rootRun, new(f.Generation, root.Value.Quest.Id, null, key, key),
            cohort.Value.Id, cohort.Value.Revision, new(key, root.Value.Quest.Revision, null, "Member", "Contribute", new()), default);

        Assert.True(member.IsSuccess, member.Error.Message);

        var memberRun = await BindCohortRunAsync(f, caller, member.Value);

        if (!descendant) return (cohort.Value, memberRun.Execution!);

        key = Guid.NewGuid();

        var child = await f.Store.IssueAsync(memberRun, new(f.Generation, member.Value.Quest.Id, null, key, key),
            member.Value.Quest.Id, new(key, member.Value.Quest.Revision, null, "Descendant", "Assist the primary", new()), default);

        Assert.True(child.IsSuccess, child.Error.Message);

        return (cohort.Value, (await BindCohortRunAsync(f, caller, child.Value)).Execution!);
    }

    private static async Task<GuildCallerContext> BindCohortRunAsync(Fixture f, GuildCallerContext caller, GuildQuestStored quest)
    {
        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Quest.Id, quest.Quest.OperationId,
            quest.Quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.Error.Message);

        return run.Value;
    }
}
