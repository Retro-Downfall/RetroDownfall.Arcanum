using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Consumption of the core owner-deletion journal by the Covenant cleanup worker.
/// </summary>
public sealed class CovenantOwnerCleanupTests
{

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static readonly Guid CampaignTwo = CovenantOperationGateFixture.CampaignTwo;

    private static readonly Guid SessionId = new("aaaaaaaa-9999-4999-8999-999999999999");

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void No_production_sql_appends_a_second_owner_deletion_event()
    {

        string root = FindRepositoryRoot();

        string[] offenders =
        [
            .. Directory
                .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
                .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
                .Where(path => File.ReadAllText(path)
                    .Contains("INSERT INTO owner_deletion_events", StringComparison.OrdinalIgnoreCase)),
        ];

        // The core delete triggers are the sole producer. A second append path in C# would let one
        // deletion produce two events, and a capability would then clean the same owner twice while
        // believing it had caught up.
        Assert.Empty(offenders);

    }

    [Fact]
    public async Task Deleting_a_campaign_appends_exactly_one_trigger_event()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await ExecuteAsync(fixture, $"DELETE FROM \"Campaigns\" WHERE \"Id\" = '{CampaignOne:D}';");

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM owner_deletion_events;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT OwnerKindCode FROM owner_deletion_events;"));

    }

    [Fact]
    public async Task Core_deletion_succeeds_when_the_covenant_family_is_wholly_absent()
    {

        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        // Core objects only: no canonical tier at all, which is exactly the isolation the trigger-
        // owned journal exists to provide.
        await database.InstallCoreObjectsAsync(
            ["Campaigns", "owner_deletion_events", "owner_deletion_operation_intents", "Campaigns_owner_deletion_event"],
            Token);

        await database.ExecuteAsync(
            "INSERT INTO \"Campaigns\" (\"Id\", \"Name\", \"NameLower\", \"Path\", \"Type\", \"Settings\", "
            + "\"CreatedAt\", \"UpdatedAt\") VALUES ('"
            + CampaignOne.ToString("D")
            + "', 'one', 'one', '/tmp/one', 1, '{}', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');",
            Token);

        await database.ExecuteAsync($"DELETE FROM \"Campaigns\" WHERE \"Id\" = '{CampaignOne:D}';", Token);

        Assert.Equal(1, await database.ScalarLongAsync("SELECT COUNT(*) FROM owner_deletion_events;", Token));

    }

    [Fact]
    public async Task A_cleanup_batch_removes_only_the_deleted_campaign()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await fixture.AddCampaignAsync(CampaignTwo, "two", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "doomed.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Going away.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignTwo,
            "surviving.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Staying.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Global.",
            Token);

        long epochBefore = await ScalarAsync(
            fixture,
            "SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = 'doomed.key';");

        await ExecuteAsync(fixture, $"DELETE FROM \"Campaigns\" WHERE \"Id\" = '{CampaignOne:D}';");

        CovenantCleanupOutcome outcome = await RunAsync(fixture);

        Assert.Equal(1, outcome.CampaignsCleaned);

        Assert.Equal(1, outcome.HeadsRemoved);

        Assert.True(outcome.SearchSequenceAdvanced);

        Assert.Equal(2, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_heads;"));

        Assert.Equal(
            0,
            await ScalarAsync(fixture, $"SELECT COUNT(*) FROM covenant_entries WHERE CampaignId = '{CampaignOne:D}';"));

        Assert.Equal(
            epochBefore + 1,
            await ScalarAsync(fixture, "SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = 'doomed.key';"));

        // One deletion delta per removed head, so the accelerator can subtract exactly those rows.
        Assert.Equal(
            1,
            await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_search_outbox WHERE DesiredVersionId IS NULL;"));

    }

    [Fact]
    public async Task A_campaign_with_no_covenant_rows_advances_no_search_sequence()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await ExecuteAsync(fixture, $"DELETE FROM \"Campaigns\" WHERE \"Id\" = '{CampaignOne:D}';");

        CovenantCleanupOutcome outcome = await RunAsync(fixture);

        Assert.Equal(1, outcome.CampaignsCleaned);

        Assert.Equal(0, outcome.HeadsRemoved);

        Assert.False(outcome.SearchSequenceAdvanced);

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"));

    }

    [Fact]
    public async Task Session_deletion_removes_its_turn_evidence()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        await CovenantCapacityFixture.AddTurnReceiptAsync(
            fixture,
            SessionId,
            Guid.NewGuid(),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            CovenantFinalOutcome.Completed,
            Token);

        await DeleteSessionAsync(fixture, SessionId);

        CovenantCleanupOutcome outcome = await RunAsync(fixture);

        Assert.Equal(1, outcome.SessionsCleaned);

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_turn_receipts;"));

    }

    [Fact]
    public async Task The_applied_cursor_advances_and_a_second_run_finds_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await ExecuteAsync(fixture, $"DELETE FROM \"Campaigns\" WHERE \"Id\" = '{CampaignOne:D}';");

        CovenantCleanupOutcome first = await RunAsync(fixture);

        Assert.Equal(1, first.CampaignsCleaned);

        Assert.True(first.AppliedCampaignSequence > 0);

        Assert.Equal(
            first.AppliedCampaignSequence,
            await ScalarAsync(
                fixture,
                "SELECT AppliedCampaignSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;"));

        CovenantCleanupOutcome second = await RunAsync(fixture);

        Assert.Equal(0, second.CampaignsCleaned);

        Assert.Equal(0, second.SessionsCleaned);

    }

    [Fact]
    public async Task A_stale_dataset_generation_refuses_the_batch()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await ExecuteAsync(fixture, $"DELETE FROM \"Campaigns\" WHERE \"Id\" = '{CampaignOne:D}';");

        FakeCovenantAvailability availability = new();

        availability.Mutate(current => current with { DatasetGeneration = Guid.NewGuid() });

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);


        await using CovenantCleanupLease lease =
            (await gate.AcquireCleanupAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantCleanupOutcome> refused = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantCleanupWorker().RunBatchAsync(lease, transaction, Token).AsTask(),
            Token,
            commit: false);

        Assert.Equal("Covenant.StaleSnapshot", refused.Error.Code);

        Assert.Equal(
            0,
            await ScalarAsync(
                fixture,
                "SELECT AppliedCampaignSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;"));

    }

    [Fact]
    public async Task A_revoked_cleanup_lease_refuses_the_batch()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            await LiveAvailabilityAsync(fixture));

        CovenantCleanupLease lease = (await gate.AcquireCleanupAsync(CovenantOperationScope.Global, Token)).Value;

        await lease.DisposeAsync();

        Result<CovenantCleanupOutcome> refused = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantCleanupWorker().RunBatchAsync(lease, transaction, Token).AsTask(),
            Token,
            commit: false);

        Assert.Equal("Covenant.StaleSnapshot", refused.Error.Code);

    }

    private static async Task<CovenantCanonicalFixture> CreateAsync()
    {

        CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            Token,
            coreObjects:
            [
                .. CovenantCapacityFixture.CoreObjects,

                "capability_cleanup_state",

                "owner_deletion_events",

                "owner_deletion_operation_intents",

                "Campaigns_owner_deletion_event",

                "Sessions_owner_deletion_event",

                "owner_deletion_events_guard_delete",

                "owner_deletion_events_guard_update",
            ]);

        await ExecuteAsync(
            fixture,
            """
            INSERT OR IGNORE INTO capability_cleanup_state
                (CapabilityFamilyCode, AppliedCampaignSequence, AppliedSessionSequence, FullSweepRequired, UpdatedAtUtc)
            VALUES (1, 0, 0, 0, '2026-01-01T00:00:00.0000000Z');
            """);

        return fixture;

    }

    private static async Task<CovenantCleanupOutcome> RunAsync(CovenantCanonicalFixture fixture)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            await LiveAvailabilityAsync(fixture));

        await using CovenantCleanupLease lease =
            (await gate.AcquireCleanupAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantCleanupOutcome> outcome = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantCleanupWorker().RunBatchAsync(lease, transaction, Token).AsTask(),
            Token);

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : null);

        return outcome.Value;

    }

    /// <summary>
    /// A published availability snapshot whose dataset generation is the one this scratch tier
    /// actually installed, so a lease taken from it is not stale before it is used.
    /// </summary>
    private static async Task<FakeCovenantAvailability> LiveAvailabilityAsync(CovenantCanonicalFixture fixture)
    {

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        FakeCovenantAvailability availability = new();

        availability.Mutate(current => current with { DatasetGeneration = generation });

        return availability;

    }

    /// <summary>
    /// Deletes a Session the way core retention does, holding the capacity scope its cascaded
    /// counter row requires.
    /// </summary>
    private static async Task DeleteSessionAsync(CovenantCanonicalFixture fixture, Guid sessionId)
    {

        using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance
            .Authorize(fixture.Connection, CovenantSqliteAuthorizationKind.SessionRetention);

        await ExecuteAsync(fixture, $"DELETE FROM \"Sessions\" WHERE \"Id\" = '{sessionId:D}';");

    }

    private static async Task ExecuteAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);

    }

    private static Task<long> ScalarAsync(CovenantCanonicalFixture fixture, string sql) =>
        CovenantCapacityFixture.ScalarAsync(fixture, sql, Token);

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {

            directory = directory.Parent;

        }

        Assert.NotNull(directory);

        return directory!.FullName;

    }

}
