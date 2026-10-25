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
            await GrimoireSchemaTestInstaller.InstallAsync(connection, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);
        }

        await AssertOutstandingAsync(connection);

        using var freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(fresh, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);

        Assert.Equal(await ReadCatalogAsync(fresh), await ReadCatalogAsync(connection));
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
