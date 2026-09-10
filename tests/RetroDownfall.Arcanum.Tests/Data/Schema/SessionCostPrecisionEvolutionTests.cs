using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The version-8 correction that aligns Sessions.TotalCostUsd with EF's exact decimal TEXT mapping.
/// </summary>
public sealed class SessionCostPrecisionEvolutionTests
{
    static SessionCostPrecisionEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task Version_seven_upgrade_preserves_dependents_and_moves_cost_to_exact_text()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult seeded = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionSevenFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, seeded.Core.Health);
        Assert.Equal(7, seeded.Core.SchemaVersion);

        Guid sessionId = new("A0000000-0000-4000-8000-000000000256");

        await SeedSessionAndEntryAsync(connection, sessionId);

        List<string> schemaBefore = await ReadUnchangedSchemaAsync(connection);
        List<string> foreignKeysBefore = await ReadForeignKeysAsync(connection);

        Assert.Equal("NUMERIC", await ReadCostColumnTypeAsync(connection));

        GrimoireSchemaInstallResult evolved = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionEightFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.Core.Health);
        Assert.Equal(8, evolved.Core.SchemaVersion);
        Assert.Equal("TEXT", await ReadCostColumnTypeAsync(connection));
        Assert.Equal("text", await ScalarStringAsync(
            connection,
            "SELECT typeof(\"TotalCostUsd\") FROM \"Sessions\" WHERE \"Id\" = $sessionId;",
            ("$sessionId", Format(sessionId))));
        Assert.Equal("'12.3456789'", await ScalarStringAsync(
            connection,
            "SELECT quote(\"TotalCostUsd\") FROM \"Sessions\" WHERE \"Id\" = $sessionId;",
            ("$sessionId", Format(sessionId))));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM \"Entries\";"));
        Assert.Equal(0L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(schemaBefore, await ReadUnchangedSchemaAsync(connection));
        Assert.Equal(foreignKeysBefore, await ReadForeignKeysAsync(connection));

        await ExecuteAsync(
            connection,
            "UPDATE \"Sessions\" SET \"TotalCostUsd\" = $cost WHERE \"Id\" = $sessionId;",
            ("$cost", 1_234_567_890.12345678m),
            ("$sessionId", Format(sessionId)));

        Assert.Equal("text", await ScalarStringAsync(
            connection,
            "SELECT typeof(\"TotalCostUsd\") FROM \"Sessions\" WHERE \"Id\" = $sessionId;",
            ("$sessionId", Format(sessionId))));
        Assert.Equal("'1234567890.12345678'", await ScalarStringAsync(
            connection,
            "SELECT quote(\"TotalCostUsd\") FROM \"Sessions\" WHERE \"Id\" = $sessionId;",
            ("$sessionId", Format(sessionId))));

        GrimoireSchemaInstallResult converged = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionEightFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, converged.Core.Health);
        Assert.Equal(8, converged.Core.SchemaVersion);
        Assert.Equal(schemaBefore, await ReadUnchangedSchemaAsync(connection));
    }

    [Fact]
    public async Task Version_eight_statement_failure_rolls_back_schema_data_and_version()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionSevenFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Guid sessionId = new("B0000000-0000-4000-8000-000000000256");

        await SeedSessionAndEntryAsync(connection, sessionId);

        List<string> schemaBefore = await ReadWholeSchemaAsync(connection);

        GrimoireSchemaVersionChainSet failing = VersionEightChainWithInterruption(
            new GrimoireSchemaTransitionStatement(
                "Transitions.V8.035_injected_failure",
                35,
                "injected_failure",
                "SELECT MissingColumn FROM \"Sessions\";"));

        _ = await Assert.ThrowsAsync<SqliteException>(
            () => GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                failing,
                1536,
                CancellationToken.None));

        await AssertVersionSevenStateAsync(connection, sessionId, schemaBefore);
    }

    [Fact]
    public async Task Version_eight_cancellation_rolls_back_schema_data_and_version()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionSevenFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Guid sessionId = new("C0000000-0000-4000-8000-000000000256");

        await SeedSessionAndEntryAsync(connection, sessionId);

        List<string> schemaBefore = await ReadWholeSchemaAsync(connection);

        using CancellationTokenSource cancellation = new();

        connection.CreateFunction(
            "cancel_schema_transition",
            () =>
            {
                cancellation.Cancel();

                return 0L;
            });

        GrimoireSchemaVersionChainSet cancelling = VersionEightChainWithInterruption(
            new GrimoireSchemaTransitionStatement(
                "Transitions.V8.035_injected_cancellation",
                35,
                "injected_cancellation",
                "SELECT cancel_schema_transition();"));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                cancelling,
                1536,
                cancellation.Token));

        await AssertVersionSevenStateAsync(connection, sessionId, schemaBefore);
    }

    private static GrimoireSchemaVersionChainSet VersionEightChainWithInterruption(
        GrimoireSchemaTransitionStatement interruption)
    {
        GrimoireSchemaVersionChain core =
            CoreSchemaVersionEightFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.Core);

        GrimoireSchemaVersionStep versionEight = Assert.Single(
            core.Steps,
            static step => step.ToVersion == 8);

        GrimoireSchemaVersionStep interrupted = versionEight with
        {
            Statements =
            [
                .. versionEight.Statements.Take(3),
                interruption,
                .. versionEight.Statements.Skip(3),
            ],
        };

        GrimoireSchemaVersionChain interruptedCore = new(
            core.HeadManifest,
            core.HeadObjects,
            [
                .. core.Steps.Where(static step => step.ToVersion < 8),
                interrupted,
            ]);

        return new GrimoireSchemaVersionChainSet(
        [
            interruptedCore,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
    }

    private static async Task AssertVersionSevenStateAsync(
        SqliteConnection connection,
        Guid sessionId,
        List<string> schemaBefore)
    {
        Assert.Equal(schemaBefore, await ReadWholeSchemaAsync(connection));
        Assert.Equal("NUMERIC", await ReadCostColumnTypeAsync(connection));
        Assert.Equal(7L, await ScalarInt64Async(
            connection,
            """
            SELECT SchemaVersion
            FROM grimoire_feature_schemas
            WHERE FamilyCode = 0 AND TransactionTierCode = 0;
            """));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM \"Entries\";"));
        Assert.Equal(0L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(12.3456789m, await ScalarDecimalAsync(
            connection,
            "SELECT \"TotalCostUsd\" FROM \"Sessions\" WHERE \"Id\" = $sessionId;",
            ("$sessionId", Format(sessionId))));
    }

    private static async Task SeedSessionAndEntryAsync(SqliteConnection connection, Guid sessionId)
    {
        const string timestamp = "2026-08-08 12:34:56.1234567+00:00";

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" (
                "Id", "CreatedAt", "UpdatedAt", "TotalCostUsd")
            VALUES ($sessionId, $timestamp, $timestamp, $cost);
            """,
            ("$sessionId", Format(sessionId)),
            ("$timestamp", timestamp),
            ("$cost", 12.3456789m));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Entries" (
                "Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
            VALUES ($entryId, $sessionId, 1, 'prompt', 'test-model', $timestamp, 1);
            """,
            ("$entryId", Guid.NewGuid().ToString("D").ToUpperInvariant()),
            ("$sessionId", Format(sessionId)),
            ("$timestamp", timestamp));
    }

    private static Task<List<string>> ReadUnchangedSchemaAsync(SqliteConnection connection) =>
        ReadRowsAsync(
            connection,
            """
            SELECT type, name, tbl_name, coalesce(sql, '')
            FROM sqlite_master
            WHERE NOT (type = 'table' AND name = 'Sessions')
            ORDER BY type, name;
            """);

    private static Task<List<string>> ReadWholeSchemaAsync(SqliteConnection connection) =>
        ReadRowsAsync(
            connection,
            """
            SELECT type, name, tbl_name, coalesce(sql, '')
            FROM sqlite_master
            ORDER BY type, name;
            """);

    private static Task<List<string>> ReadForeignKeysAsync(SqliteConnection connection) =>
        ReadRowsAsync(
            connection,
            """
            SELECT owner.name, foreign_key.id, foreign_key.seq, foreign_key."table",
                   foreign_key."from", foreign_key."to", foreign_key.on_update,
                   foreign_key.on_delete, foreign_key.match
            FROM sqlite_master AS owner
            JOIN pragma_foreign_key_list(owner.name) AS foreign_key
            WHERE owner.type = 'table'
            ORDER BY owner.name, foreign_key.id, foreign_key.seq;
            """);

    private static async Task<List<string>> ReadRowsAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        List<string> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            string[] values = new string[reader.FieldCount];

            for (int index = 0; index < reader.FieldCount; index++)
            {
                values[index] = Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "<null>";
            }

            rows.Add(string.Join("\u001F", values));
        }

        return rows;
    }

    private static async Task<string> ReadCostColumnTypeAsync(SqliteConnection connection) =>
        await ScalarStringAsync(
            connection,
            "SELECT type FROM pragma_table_info('Sessions') WHERE name = 'TotalCostUsd';");

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<object> ScalarAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Expected a scalar value.");
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters) =>
        Convert.ToString(
            await ScalarAsync(connection, sql, parameters),
            CultureInfo.InvariantCulture)!;

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(
            await ScalarAsync(connection, sql, parameters),
            CultureInfo.InvariantCulture);

    private static async Task<decimal> ScalarDecimalAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters) =>
        Convert.ToDecimal(
            await ScalarAsync(connection, sql, parameters),
            CultureInfo.InvariantCulture);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();
}
