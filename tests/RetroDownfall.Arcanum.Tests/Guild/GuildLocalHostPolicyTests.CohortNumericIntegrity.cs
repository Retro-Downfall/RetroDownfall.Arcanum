using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("Generation", GuildConvergenceKind.First)]
    [InlineData("KindCode", GuildConvergenceKind.First)]
    [InlineData("ThroughSequence", GuildConvergenceKind.First)]
    [InlineData("WinningBucket", GuildConvergenceKind.Quorum)]
    public async Task Cohort_selected_rule_rejects_real_numbers_in_both_readers(string column, GuildConvergenceKind kind)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, kind,
            kind == GuildConvergenceKind.Quorum ? 1 : null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "selected");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await f.ExecuteAsync($"""
            UPDATE guild_cohorts SET StateCode=3,DecisionCode=1,Revision=Revision+1;
            INSERT INTO guild_cohort_eval_candidates(CohortId,Generation,MemberId,ResultSequence,OutcomeCode,BucketId)
                SELECT CohortId,1,MemberId,ResultSequence,OutcomeCode,1 FROM guild_cohort_contributions;
            INSERT INTO guild_cohort_selections(CohortId,Generation,KindCode,WinningBucket,ThroughSequence)
                SELECT Id,1,PolicyCode,{(kind == GuildConvergenceKind.Quorum ? "1" : "NULL")},1 FROM guild_cohorts;
            """);

        var baseline = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default);

        Assert.True(baseline.IsSuccess, baseline.Error.Message);

        var validReference = await f.Authority.InQuestTransactionAsync<GuildDataRef?>(root.Quest.Id,
            async (connection, transaction, _, ct) =>
            {
                var stored = await GuildCohortStore.ReadWithinAsync(connection, transaction, cohort.Id, ct);

                return stored.IsFailure ? stored.Error
                    : await GuildCohortStore.ReadSingleSelectedReferenceWithinAsync(connection, transaction, stored.Value.Cohort, ct);
            }, default);

        Assert.True(validReference.IsSuccess, validReference.Error.Message);

        Assert.NotNull(validReference.Value);

        await f.ExecuteAsync($"""
            PRAGMA ignore_check_constraints=ON;
            DROP TRIGGER guild_cohort_selections_guard_update;
            UPDATE guild_cohort_selections SET {column}={column}+0.5;
            """);

        var page = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default);

        Assert.True(page.IsFailure);

        var reference = await f.Authority.InQuestTransactionAsync<GuildDataRef?>(root.Quest.Id,
            async (connection, transaction, _, ct) =>
            {
                var stored = await GuildCohortStore.ReadWithinAsync(connection, transaction, cohort.Id, ct);

                return stored.IsFailure ? stored.Error
                    : await GuildCohortStore.ReadSingleSelectedReferenceWithinAsync(connection, transaction, stored.Value.Cohort, ct);
            }, default);

        Assert.True(reference.IsFailure);
    }

    [Fact]
    public async Task Cohort_reconciliation_rejects_a_real_valued_checkpoint()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.True((await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default)).IsSuccess);

        await CommitConvergenceResultAsync(f, owner, members[0], "first");

        _ = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.PendingMemberCount == 1);

        await f.ExecuteAsync("PRAGMA ignore_check_constraints=ON; UPDATE guild_cohort_eval_state SET Generation=Generation+0.5");

        var refused = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, refused.Error.Code);
    }
}
