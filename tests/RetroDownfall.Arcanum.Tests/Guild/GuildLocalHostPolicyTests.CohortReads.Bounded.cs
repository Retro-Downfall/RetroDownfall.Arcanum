using System.Reflection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Cohort_selected_page_uses_membership_keyset_before_candidate_lookups_without_sorting_selection()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        await using var connection = await f.OpenAsync();

        await using var transaction = connection.BeginTransaction();

        Type ruleType = typeof(GuildCohortStore).GetNestedType("SelectionRule", BindingFlags.NonPublic)!;

        object rule = Activator.CreateInstance(ruleType, [1L, GuildConvergenceKind.All, null, null])!;

        MethodInfo query = typeof(GuildCohortStore).GetMethod("SelectedQuery", BindingFlags.NonPublic | BindingFlags.Static)!;

        await using SqliteCommand plan = Assert.IsType<SqliteCommand>(query.Invoke(null,
            [connection, transaction, rule, Guid.NewGuid(), 1_000_000L, 50_000L, 1, false, null]));

        plan.CommandText = "EXPLAIN QUERY PLAN " + plan.CommandText;

        List<string> steps = [];

        await using var reader = await plan.ExecuteReaderAsync();

        while (await reader.ReadAsync()) steps.Add(reader.GetString(3));

        Assert.Contains(steps, step => step.Contains("SEARCH m USING INDEX idx_guild_cohort_members_order", StringComparison.Ordinal)
            && step.Contains("AdmissionSequence>", StringComparison.Ordinal));

        Assert.DoesNotContain(steps, step => step.Contains("SCAN candidate", StringComparison.Ordinal)
            || step.Contains("USE TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal));

        Assert.Contains(steps, step => step.Contains("SEARCH candidate", StringComparison.Ordinal)
            && step.Contains("MemberId=?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cohort_selected_sparse_page_stops_after_one_hundred_members_and_preserves_continuation()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.First, null, "text/plain", 101);

        await CommitConvergenceResultAsync(f, owner, members[^1], "the selected late admission");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await f.ExecuteAsync("""
            UPDATE guild_cohorts SET StateCode=3,DecisionCode=1,Revision=Revision+1;
            INSERT INTO guild_cohort_eval_candidates(CohortId,Generation,MemberId,ResultSequence,OutcomeCode,BucketId)
                SELECT CohortId,1,MemberId,ResultSequence,OutcomeCode,NULL FROM guild_cohort_contributions;
            INSERT INTO guild_cohort_selections(CohortId,Generation,KindCode,WinningBucket,ThroughSequence)
                SELECT Id,1,2,NULL,1 FROM guild_cohorts;
            """);

        var first = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Empty(first.Value.Items);

        Assert.Equal(100L, first.Value.NextAdmissionSequence);

        var second = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id,
            first.Value.NextAdmissionSequence!.Value, 1, default);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(members[^1].Quest.Id, Assert.Single(second.Value.Items).PrimaryVotingQuestId);

        Assert.Null(second.Value.NextAdmissionSequence);
    }
}
