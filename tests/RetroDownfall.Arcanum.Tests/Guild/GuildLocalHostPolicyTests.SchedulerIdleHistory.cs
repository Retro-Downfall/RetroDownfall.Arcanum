using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Disabled_scheduler_recovers_a_stale_terminal_cohort_settlement_before_becoming_quiet()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        _ = await CreateIdleTerminalHistoryAsync(f, 1, refreshTerminalSnapshot: false);

        const string pendingSnapshot = """
            SELECT count(*) FROM guild_cohorts c JOIN guild_cohort_settlement_state s ON s.CohortId=c.Id
            JOIN guild_budget_accounts account ON account.AccountId=c.AllocationAccountId
            JOIN guild_cohort_settlement_clock clock ON clock.RootAccountId=account.RootAccountId
            WHERE c.StateCode=4 AND (s.Complete=0 OR s.Epoch<>clock.Revision)
            """;

        Assert.Equal(1L, await f.ScalarAsync(pendingSnapshot));

        long revision = await f.ScalarAsync("SELECT Revision FROM guild_cohorts WHERE StateCode=4");

        await AssertRetainedHistoryBecomesQuietAsync(f, advanceAccountingPage: false);

        Assert.Equal(0L, await f.ScalarAsync(pendingSnapshot));

        Assert.Equal(revision, await f.ScalarAsync("SELECT Revision FROM guild_cohorts WHERE StateCode=4"));
    }

    [Fact]
    public async Task Disabled_scheduler_becomes_quiet_with_more_than_one_page_of_settled_bound_history_and_a_terminal_cohort()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        _ = await CreateIdleSettledHistoryAsync(f, 101);

        Assert.True(await f.ScalarAsync("SELECT count(*) FROM guild_local_executions") > 100);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohorts WHERE StateCode=4"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_quests WHERE AccountingStateCode<>0"));

        await AssertRetainedHistoryBecomesQuietAsync(f, advanceAccountingPage: true);
    }

    [Fact]
    public async Task Disabled_scheduler_revisits_accounting_committed_behind_its_first_page_before_becoming_quiet()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var history = await CreateIdleTerminalHistoryAsync(f, 101, refreshTerminalSnapshot: true);

        string target;

        await using (SqliteConnection connection = await f.OpenAsync())
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT page.Id FROM (SELECT Id FROM guild_quests ORDER BY Id LIMIT 100) page
                JOIN guild_local_executions binding ON binding.QuestId=page.Id
                ORDER BY page.Id LIMIT 1
                """;

            target = Assert.IsType<string>(await command.ExecuteScalarAsync());
        }

        Assert.InRange(await f.ScalarAsync($"SELECT count(*) FROM guild_quests WHERE Id<='{target}'"), 1L, 100L);

        Assert.Equal(0L, await f.ScalarAsync($"SELECT AccountingStateCode FROM guild_quests WHERE Id='{target}'"));

        f.Settings.CurrentValue.Features.Guild = false;

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), f.Gate);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        await scheduler.StartAsync(default);

        try
        {
            await cadence.FirstTimerCreated.WaitAsync(watchdog.Token);

            // The cancellation-frontier statement follows the first accounting page. More
            // than100 retained Quests ensure the bounded primary-key scan has not wrapped.
            await traced.WaitForBoundaryAfterAsync(0, watchdog.Token);

            int firstPageOpens = f.Connections.Kinds.Count;

            await f.ExecuteAsync($"UPDATE guild_quests SET AccountingStateCode=1,Revision=Revision+1 WHERE Id='{target}'");

            long pendingRevision = await f.ScalarAsync($"SELECT Revision FROM guild_quests WHERE Id='{target}' AND AccountingStateCode=1");

            // The test-authored Quest revision also advances the Cohort settlement epoch.
            // Settle that projection before resuming the manual accounting-cursor scenario.
            await DriveIdleHistoryConvergenceAsync(f, history.Cohorts, history.Owner, history.CohortId,
                value => value.State == GuildCohortState.Completed);

            long stamps = traced.CompletedStamps;

            cadence.Advance(TimeSpan.FromSeconds(1));

            // The second accounting page wraps the scan. Its subsequent global census
            // must see the obligation behind the cursor despite the empty remaining page.
            await traced.WaitForStampCountAsync(stamps + 2, watchdog.Token);

            Assert.True(f.Connections.Kinds.Count > firstPageOpens);

            Assert.Equal(1L, await f.ScalarAsync($"SELECT AccountingStateCode FROM guild_quests WHERE Id='{target}'"));

            Assert.Equal(pendingRevision, await f.ScalarAsync($"SELECT Revision FROM guild_quests WHERE Id='{target}'"));

            int afterWrapOpens = f.Connections.Kinds.Count;

            long boundary = traced.CompletedBoundaryStatements;

            // No further test-authored write wakes recovery. The global pending census
            // must keep discovery active until its next first page revisits this row.
            cadence.Advance(TimeSpan.FromSeconds(1));

            await traced.WaitForBoundaryAfterAsync(boundary, watchdog.Token);

            Assert.True(f.Connections.Kinds.Count > afterWrapOpens, "Pending accounting behind the wrapped cursor must prevent quiet.");

            Assert.Equal(0L, await f.ScalarAsync($"SELECT AccountingStateCode FROM guild_quests WHERE Id='{target}'"));

            Assert.True(await f.ScalarAsync($"SELECT Revision FROM guild_quests WHERE Id='{target}'") > pendingRevision);
        }
        finally
        {
            await scheduler.StopAsync(default);
        }
    }

    [Fact]
    public async Task Disabled_scheduler_becomes_quiet_with_an_unstarted_metadata_only_quest_without_an_execution_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext owner = await f.OperatorAsync();

        var issued = await f.IssueAsync(owner);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_local_executions"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_remote_executions"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_dispatches"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects"));

        // Issuance starts its accounting projection Pending even without execution. This
        // fixture represents retained, already-settled metadata rather than recovery work.
        await f.ExecuteAsync("UPDATE guild_quests SET AccountingStateCode=0,Revision=Revision+1");

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_quests WHERE AccountingStateCode<>0 OR CancellationRequested<>0 OR CancellationConverged<>0"));

        await AssertRetainedHistoryBecomesQuietAsync(f, advanceAccountingPage: false);
    }

    [Fact]
    public async Task Disabled_scheduler_keeps_recovering_a_future_execution_lease_despite_terminal_quest_history()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildQuestStored member = await CreateIdleSettledHistoryAsync(f, 1);

        string operation = member.Quest.OperationId.ToString("N");

        // Retained terminal Quest evidence alone cannot prove its external operation stopped.
        // This future lease cannot become due during any manually advanced discovery tick.
        await f.ExecuteAsync($"UPDATE LongRunningOperations SET State=1,CompletedAt=NULL,LeaseOwner='retained-writer',LeaseExpiresAt='2099-01-01T00:00:00.0000000Z',Revision=Revision+1 WHERE Id='{operation}'");

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM LongRunningOperations WHERE Id='{operation}' AND State=1 AND LeaseExpiresAt='2099-01-01T00:00:00.0000000Z'"));

        f.Settings.CurrentValue.Features.Guild = false;

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), f.Gate);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        await scheduler.StartAsync(default);

        try
        {
            await cadence.FirstTimerCreated.WaitAsync(watchdog.Token);

            // The initial pre/post census stamps acknowledge a real completed census, even
            // though unresolved future work must prevent that census from parking the worker.
            await traced.WaitForInitialProofAsync(watchdog.Token);

            for (int tick = 0; tick < 5; tick++)
            {
                int opens = f.Connections.Kinds.Count;

                long boundary = traced.CompletedBoundaryStatements;

                cadence.Advance(TimeSpan.FromSeconds(1));

                await traced.WaitForBoundaryAfterAsync(boundary, watchdog.Token);

                Assert.True(f.Connections.Kinds.Count > opens, "Retained future execution work must keep ordinary recovery polling active.");
            }

            Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM LongRunningOperations WHERE Id='{operation}' AND State=1 AND LeaseExpiresAt='2099-01-01T00:00:00.0000000Z'"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_quests WHERE AccountingStateCode<>0"));
        }
        finally
        {
            await scheduler.StopAsync(default);
        }
    }

    private static async Task AssertRetainedHistoryBecomesQuietAsync(Fixture f, bool advanceAccountingPage)
    {
        f.Settings.CurrentValue.Features.Guild = false;

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), f.Gate);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        await scheduler.StartAsync(default);

        try
        {
            await cadence.FirstTimerCreated.WaitAsync(watchdog.Token);

            if (advanceAccountingPage)
            {
                await traced.WaitForBoundaryAfterAsync(0, watchdog.Token);

                cadence.Advance(TimeSpan.FromSeconds(1));
            }

            await traced.WaitForInitialProofAsync(watchdog.Token);

            int opens = f.Connections.Kinds.Count;

            for (int tick = 0; tick < 20; tick++)
            {
                long boundary = traced.CompletedBoundaryStatements;

                cadence.Advance(TimeSpan.FromSeconds(1));

                await traced.WaitForBoundaryAfterAsync(boundary, watchdog.Token);

                Assert.Equal(opens, f.Connections.Kinds.Count);

                Assert.Equal(1, traced.LastBoundaryAutocommit);
            }
        }
        finally
        {
            await scheduler.StopAsync(default);
        }
    }

    private static ServiceProvider IdleHistoryServices(Fixture f)
    {
        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store,
            new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        ServiceCollection registration = new();

        registration.AddSingleton(f.Store);

        registration.AddSingleton(f.Policy);

        registration.AddSingleton(cohorts);

        return registration.BuildServiceProvider();
    }

    private static async Task<GuildQuestStored> CreateIdleSettledHistoryAsync(Fixture f, int memberCount) =>
        (await CreateIdleTerminalHistoryAsync(f, memberCount, refreshTerminalSnapshot: true)).Member;

    private static async Task<(GuildQuestStored Member, GuildCallerContext Owner, GuildCohortStore Cohorts, Guid CohortId)>
        CreateIdleTerminalHistoryAsync(Fixture f, int memberCount, bool refreshTerminalSnapshot)
    {
        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", memberCount);

        foreach (GuildQuestStored member in members)
            await CommitConvergenceResultAsync(f, owner, member, "settled history");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await DriveIdleHistoryConvergenceAsync(f, cohorts, owner, cohort.Id,
            value => value.State is GuildCohortState.Draining or GuildCohortState.Completed);

        foreach (GuildQuestStored member in members)
        {
            var stopped = await f.Store.RefreshCancellationConvergenceAsync(owner, member.Quest.Id, default);

            Assert.True(stopped.IsSuccess, stopped.Error.Message);

            Assert.True(stopped.Value);
        }

        await DriveIdleHistoryConvergenceAsync(f, cohorts, owner, cohort.Id,
            value => value.State == GuildCohortState.Completed);

        await CommitConvergenceResultAsync(f, owner, root, "settled owner", retainedRun: issuer);

        if (refreshTerminalSnapshot)
            await DriveIdleHistoryConvergenceAsync(f, cohorts, owner, cohort.Id,
                value => value.State == GuildCohortState.Completed);

        return (members[0], owner, cohorts, cohort.Id);
    }

    private static async Task DriveIdleHistoryConvergenceAsync(Fixture f, GuildCohortStore cohorts,
        GuildCallerContext owner, Guid cohort, Func<CohortConvergenceDto, bool> complete)
    {
        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(5));

        await using SqliteConnection observation = await f.OpenAsync();

        string previous = await ReadIdleHistoryProgressAsync(observation, cohort, watchdog.Token);

        HashSet<string> seen = [previous];

        while (true)
        {
            watchdog.Token.ThrowIfCancellationRequested();

            var result = await cohorts.ReconcileAsync(owner, cohort, watchdog.Token);

            Assert.True(result.IsSuccess || result.Error.Code == ErrorCodes.Guild.MaintenanceDeferred, result.Error.Message);

            if (result.IsSuccess && complete(result.Value)) return;

            string next = await ReadIdleHistoryProgressAsync(observation, cohort, watchdog.Token);

            Assert.NotEqual(previous, next);

            Assert.True(seen.Add(next), "Cohort fixture reconciliation repeated a durable recovery checkpoint.");

            previous = next;
        }
    }

    private static async Task<string> ReadIdleHistoryProgressAsync(SqliteConnection connection, Guid cohort, CancellationToken ct)
    {
        StringBuilder checkpoint = new();

        string[] tables =
        [
            "guild_cohorts", "guild_cohort_eval_state", "guild_cohort_eval_candidates", "guild_cohort_eval_buckets",
            "guild_cohort_settlement_state", "guild_cohort_settlement_nodes", "guild_cohort_stop_frontiers",
            "guild_cohort_award_authority_scan",
        ];

        foreach (string table in tables)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = table == "guild_cohorts"
                ? "SELECT StateCode,DecisionCode,FrozenMemberCount,ClosedMembershipVersion FROM guild_cohorts WHERE Id=$cohort"
                : $"SELECT * FROM {table} WHERE CohortId=$cohort ORDER BY 1,2";

            command.Parameters.AddWithValue("$cohort", cohort.ToString("D").ToUpperInvariant());

            checkpoint.Append(table).Append(':');

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                for (int column = 0; column < reader.FieldCount; column++)
                {
                    object value = reader.GetValue(column);

                    checkpoint.Append(value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, CultureInfo.InvariantCulture)).Append('|');
                }

                checkpoint.AppendLine();
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkpoint.ToString())));
    }
}
