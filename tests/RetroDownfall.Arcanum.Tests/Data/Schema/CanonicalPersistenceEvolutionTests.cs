using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Core 9 and Covenant canonical 4 converge inherited instants and authoritative money without an
/// unbounded migration transaction.
/// </summary>
public sealed class CanonicalPersistenceEvolutionTests
{
    static CanonicalPersistenceEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task Core_version_eight_evolves_all_authoritative_USD_columns_and_UTC_instants()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult seeded = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionEightFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(8, seeded.Core.SchemaVersion);

        string sessionId = "A0000000-0000-4000-8000-000000000256";

        string runId = "B0000000000040008000000000000256";

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "Status", "CreatedAt", "UpdatedAt", "LastSummarizedMessageAt")
            VALUES ($sessionId, 'active', '2026-09-08T23:59:59.1234567-04:00',
                    '2026-09-09 03:59:59.1234567', NULL);

            INSERT INTO "InferenceRuns"
                ("Id", "RequestId", "Surface", "Purpose", "StartedAt", "Status")
            VALUES ($runId, 'utc-evolution', 'test', 'test',
                    '2026-09-08T23:00:00-04:00', 1);

            INSERT INTO "BillableOperations"
                ("Id", "RunId", "OperationType", "Provider", "Model", "Purpose", "StartedAt",
                 "CompletedAt", "InputTokens", "OutputTokens", "ReasoningTokens", "CachedTokens",
                 "PricingSnapshotJson", "ActualCostUsd", "Status")
            VALUES ('billable', $runId, 1, 'test', 'test', 'test',
                    '2026-09-08T23:00:00-04:00', '2026-09-08T23:01:00-04:00',
                    1, 1, 0, 0, '{}', 1.0e-8, 1);

            INSERT INTO "BudgetReservations"
                ("Id", "RunId", "BudgetPeriod", "ReservedUsd", "ReconciledUsd", "Status",
                 "ExpiresAt", "CreatedAt", "UpdatedAt")
            VALUES ('reservation', $runId, '2026-09-09', 0.00000002, 0.00000001, 1,
                    '2026-09-09 05:00:00', '2026-09-09 03:00:00', '2026-09-09 03:00:00');

            INSERT INTO "CostAdjustments"
                ("Id", "BillableOperationId", "RunId", "AmountUsd", "Reason", "CreatedAt")
            VALUES ('adjustment', 'billable', $runId, -0.00000001, 'test', '2026-09-09 03:00:00');

            INSERT INTO "BudgetAlerts"
                ("Id", "Threshold", "AlertedAt", "SpendUsd", "DailyLimitUsd")
            VALUES ('alert', 80, '2026-09-09 03:00:00', 0.00000001, 1000000.00000001);
            """,
            ("$sessionId", sessionId),
            ("$runId", runId));

        GrimoireSchemaInstallResult evolved = await EvolveAsync(
            connection,
            GrimoireSchemaTransactionTier.Core);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.Core.Health);

        Assert.Equal(9, evolved.Core.SchemaVersion);

        Assert.Equal(
            "2026-09-09T03:59:59.1234567Z",
            await ScalarStringAsync(
                connection,
                "SELECT CreatedAt FROM Sessions WHERE Id = $id;",
                ("$id", sessionId)));

        Assert.Equal(
            "2026-09-09T03:59:59.1234567Z",
            await ScalarStringAsync(
                connection,
                "SELECT UpdatedAt FROM Sessions WHERE Id = $id;",
                ("$id", sessionId)));

        foreach ((string table, string column) in AuthoritativeUsdColumns())
        {
            Assert.Equal(
                "TEXT",
                await ScalarStringAsync(
                    connection,
                    $"SELECT type FROM pragma_table_info('{table}') WHERE name = '{column}';"));
        }

        Assert.Equal(
            0.00000001m,
            ExactUsdText.Parse(await ScalarStringAsync(
                connection,
                "SELECT ActualCostUsd FROM BillableOperations WHERE Id = 'billable';")));

        Assert.Equal(
            0.00000002m,
            ExactUsdText.Parse(await ScalarStringAsync(
                connection,
                "SELECT ReservedUsd FROM BudgetReservations WHERE Id = 'reservation';")));

        Assert.Equal(
            0.00000001m,
            ExactUsdText.Parse(await ScalarStringAsync(
                connection,
                "SELECT ReconciledUsd FROM BudgetReservations WHERE Id = 'reservation';")));

        Assert.Equal(
            -0.00000001m,
            ExactUsdText.Parse(await ScalarStringAsync(
                connection,
                "SELECT AmountUsd FROM CostAdjustments WHERE Id = 'adjustment';")));

        Assert.Equal(
            0.00000001m,
            ExactUsdText.Parse(await ScalarStringAsync(
                connection,
                "SELECT SpendUsd FROM BudgetAlerts WHERE Id = 'alert';")));

        Assert.Equal(
            1000000.00000001m,
            ExactUsdText.Parse(await ScalarStringAsync(
                connection,
                "SELECT DailyLimitUsd FROM BudgetAlerts WHERE Id = 'alert';")));

        Assert.Equal(
            "text|text|text|text|text|text",
            await ScalarStringAsync(
                connection,
                """
                SELECT typeof(b.ActualCostUsd)
                    || '|' || typeof(r.ReservedUsd)
                    || '|' || typeof(r.ReconciledUsd)
                    || '|' || typeof(a.AmountUsd)
                    || '|' || typeof(l.SpendUsd)
                    || '|' || typeof(l.DailyLimitUsd)
                FROM BillableOperations b, BudgetReservations r, CostAdjustments a, BudgetAlerts l
                WHERE b.Id = 'billable' AND r.Id = 'reservation'
                    AND a.Id = 'adjustment' AND l.Id = 'alert';
                """));

        _ = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO BudgetAlerts (Id, Threshold, AlertedAt, SpendUsd, DailyLimitUsd)
                VALUES ('invalid-money-type', 90, '2026-09-10T00:00:00.0000000Z', X'01', '1');
                """));

        Assert.Equal(
            0L,
            await ScalarInt64Async(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));

        Assert.Equal(
            "InferenceRuns|RunId|Id|CASCADE",
            await ScalarStringAsync(
                connection,
                """
                SELECT "table" || '|' || "from" || '|' || "to" || '|' || on_delete
                FROM pragma_foreign_key_list('BillableOperations');
                """));

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'IX_BillableOperations_CompletedAt';"));

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'IX_BudgetAlerts_Threshold_Date';"));

        string plan = await ScalarStringAsync(
            connection,
            "EXPLAIN QUERY PLAN SELECT Id FROM Sessions WHERE CreatedAt >= $from ORDER BY CreatedAt;",
            ("$from", "2026-09-09T00:00:00.0000000Z"),
            ordinal: 3);

        Assert.Contains("IX_Sessions_CreatedAt", plan, StringComparison.Ordinal);

        string accountingPlan = await ScalarStringAsync(
            connection,
            """
            EXPLAIN QUERY PLAN
            SELECT ActualCostUsd
            FROM BillableOperations
            WHERE CompletedAt >= $from AND CompletedAt < $through;
            """,
            ("$from", "2026-09-09T00:00:00.0000000Z"),
            ("$through", "2026-09-10T00:00:00.0000000Z"),
            ordinal: 3);

        Assert.Contains("IX_BillableOperations_CompletedAt", accountingPlan, StringComparison.Ordinal);

        string[] evolvedDefinitions = await ReadUsdObjectDefinitionsAsync(connection);

        using EvolutionScratchDatabase freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection freshConnection = await freshFile.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult fresh = await GrimoireSchemaTestInstaller.InstallAsync(
            freshConnection,
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, fresh.Core.Health);

        Assert.Equal(evolvedDefinitions, await ReadUsdObjectDefinitionsAsync(freshConnection));
    }

    [Fact]
    public async Task Covenant_version_three_backfill_repairs_append_only_rows_and_restores_the_guard()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult seeded = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CovenantCanonicalSchemaVersionThreeFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(3, seeded.CovenantCanonical.SchemaVersion);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO covenant_entries
                (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
            VALUES ('entry', 1, NULL, 'Key', 'key', '2026-09-08T23:00:00.0000009-04:00');
            """);

        GrimoireSchemaInstallResult evolved = await EvolveAsync(
            connection,
            GrimoireSchemaTransactionTier.CovenantCanonical);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.CovenantCanonical.Health);

        Assert.Equal(4, evolved.CovenantCanonical.SchemaVersion);

        Assert.Equal(
            "2026-09-09T03:00:00.0000009Z",
            await ScalarStringAsync(
                connection,
                "SELECT CreatedAtUtc FROM covenant_entries WHERE EntryId = 'entry';"));

        SqliteException guard = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                "UPDATE covenant_entries SET CreatedAtUtc = CreatedAtUtc WHERE EntryId = 'entry';"));

        Assert.Contains("append-only", guard.Message, StringComparison.Ordinal);
    }

    private static async Task<GrimoireSchemaInstallResult> EvolveAsync(
        SqliteConnection connection,
        GrimoireSchemaTransactionTier tier)
    {
        GrimoireSchemaVersionChainSet target =
            tier is GrimoireSchemaTransactionTier.Core
                ? CoreSchemaVersionNineFixture.ChainSet()
                : GrimoireSchemaVersionChains.Default;

        GrimoireSchemaInstallResult staged = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            target,
            1536,
            CancellationToken.None);

        GrimoireSchemaTierInstallResult stagedTier = tier switch
        {
            GrimoireSchemaTransactionTier.Core => staged.Core,

            GrimoireSchemaTransactionTier.CovenantCanonical => staged.CovenantCanonical,

            _ => staged.CovenantAccelerator,
        };

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, stagedTier.Health);

        GrimoireSchemaTransitionJournalRow journal =
            await GrimoireSchemaTransitionJournal.ReadAsync(
                connection,
                transaction: null,
                tier,
                CancellationToken.None)
            ?? throw new InvalidOperationException("Expected a pending schema transition journal.");

        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create(target);

        GrimoireSchemaBackfillProgress progress = await new GrimoireSchemaBackfillRunner(
                installer,
                TimeProvider.System)
            .AdvanceAsync(
                connection,
                target.ForTier(tier),
                journal,
                GrimoireSchemaTestInstaller.CreateContext(),
                maxBatches: 128,
                CancellationToken.None);

        GrimoireSchemaInspectionResult inspection = await new GrimoireSchemaManifestInspector(
                GrimoireSchemaTierOwnershipRegistry.CreateDefault())
            .InspectAsync(
                connection,
                transaction: null,
                target.ForTier(tier).HeadManifest,
                CancellationToken.None);

        Assert.True(
            progress.StepComplete,
            $"The evolved catalog did not converge: {inspection.Failure} {inspection.ObjectName}.");

        Assert.True(
            inspection.IsValid,
            $"The completed evolution left catalog drift: {inspection.Failure} {inspection.ObjectName}.");

        return await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            target,
            1536,
            CancellationToken.None);
    }

    private static (string Table, string Column)[] AuthoritativeUsdColumns() =>
    [
        ("BillableOperations", "ActualCostUsd"),
        ("BudgetReservations", "ReservedUsd"),
        ("BudgetReservations", "ReconciledUsd"),
        ("CostAdjustments", "AmountUsd"),
        ("BudgetAlerts", "SpendUsd"),
        ("BudgetAlerts", "DailyLimitUsd"),
    ];

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        AddParameters(command, parameters);

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        (string Name, object Value) parameter = default,
        int ordinal = 0)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        if (parameter.Name is not null)
        {
            AddParameters(command, [parameter]);
        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return reader.GetString(ordinal);
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        (string Name, object Value) firstParameter,
        (string Name, object Value) secondParameter,
        int ordinal)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        AddParameters(command, [firstParameter, secondParameter]);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return reader.GetString(ordinal);
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string[]> ReadUsdObjectDefinitionsAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT type, name, sql
            FROM sqlite_schema
            WHERE tbl_name IN (
                'BillableOperations',
                'BudgetReservations',
                'CostAdjustments',
                'BudgetAlerts')
              AND sql IS NOT NULL
            ORDER BY type, name;
            """;

        List<string> definitions = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            definitions.Add(
                reader.GetString(0)
                + "|"
                + reader.GetString(1)
                + "|"
                + GrimoireSqlNormalizer.Normalize(reader.GetString(2)));
        }

        return [.. definitions];
    }

    private static void AddParameters(
        SqliteCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }
    }
}
