using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed partial class CovenantAvailabilityRepublicationTests
{
    [SkippableFact]
    public async Task Native_owner_backlog_uses_short_passes_and_reaches_a_campaign_behind_session_deletions()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using var host = Host(new InMemoryOsCredentialStore());

        using var client = host.CreateAuthenticatedClient();

        Guid campaign = await RegisterCampaignAsync(host, client, "catch-up");

        await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Campaign, campaign, Key, "Fixture protected row.");

        await PassAsync(host);

        await SeedDeletedSessionsAsync(host);

        await DeleteCampaignAsync(client, campaign);

        long campaignSequence = await CoreCampaignDeletionSequenceAsync(host);

        Assert.Equal(131, await ScalarAsync(host, "SELECT COUNT(*) FROM owner_deletion_events;"));

        ManualWallClockTimeProvider clock = new();

        using CovenantMaintenanceHostedService service = new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
            host.Services.GetRequiredService<ICovenantAvailability>(), clock,
            NullLogger<CovenantMaintenanceHostedService>.Instance);

        await service.StartAsync(Token);

        try
        {
            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(64, await ScalarAsync(host, "SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;"));

            Assert.Equal(1, await ScalarAsync(host, "SELECT COUNT(*) FROM covenant_heads;"));

            Assert.Equal(TimeSpan.FromMilliseconds(500), clock.NextDueIn);

            clock.Advance(TimeSpan.FromMilliseconds(500));

            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(128, await ScalarAsync(host, "SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;"));

            Assert.Equal(TimeSpan.FromMilliseconds(500), clock.NextDueIn);

            clock.Advance(TimeSpan.FromMilliseconds(500));

            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(campaignSequence, await ScalarAsync(host, "SELECT AppliedCampaignSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;"));

            Assert.Equal(0, await ScalarAsync(host, "SELECT COUNT(*) FROM covenant_heads;"));

            clock.Advance(TimeSpan.FromMilliseconds(500));

            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(TimeSpan.FromMinutes(5), clock.NextDueIn);
        }
        finally
        {
            await service.StopAsync(Token);
        }

        Assert.Equal(0, clock.ArmedTimers);
    }

    [SkippableTheory]
    [InlineData(320, 320)]
    [InlineData(600, 1)]
    public async Task Native_outbox_progress_below_or_above_the_row_target_schedules_another_bounded_pass(int firstRows, int secondRows)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using var host = Host(new InMemoryOsCredentialStore());

        using var client = host.CreateAuthenticatedClient();

        await PassAsync(host);

        await SeedMaintenanceOutboxAsync(host, firstRows, secondRows);

        ManualWallClockTimeProvider clock = new();

        using CovenantMaintenanceHostedService service = new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
            host.Services.GetRequiredService<ICovenantAvailability>(), clock,
            NullLogger<CovenantMaintenanceHostedService>.Instance);

        await service.StartAsync(Token);

        try
        {
            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(secondRows, await ScalarAsync(host, "SELECT COUNT(*) FROM covenant_search_outbox;"));

            Assert.Equal(1, await ScalarAsync(host, "SELECT AppliedSearchSequence FROM covenant_state WHERE StateKey = 1;"));

            Assert.Equal(0, await ScalarAsync(host, "SELECT COUNT(*) FROM covenant_fts_docsize;"));

            Assert.Equal(TimeSpan.FromMilliseconds(500), clock.NextDueIn);

            clock.Advance(TimeSpan.FromMilliseconds(500));

            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(0, await ScalarAsync(host, "SELECT COUNT(*) FROM covenant_search_outbox;"));

            Assert.Equal(2, await ScalarAsync(host, "SELECT AppliedSearchSequence FROM covenant_state WHERE StateKey = 1;"));

            Assert.Equal(TimeSpan.FromMilliseconds(500), clock.NextDueIn);

            clock.Advance(TimeSpan.FromMilliseconds(500));

            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(TimeSpan.FromMinutes(5), clock.NextDueIn);
        }
        finally
        {
            await service.StopAsync(Token);
        }

        Assert.Equal(0, clock.ArmedTimers);
    }

    [SkippableFact]
    public async Task Native_catchup_coalesces_rebuild_warnings_and_rearms_for_epoch_change_and_recovery()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using var host = Host(new InMemoryOsCredentialStore());

        using var client = host.CreateAuthenticatedClient();

        await PassAsync(host);

        MemoryErasureRouteDriver driver = new(client);

        await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Fixture protected row.");

        const string breakProjection = "UPDATE covenant_search_outbox SET DesiredVersionId = '00000000-0000-4000-8000-000000000001';";

        await ExecuteAsync(host, breakProjection);

        await SeedDeletedSessionsAsync(host);

        ManualWallClockTimeProvider clock = new();

        TestCapturingLogger<CovenantMaintenanceHostedService> logger = new();

        using CovenantMaintenanceHostedService service = new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
            host.Services.GetRequiredService<ICovenantAvailability>(), clock, logger);

        await service.StartAsync(Token);

        try
        {
            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Single(logger.Entries, entry => entry.Message.Contains("requires a rebuild", StringComparison.Ordinal));

            Assert.Equal(TimeSpan.FromMilliseconds(500), clock.NextDueIn);

            clock.Advance(TimeSpan.FromMilliseconds(500));

            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Single(logger.Entries, entry => entry.Message.Contains("requires a rebuild", StringComparison.Ordinal));

            await ExecuteAsync(host, "UPDATE covenant_state SET AcceleratorEpoch = AcceleratorEpoch + 1 WHERE StateKey = 1;");

            await RepublishMaintenanceStateAsync(host);

            clock.Advance(TimeSpan.FromMilliseconds(500));

            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(2, logger.Entries.Count(entry => entry.Message.Contains("requires a rebuild", StringComparison.Ordinal)));

            await ExecuteAsync(host, "UPDATE covenant_search_outbox SET DesiredVersionId = (SELECT CurrentVersionId FROM covenant_heads LIMIT 1);");

            clock.Advance(TimeSpan.FromMilliseconds(500));

            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Changed fixture row.");

            await ExecuteAsync(host, breakProjection);

            clock.Advance(TimeSpan.FromMilliseconds(500));

            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(3, logger.Entries.Count(entry => entry.Message.Contains("requires a rebuild", StringComparison.Ordinal)));

            Assert.Equal(TimeSpan.FromMinutes(5), clock.NextDueIn);
        }
        finally
        {
            await service.StopAsync(Token);
        }

        Assert.Equal(0, clock.ArmedTimers);

        int entriesAfterStop = logger.Entries.Count;

        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(entriesAfterStop, logger.Entries.Count);
    }

    private static async Task SeedDeletedSessionsAsync(ArcanumWebApplicationFactory host)
    {
        await using var scope = host.Services.CreateAsyncScope();

        await OpenAsync(scope);

        SqliteConnection connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenConnectionAsync(Token);

        // These empty Sessions own quota rows. Use the same narrow scope as core retention,
        // preserving its guarded cascade and the real owner-deletion triggers.
        using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            WITH RECURSIVE seq(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM seq WHERE n < 130)
            INSERT INTO Sessions (Id, CreatedAt, UpdatedAt)
            SELECT printf('00000000-0000-4000-8000-%012d', n),
                '2026-10-27T00:00:00.0000000Z', '2026-10-27T00:00:00.0000000Z' FROM seq;
            DELETE FROM Sessions WHERE Id LIKE '00000000-0000-4000-8000-%';
            """;

        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task RepublishMaintenanceStateAsync(ArcanumWebApplicationFactory host)
    {
        await using var scope = host.Services.CreateAsyncScope();

        await OpenAsync(scope);

        SqliteConnection connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenConnectionAsync(Token);

        Assert.True(await CovenantPersistedAvailabilityPublisher.PublishAsync(
            scope.ServiceProvider.GetRequiredService<CovenantAvailability>(), connection, acceleratorHealthy: true,
            CovenantHealthTransition.CanonicalMutation, Token));
    }

    private static async Task SeedMaintenanceOutboxAsync(ArcanumWebApplicationFactory host, int firstRows, int secondRows)
    {
        await using var scope = host.Services.CreateAsyncScope();

        await OpenAsync(scope);

        SqliteConnection connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenConnectionAsync(Token);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            WITH RECURSIVE seq(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM seq WHERE n < $first)
            INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
            SELECT 1, n - 1, n, '00000000-0000-4000-8000-000000000001', 1, NULL FROM seq;
            WITH RECURSIVE seq(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM seq WHERE n < $second)
            INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
            SELECT 2, n - 1, 10000 + n, '00000000-0000-4000-8000-000000000002', 1, NULL FROM seq;
            UPDATE covenant_state SET CanonicalSearchSequence = 2 WHERE StateKey = 1;
            """;

        command.Parameters.AddWithValue("$first", firstRows);

        command.Parameters.AddWithValue("$second", secondRows);

        await command.ExecuteNonQueryAsync(Token);

        Assert.True(await CovenantPersistedAvailabilityPublisher.PublishAsync(
            scope.ServiceProvider.GetRequiredService<CovenantAvailability>(), connection, acceleratorHealthy: true,
            CovenantHealthTransition.CanonicalMutation, Token));
    }
}
