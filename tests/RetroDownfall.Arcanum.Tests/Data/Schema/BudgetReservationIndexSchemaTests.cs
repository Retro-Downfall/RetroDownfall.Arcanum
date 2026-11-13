using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class BudgetReservationIndexSchemaTests
{
    [Fact]
    public void The_version_twenty_one_fixture_preserves_its_captured_fingerprint() =>
        Assert.Equal(CoreSchemaVersionTwentyOneFixture.PublishedFingerprint, CoreSchemaVersionTwentyOneFixture.Fingerprint);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outstanding_reservations_use_index_searches_and_preserve_exact_membership(bool evolve)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            evolve ? CoreSchemaVersionTwentyOneFixture.ChainSet() : GrimoireSchemaVersionChains.Default,
            64,
            CancellationToken.None);

        await SeedAsync(connection);

        if (evolve)
        {
            await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, CancellationToken.None);
        }

        await AssertOutstandingAsync(connection);

        using var freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(fresh, 64, CancellationToken.None);

        Assert.Equal(await ReadCatalogAsync(fresh), await ReadCatalogAsync(connection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normalized_reservation_identity_seeks_all_cross_run_aliases(bool evolve)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        Assert.Equal("8039D59876DB73F0B03CEFBB9A24364376B057F603099F57AE4FFA1407FBD448", GrimoireSchemaCatalog.ComputeSourceFingerprint(Core39Objects()));

        var initial = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, CancellationToken.None,
            evolve ? Core39Chains() : null);

        Assert.Equal(evolve ? 39 : 40, initial.Core.SchemaVersion);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO BudgetReservations (Id, RunId, BudgetPeriod, Status, ExpiresAt, CreatedAt, UpdatedAt)
            VALUES ('ABCDEF0123456789ABCDEF0123456789','run-one','2026-01-01',0,'x','x','x'),
                   ('abcdef01-2345-6789-abcd-ef0123456789','run-two','2026-01-01',0,'x','x','x'),
                   ('AbCdEf01-2345-6789-AbCd-Ef0123456789','run-three','2026-01-01',0,'x','x','x'),
                   ('unrelated','run-four','2026-01-01',0,'x','x','x');
            """;

        await command.ExecuteNonQueryAsync();

        if (evolve)
        {
            var upgraded = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, CancellationToken.None);

            Assert.Equal(40, upgraded.Core.SchemaVersion);
        }

        const string query = "SELECT RunId FROM BudgetReservations WHERE lower(replace(Id, '-', '')) = $id ORDER BY RunId";

        command.CommandText = query;
        command.Parameters.AddWithValue("$id", "abcdef0123456789abcdef0123456789");

        List<string> runs = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                runs.Add(reader.GetString(0));
            }
        }

        Assert.Equal(new[] { "run-one", "run-three", "run-two" }, runs);

        command.CommandText = "EXPLAIN QUERY PLAN " + query;

        List<string> plan = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.Contains(plan, detail => detail.Contains("SEARCH BudgetReservations USING INDEX IX_BudgetReservations_Id_Norm", StringComparison.Ordinal));

        using var freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(fresh, 64, CancellationToken.None);

        Assert.Equal(await ReadCatalogAsync(fresh), await ReadCatalogAsync(connection));
    }

    [Fact]
    public void Current_core_fingerprint_and_index_ownership_are_exact()
    {
        Assert.Equal("FEEE1C2184FD0748760F91F4121FD675E2C977FFC406B609A6FD0E58FFE97E59", GrimoireSchemaCatalog.CoreSchemaFingerprint);

        GrimoireSchemaTierOwnershipRegistry registry = GrimoireSchemaTierOwnershipRegistry.CreateDefault();

        Assert.True(registry.TryGetIndexOwner("IX_BudgetReservations_Id_Norm", out GrimoireSchemaTransactionTier tier));

        Assert.Equal(GrimoireSchemaTransactionTier.Core, tier);
    }

    [Fact]
    public async Task Failed_index_transition_rolls_back_and_reopens_for_retry()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection connection = await file.OpenAsync(CancellationToken.None))
        {
            await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, CancellationToken.None, Core39Chains());

            await using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = "INSERT INTO BudgetReservations (Id, RunId, BudgetPeriod, Status, ExpiresAt, CreatedAt, UpdatedAt) VALUES ('A', 'other-run', '2026-01-01', 0, 'x', 'x', 'x')";
            await seed.ExecuteNonQueryAsync();

            await Assert.ThrowsAnyAsync<Exception>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, Failing40Chains(), 64, CancellationToken.None));

            Assert.Equal(39, await GrimoireSchemaEvolutionInstallerTests.RecordedVersionAsync(connection));

            seed.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_BudgetReservations_Id_Norm'";
            Assert.Equal(0L, await seed.ExecuteScalarAsync());

            seed.CommandText = "SELECT RunId FROM BudgetReservations WHERE Id='A'";
            Assert.Equal("other-run", await seed.ExecuteScalarAsync());
        }

        await using SqliteConnection reopened = await file.OpenAsync(CancellationToken.None);

        var upgraded = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(reopened, 64, CancellationToken.None);

        Assert.Equal(40, upgraded.Core.SchemaVersion);

        await using SqliteCommand check = reopened.CreateCommand();
        check.CommandText = "SELECT RunId FROM BudgetReservations WHERE Id='A'";
        Assert.Equal("other-run", await check.ExecuteScalarAsync());
    }

    private static GrimoireSchemaVersionChainSet Failing40Chains()
    {
        GrimoireSchemaVersionChainSet current = GrimoireSchemaVersionChains.Default;
        GrimoireSchemaVersionChain core = current.ForTier(GrimoireSchemaTransactionTier.Core);
        GrimoireSchemaVersionStep final = core.Steps[^1];
        GrimoireSchemaVersionStep failure = final with
        {
            Statements = [.. final.Statements, new GrimoireSchemaTransitionStatement("test.failure", 20, "failure", "SELECT * FROM missing_transition_test_table;")],
        };

        return new GrimoireSchemaVersionChainSet(
        [
            new GrimoireSchemaVersionChain(core.HeadManifest, core.HeadObjects, [.. core.Steps.Take(core.Steps.Count - 1), failure]),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            current.ForTier(GrimoireSchemaTransactionTier.GuildCanonical),
        ]);
    }

    private static IReadOnlyList<GrimoireSchemaObject> Core39Objects() =>
        [.. GrimoireSchemaCatalog.CoreObjects.Select(definition => definition.Name == "BudgetReservations"
            ? definition with { Sql = definition.Sql.Replace("\nCREATE INDEX IF NOT EXISTS \"IX_BudgetReservations_Id_Norm\" ON \"BudgetReservations\" (lower(replace(\"Id\", '-', '')));\n", "", StringComparison.Ordinal) }
            : definition)];

    private static GrimoireSchemaVersionChainSet Core39Chains()
    {
        IReadOnlyList<GrimoireSchemaObject> objects = Core39Objects();

        GrimoireSchemaVersionChainSet current = GrimoireSchemaVersionChains.Default;

        return new GrimoireSchemaVersionChainSet(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 39,
                    "8039D59876DB73F0B03CEFBB9A24364376B057F603099F57AE4FFA1407FBD448", objects),
                objects,
                [.. current.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(static step => step.ToVersion <= 39)]),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            current.ForTier(GrimoireSchemaTransactionTier.GuildCanonical),
        ]);
    }

    private static async Task<string[]> ReadCatalogAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT type || ':' || name || ':' || sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> definitions = [];

        while (await reader.ReadAsync())
        {
            definitions.Add(reader.GetString(0));
        }

        return [.. definitions];
    }

    private static async Task SeedAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM rows WHERE n < 2048)
            INSERT INTO "BudgetReservations"
                ("Id", "RunId", "BudgetPeriod", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt", "ReservedUsd", "ReconciledUsd")
            SELECT printf('%032X', n), printf('%032X', n), '2026-10-24', @reconciled,
                @at, @at, @at, '4', '4' FROM rows;
            INSERT INTO "BudgetReservations"
                ("Id", "RunId", "BudgetPeriod", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt", "ReservedUsd", "ReconciledUsd")
            VALUES
                ('00000000000000000000000000001001', '00000000000000000000000000001001', '2026-10-25', @reserved, @at, @at, @at, '0.1234567890123456789012345678', '0.0234567890123456789012345678'),
                ('00000000000000000000000000001002', '00000000000000000000000000001002', '2026-10-24', @uncertain, @at, @at, @at, '0.2000000000000000000000000001', '0'),
                ('00000000000000000000000000001003', '00000000000000000000000000001003', '2026-10-25', @uncertain, @at, @at, @at, '0.3', '0.1'),
                ('00000000000000000000000000001004', '00000000000000000000000000001004', '2026-10-24', @reserved, @at, @at, @at, '99', '0'),
                ('00000000000000000000000000001005', '00000000000000000000000000001005', '2026-10-25', @released, @at, @at, @at, '99', '0');
            """;

        command.Parameters.AddWithValue("@reserved", (int)BudgetReservationStatus.Reserved);
        command.Parameters.AddWithValue("@uncertain", (int)BudgetReservationStatus.UncertainPaid);
        command.Parameters.AddWithValue("@reconciled", (int)BudgetReservationStatus.Reconciled);
        command.Parameters.AddWithValue("@released", (int)BudgetReservationStatus.Released);
        command.Parameters.AddWithValue("@at", "2026-10-25T00:00:00.0000000Z");

        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertOutstandingAsync(SqliteConnection connection)
    {
        // Same predicate as the service: current-period Reserved, but UncertainPaid across all periods.
        const string select =
            """
            SELECT "ReservedUsd", "ReconciledUsd"
            FROM "BudgetReservations"
            WHERE ("BudgetPeriod" = @period AND "Status" = @reserved) OR "Status" = @uncertain
            """;

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = select;
        command.Parameters.AddWithValue("@period", "2026-10-25");
        command.Parameters.AddWithValue("@reserved", (int)BudgetReservationStatus.Reserved);
        command.Parameters.AddWithValue("@uncertain", (int)BudgetReservationStatus.UncertainPaid);

        decimal total = 0m;

        int count = 0;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                total = ExactUsdText.CheckedAdd(total, checked(ExactUsdText.Read(reader, 0) - ExactUsdText.Read(reader, 1)));
                count++;
            }
        }

        Assert.Equal(3, count);
        Assert.Equal(0.5000000000000000000000000001m, total);

        command.CommandText = "EXPLAIN QUERY PLAN " + select;

        List<string> plan = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.DoesNotContain(plan, detail => detail.Contains("SCAN BudgetReservations", StringComparison.Ordinal));
        Assert.Contains(plan, detail => detail.Contains("SEARCH BudgetReservations", StringComparison.Ordinal));
    }
}
