using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(QuestState.Failed, GuildContributionOutcome.Failed)]
    [InlineData(QuestState.Canceled, GuildContributionOutcome.Canceled)]
    public async Task Non_successful_primary_cannot_vote_and_makes_closed_all_impossible(QuestState outcome, GuildContributionOutcome contribution)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "not a successful vote", outcome: outcome);

        Assert.Equal((long)contribution, await f.ScalarAsync("SELECT OutcomeCode FROM guild_cohort_contributions"));

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.True((await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default)).IsSuccess);

        CohortConvergenceDto decided = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Impossible, decided.Decision);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections"));
    }

    [Fact]
    public async Task First_selection_does_not_change_when_prepared_loser_finishes_late()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.First, null, "text/plain", 2);

        TaskCompletionSource lateReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        async Task Pause()
        {
            lateReady.TrySetResult();

            await release.Task.WaitAsync(watchdog.Token);
        }

        Result<GuildResultReceipt>? lateResult = null;

        Task late = CommitConvergenceResultAsync(f, owner, members[1], "late", beforeCommit: Pause,
            observeCommit: result => lateResult = result);

        try
        {
            await lateReady.Task.WaitAsync(watchdog.Token);

            await CommitConvergenceResultAsync(f, owner, members[0], "winner");

            var current = await cohorts.GetAsync(issuer, cohort.Id, default);

            Assert.True(current.IsSuccess, current.Error.Message);

            Assert.True((await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default)).IsSuccess);

            CohortConvergenceDto selected = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

            Assert.Equal(GuildConvergenceDecision.Success, selected.Decision);

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections WHERE ThroughSequence=1"));

            release.TrySetResult();

            await late.WaitAsync(watchdog.Token);

            Assert.NotNull(lateResult);

            Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, lateResult.Error.Code);

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions"));

            var replay = await cohorts.ReconcileAsync(owner, cohort.Id, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(GuildConvergenceDecision.Success, replay.Value.Decision);

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections WHERE ThroughSequence=1"));
        }
        finally
        {
            release.TrySetResult();

            await late;
        }
    }

    [Fact]
    public async Task Simultaneous_primary_results_select_once_and_count_each_members_spend_once()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.First, null, "text/plain", 2);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.True((await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default)).IsSuccess);

        TaskCompletionSource firstReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource secondReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        async Task AtCommit(TaskCompletionSource ready)
        {
            ready.TrySetResult();

            await release.Task.WaitAsync(watchdog.Token);
        }

        Task first = CommitConvergenceResultAsync(f, owner, members[0], "first", beforeCommit: () => AtCommit(firstReady), knownUsd: 1m, knownTokens: 10);

        Task second = CommitConvergenceResultAsync(f, owner, members[1], "second", beforeCommit: () => AtCommit(secondReady), knownUsd: 2m, knownTokens: 20);

        try
        {
            await Task.WhenAll(firstReady.Task, secondReady.Task).WaitAsync(watchdog.Token);

            release.TrySetResult();

            await Task.WhenAll(first, second).WaitAsync(watchdog.Token);
        }
        finally
        {
            release.TrySetResult();

            await Task.WhenAll(first, second);
        }

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT min(ResultSequence) FROM guild_cohort_contributions"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT max(ResultSequence) FROM guild_cohort_contributions"));

        CohortConvergenceDto selected = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Success, selected.Decision);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections WHERE ThroughSequence=1"));

        var replay = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(GuildConvergenceDecision.Success, replay.Value.Decision);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections WHERE ThroughSequence=1"));

        var spend = await f.Authority.InQuestTransactionAsync<GuildBudgetSnapshot>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => new GuildBudgetLedger().ReadWithinAsync(connection, transaction, cohort.AllocationAccountId, ct), default);

        Assert.True(spend.IsSuccess, spend.Error.Message);

        Assert.Equal(3m, spend.Value.KnownUsd);

        Assert.Equal(30, spend.Value.KnownTokens);

        var treeSpend = await f.Authority.InQuestTransactionAsync<GuildBudgetSnapshot>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => new GuildBudgetLedger().ReadWithinAsync(connection, transaction, root.AllocationAccountId, ct), default);

        Assert.True(treeSpend.IsSuccess, treeSpend.Error.Message);

        Assert.Equal(3m, treeSpend.Value.KnownUsd);

        Assert.Equal(30, treeSpend.Value.KnownTokens);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_evidence"));
    }
}
