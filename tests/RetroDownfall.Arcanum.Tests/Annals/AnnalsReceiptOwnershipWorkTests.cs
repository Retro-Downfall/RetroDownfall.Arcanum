using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Annals;

public sealed class AnnalsReceiptOwnershipWorkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Positive_orphan_MemoryId_selection_seeks_without_reading_unrelated_input_history(bool protectedInputs)
    {
        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        string table = protectedInputs ? "long_rest_protected_receipt_inputs" : "long_rest_receipt_inputs";

        await database.InstallCoreObjectsAsync([table], CancellationToken.None);

        SqliteConnection connection = database.Connection;

        await using SqliteCommand seed = connection.CreateCommand();

        seed.CommandText = "PRAGMA foreign_keys = OFF;";

        await seed.ExecuteNonQueryAsync();

        await SeedAsync(connection, table, protectedInputs, 32);

        (long earlySteps, string[] early) = await ReadPositiveOrphansAsync(connection, table);

        await SeedAsync(connection, table, protectedInputs, 8192);

        (long lateSteps, string[] late) = await ReadPositiveOrphansAsync(connection, table);

        Assert.Equal(early, late);

        Assert.Equal(2, late.Length);

        Console.WriteLine($"Actual SQLCipher positive MemoryId lookup: table={table}; unrelated32VM={earlySteps}; unrelated8192VM={lateSteps}; selected={late.Length}.");

        Assert.True(lateSteps <= earlySteps + 500,
            $"Positive orphan ownership lookup scanned unrelated inputs: {earlySteps}->{lateSteps} native VM steps.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Isolated_MemoryId_covering_seek_preserves_the_exact_owned_receipt_set(bool protectedInputs)
    {
        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        string table = protectedInputs ? "long_rest_protected_receipt_inputs" : "long_rest_receipt_inputs";

        await database.InstallCoreObjectsAsync([table], CancellationToken.None);

        await database.ExecuteAsync("DROP INDEX idx_" + table + "_memory_receipt", CancellationToken.None);

        SqliteConnection connection = database.Connection;

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA foreign_keys = OFF;";

        await command.ExecuteNonQueryAsync();

        await SeedAsync(connection, table, protectedInputs, 8192);

        (long before, string[] expected) = await ReadPositiveOrphansAsync(connection, table);

        command.CommandText = $"CREATE INDEX rv217_trial_memory_receipt ON {table}(lower(replace(MemoryId, '-', '')), ReceiptId, MemoryId);";

        await command.ExecuteNonQueryAsync();

        (long after, string[] actual) = await ReadPositiveOrphansAsync(connection, table);

        Assert.Equal(expected, actual);

        Assert.True(after < before / 16);

        Console.WriteLine($"Isolated MemoryId/ReceiptId index: table={table}; unrelated8192; VM={before}->{after}; sameSelected={actual.Length}.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Shared_expression_index_matches_existing_normalized_authority_and_preserves_exact_collision(bool protectedInputs, bool guid)
    {
        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        string table = protectedInputs ? "long_rest_protected_receipt_inputs" : "long_rest_receipt_inputs";

        await database.InstallCoreObjectsAsync([table], CancellationToken.None);

        await database.ExecuteAsync("DROP INDEX idx_" + table + "_memory_receipt", CancellationToken.None);

        SqliteConnection connection = database.Connection;

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA foreign_keys = OFF;";

        await command.ExecuteNonQueryAsync();

        await SeedAsync(connection, table, protectedInputs, 8192);

        string target = guid ? "abcdef01-2345-4abc-9def-0123456789ab" : "MiXeD-Key";

        string variant = guid ? "aBcDeF0123454aBc9dEf0123456789aB" : "mixedkey";

        command.CommandText = $"UPDATE {table} SET MemoryId=$memory WHERE ReceiptId=printf('%032X',$row);";

        command.Parameters.AddWithValue("$memory", target);

        command.Parameters.AddWithValue("$row", 1);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.Parameters["$memory"].Value = variant;

        command.Parameters["$row"].Value = 2;

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        (long exactBefore, string[] exact) = await ReadPositiveOrphansAsync(connection, table, target);

        (long normalizedBefore, string[] normalizedIds) = await ReadPositiveOrphansAsync(connection, table, target, normalized: true);

        Assert.Single(exact);

        Assert.Equal(2, normalizedIds.Length);

        command.Parameters.Clear();

        command.CommandText = $"CREATE INDEX rv217_trial_expression ON {table}(lower(replace(MemoryId, '-', '')), ReceiptId, MemoryId);";

        await command.ExecuteNonQueryAsync();

        (long exactAfter, string[] exactAfterIds) = await ReadPositiveOrphansAsync(connection, table, target);

        (long normalizedAfter, string[] normalizedAfterIds) = await ReadPositiveOrphansAsync(connection, table, target, normalized: true);

        Assert.Equal(exact, exactAfterIds);

        Assert.Equal(normalizedIds, normalizedAfterIds);

        Assert.True(exactAfter < exactBefore / 16);

        Assert.True(normalizedAfter < normalizedBefore / 16);

        Console.WriteLine($"Native shared expression index: protected={protectedInputs}; guid={guid}; exactVM={exactBefore}->{exactAfter}; normalizedVM={normalizedBefore}->{normalizedAfter}; exactRows={exact.Length}; existingNormalizedRows={normalizedIds.Length}; unrelated history retained.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_shared_receipt_selector_keeps_work_independent_of_unrelated_inputs(bool normalized)
    {
        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCoreObjectsAsync(["annal_claims", "annal_versions", "long_rest_receipts",
            "long_rest_receipt_inputs", "long_rest_protected_receipt_inputs"], CancellationToken.None);

        await database.ExecuteAsync("PRAGMA foreign_keys=OFF;", CancellationToken.None);

        AnnalsReceiptSelection selection = AnnalsErasurePlan.ForSubjectQuery(AnnalSubjectStore.Saga, "SELECT $memory",
            normalized ? "$key" : null).First(static step => step.Receipts is not null).Receipts!;

        foreach (int count in new[] { 32, 8192 })
        {
            await SeedAsync(database.Connection, "long_rest_receipt_inputs", false, count);

            await SeedAsync(database.Connection, "long_rest_protected_receipt_inputs", true, count);

            long steps = 0;

            raw.sqlite3_progress_handler(database.Connection.Handle, 1, (object _) => ++steps > 100000 ? 1 : 0, null);

            try
            {
                await using SqliteCommand command = database.Connection.CreateCommand();

                command.CommandText = selection.Query;

                command.Parameters.AddWithValue("$memory", "selected");

                command.Parameters.AddWithValue("$key", "selected");

                List<string> receipts = [];

                await using SqliteDataReader reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    receipts.Add(reader.GetString(0));
                }

                Assert.Equal(2, receipts.Count);

                Assert.True(steps < 1000, $"The complete closed selector scanned unrelated receipt history: {steps} VM.");

                Console.WriteLine($"Actual shared receipt selection: normalized={normalized}; unrelatedInputsPerFamily={count}; selected=2; nativeVM={steps}; closedAuthority=true.");
            }
            finally
            {
                raw.sqlite3_progress_handler(database.Connection.Handle, 0, null, null);
            }
        }
    }

    private static async Task SeedAsync(SqliteConnection connection, string table, bool protectedInputs, int count)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"DELETE FROM {table};";

        await command.ExecuteNonQueryAsync();

        string columns = protectedInputs
            ? "ReceiptId,Ordinal,MemoryId,ClaimId,VersionId,LabelId,LabelDigest,SagaRevision,SagaContentDigest,SensitivityDigest,LabelJson"
            : "ReceiptId,Ordinal,MemoryId,VersionId,ContentHashFormatCode,ContentHash,SnapshotHash,SnapshotJson";

        string values = protectedInputs
            ? "printf('%032X',n),1,CASE WHEN n<=2 THEN 'selected' ELSE 'unrelated' END,'claim',printf('%032X',n),'label',zeroblob(32),1,zeroblob(32),zeroblob(32),'{}'"
            : "printf('%032X',n),1,CASE WHEN n<=2 THEN 'selected' ELSE 'unrelated' END,printf('%032X',n),1,zeroblob(32),printf('%064X',n),'{}'";

        command.CommandText = $"WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n<$count) INSERT INTO {table}({columns}) SELECT {values} FROM rows;";

        command.Parameters.AddWithValue("$count", count + 2);

        Assert.Equal(count + 2, await command.ExecuteNonQueryAsync());
    }

    private static async Task<(long Steps, string[] Ids)> ReadPositiveOrphansAsync(SqliteConnection connection, string table, string memory = "selected", bool normalized = false)
    {
        long steps = 0;

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => ++steps > 1_000_000 ? 1 : 0, null);

        try
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"SELECT ReceiptId FROM {table} WHERE " + CovenantIdentitySql.Keyed("MemoryId", "$key")
                + (normalized ? "" : " AND MemoryId = $memory") + " ORDER BY ReceiptId;";

            command.Parameters.AddWithValue("$memory", memory);

            command.Parameters.AddWithValue("$key", CovenantIdentitySql.Key(memory));

            List<string> ids = [];

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(timeout.Token);

            while (await reader.ReadAsync(timeout.Token))
            {
                ids.Add(reader.GetString(0));
            }

            return (steps, [.. ids]);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }
    }
}
