using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Cohort_competing_admissions_commit_one_member_and_cannot_duplicate_its_stable_agent()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await NewAdmissionCohortAsync(f);

        async Task<Result<GuildQuestStored>> AdmitAsync(Guid key) => await f.Store.IssueCohortMemberAsync(setup.Run,
            new(f.Generation, setup.Root.Quest.Id, null, key, key), setup.Cohort.Id, 1,
            new(key, setup.Root.Quest.Revision, null, "Member", "Contribute", new()), default);

        var attempts = await Task.WhenAll(AdmitAsync(Guid.NewGuid()), AdmitAsync(Guid.NewGuid()));

        var accepted = Assert.Single(attempts, item => item.IsSuccess).Value;

        Assert.Equal(ErrorCodes.Guild.RevisionConflict, Assert.Single(attempts, item => item.IsFailure).Error.Code);

        Guid duplicateKey = Guid.NewGuid();

        var duplicate = await f.Store.IssueCohortMemberAsync(setup.Run,
            new(f.Generation, setup.Root.Quest.Id, null, duplicateKey, duplicateKey), setup.Cohort.Id, 2,
            new(duplicateKey, setup.Root.Quest.Revision, accepted.Quest.AgentId, "Again", "Another vote", new()), default);

        Assert.Contains(duplicate.Error.Code, new[] { ErrorCodes.Guild.Forbidden, ErrorCodes.Guild.NotFound });

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_members"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));
    }

    [Fact]
    public async Task Cohort_failed_execution_input_rolls_back_member_signet_session_budget_and_dispatch_atomically()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await NewAdmissionCohortAsync(f);

        GuildQuestStore rejecting = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(), TimeProvider.System, new RefuseCohortExecutionAdmission(), f.Readiness);

        Guid key = Guid.NewGuid();

        var refused = await rejecting.IssueCohortMemberAsync(setup.Run,
            new(f.Generation, setup.Root.Quest.Id, null, key, key), setup.Cohort.Id, 1,
            new(key, setup.Root.Quest.Revision, null, "Member", "Contribute", new()), default);

        Assert.Equal(ErrorCodes.Guild.Unavailable, refused.Error.Code);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_members"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM Sessions"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quest_start_intents"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT Revision FROM guild_cohorts"));
    }

    private static async Task<(GuildQuestStored Root, GuildCallerContext Run, CohortDto Cohort)> NewAdmissionCohortAsync(Fixture f)
    {
        var owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner, f.Request with
        {
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] },
        });

        Assert.True(root.IsSuccess, root.Error.Message);

        var run = await BindCohortRunAsync(f, owner, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new(), f.Policy, TimeProvider.System);

        var cohort = await cohorts.CreateAsync(run, new(Guid.NewGuid(), root.Value.Quest.Id, "Admission",
            new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        return (root.Value, run, cohort.Value);
    }

    private sealed class RefuseCohortExecutionAdmission : IGuildLocalExecutionAdmission
    {
        public Task<Result<GuildLocalExecutionInput>> ResolveWithinAsync(GuildCallerContext caller, GuildQuestStored quest,
            SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct) =>
            Task.FromResult(Result<GuildLocalExecutionInput>.Failure(new(ErrorCodes.Guild.Unavailable, "Fixture refuses after atomic member admission.")));
    }
}
