using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Disabled_scheduler_recovers_accounting_committed_after_its_renewal_snapshot_without_another_write()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var member = await CreateIdleSettledHistoryAsync(f, 1);

        string quest = member.Quest.Id.ToString("D").ToUpperInvariant();

        Assert.Equal(0L, await f.ScalarAsync($"SELECT AccountingStateCode FROM guild_quests WHERE Id='{quest}'"));

        long settledRevision = await f.ScalarAsync($"SELECT Revision FROM guild_quests WHERE Id='{quest}'");

        await using (var connection = await f.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode;";

            Assert.Equal("wal", Assert.IsType<string>(await command.ExecuteScalarAsync()).ToLowerInvariant());
        }

        f.Settings.CurrentValue.Features.Guild = false;

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), f.Gate);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        Task? renewalTick = null;

        await scheduler.StartAsync(default);

        try
        {
            await cadence.FirstTimerCreated.WaitAsync(watchdog.Token);

            await traced.WaitForInitialProofAsync(watchdog.Token);

            long stampCount = traced.CompletedStamps;

            // Only this unrelated write forces renewal of an already-established quiet proof.
            await f.ExecuteAsync($"INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES('{Guid.NewGuid().ToString("D").ToUpperInvariant()}','2026-11-16T00:00:00Z','2026-11-16T00:00:00Z')");

            traced.ArmActiveVm();

            // Timer continuations can execute synchronously. Keep the paused SQLite VM off
            // this test thread so the second WAL connection can commit while it is paused.
            renewalTick = Task.Run(() => cadence.Advance(TimeSpan.FromSeconds(1)));

            await traced.ActiveVmEntered.WaitAsync(watchdog.Token);

            Assert.False(renewalTick.IsCompleted);

            // The census VM starts after schema validation has read inside the deferred
            // transaction, so that transaction already owns the older WAL read snapshot.
            // Commit pending accounting from another connection before the VM can proceed.
            await f.ExecuteAsync($"UPDATE guild_quests SET AccountingStateCode=1,Revision=Revision+1 WHERE Id='{quest}'");

            long committedRevision = await f.ScalarAsync($"SELECT Revision FROM guild_quests WHERE Id='{quest}' AND AccountingStateCode=1");

            Assert.Equal(settledRevision + 1, committedRevision);

            traced.ReleaseActiveVm();

            await renewalTick.WaitAsync(watchdog.Token);

            // A changed-stamp read plus pre/post-census reads acknowledge completion of
            // renewal. No further test-authored database write is permitted after this point.
            await traced.WaitForStampCountAsync(stampCount + 3, watchdog.Token);

            long boundary = traced.CompletedBoundaryStatements;

            cadence.Advance(TimeSpan.FromSeconds(1));

            await traced.WaitForBoundaryAfterAsync(boundary, watchdog.Token);

            // With a swallowed commit, this tick merely reads the remembered new stamp and
            // the durable projection stays Pending. Actual recovery must settle it instead.
            Assert.Equal(0L, await f.ScalarAsync($"SELECT AccountingStateCode FROM guild_quests WHERE Id='{quest}'"));

            Assert.True(await f.ScalarAsync($"SELECT Revision FROM guild_quests WHERE Id='{quest}'") > committedRevision);
        }
        finally
        {
            traced.ReleaseActiveVm();

            try
            {
                if (renewalTick is not null) await renewalTick.WaitAsync(watchdog.Token);
            }
            finally
            {
                await scheduler.StopAsync(default);
            }
        }
    }
}
