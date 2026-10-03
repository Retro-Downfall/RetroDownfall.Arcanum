using System.Globalization;

using Microsoft.Data.Sqlite;

using SQLitePCL;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The erasure receipt's update guard and the evidence tables' own checks, on a fresh version-13
/// Grimoire.
/// </summary>
/// <remarks>
/// No production writer exists yet, so each receipt is inserted with raw SQL. The guard permits
/// exactly two changes, clearing the WAL checkpoint reason and moving Pending to Verified once no
/// reason remains; every other change it refuses with its own message, because it fires before any
/// table check can. What the guard permits can still fail the table's checks: clearing the last
/// reason while the receipt stays Pending is allowed by the guard and refused by the table, which
/// requires a Pending receipt to name a reason.
/// </remarks>
public sealed class MemoryErasureReceiptGuardTests
{
    private const string GuardMessage =
        "memory_erasure_receipts permits only clearing the WAL checkpoint reason and moving Pending to Verified once no reason remains.";

    private const string MutationId = "0F8FAD5B-D9CB-469F-A165-70867728950E";

    private static readonly (string Column, string Value)[] ReceiptColumns =
    [
        ("MutationId", "$mutationId"),
        ("StoreCode", "2"),
        ("KeyId", "randomblob(16)"),
        ("RequestDigest", "randomblob(32)"),
        ("EffectDigest", "randomblob(32)"),
        ("ErasedItemCount", "1"),
        ("RemovedRowCount", "1"),
        ("RemovedLabelCount", "0"),
        ("RemovedRetirementSuppressionCount", "0"),
        ("AuthorshipEvidenceCode", "1"),
        ("ContextEvidenceCode", "3"),
        ("EmbeddingEvidenceCode", "1"),
        ("BackupEvidenceCode", "2"),
        ("OtherExternalEvidenceCode", "3"),
        ("RetainedCopiesMask", "223"),
        ("ScrubStateCode", "$state"),
        ("ScrubPendingReasonMask", "$mask"),
    ];

    static MemoryErasureReceiptGuardTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task A_wal_only_receipt_becomes_verified_when_the_wal_reason_clears()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await OpenInstalledAsync(file);

        await InsertReceiptAsync(connection, MutationId, 1, 1);

        await UpdateReceiptAsync(connection, "ScrubStateCode = 2, ScrubPendingReasonMask = 0");

        Assert.Equal((2L, 0L), await ScrubAsync(connection));
    }

    [Fact]
    public async Task Clearing_the_wal_reason_keeps_a_non_upgradable_receipt_pending()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await OpenInstalledAsync(file);

        await InsertReceiptAsync(connection, MutationId, 1, 3);

        await UpdateReceiptAsync(connection, "ScrubPendingReasonMask = 2");

        Assert.Equal((1L, 2L), await ScrubAsync(connection));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task A_non_upgradable_reason_can_never_reach_verified(int mask)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await OpenInstalledAsync(file);

        await InsertReceiptAsync(connection, MutationId, 1, mask);

        string before = await SnapshotAsync(connection);

        await AssertRefusedAsync(
            connection,
            "ScrubStateCode = 2, ScrubPendingReasonMask = ScrubPendingReasonMask & ~1");

        Assert.Equal(before, await SnapshotAsync(connection));
    }

    [Theory]
    [InlineData(3, 7)]
    [InlineData(3, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 0)]
    public async Task A_reason_can_never_be_added_or_a_non_wal_reason_cleared(int from, int to)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await OpenInstalledAsync(file);

        await InsertReceiptAsync(connection, MutationId, 1, from);

        string before = await SnapshotAsync(connection);

        await AssertRefusedAsync(
            connection,
            $"ScrubStateCode = 1, ScrubPendingReasonMask = {to.ToString(CultureInfo.InvariantCulture)}");

        Assert.Equal(before, await SnapshotAsync(connection));
    }

    [Fact]
    public async Task Verified_never_returns_to_pending()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await OpenInstalledAsync(file);

        await InsertReceiptAsync(connection, MutationId, 2, 0);

        string before = await SnapshotAsync(connection);

        await AssertRefusedAsync(connection, "ScrubStateCode = 1, ScrubPendingReasonMask = 1");

        Assert.Equal(before, await SnapshotAsync(connection));
    }

    /// <summary>
    /// The guard permits clearing the WAL reason, so clearing the last reason while the receipt stays
    /// Pending reaches the table's own check, which is what refuses it.
    /// </summary>
    [Fact]
    public async Task Clearing_the_last_reason_without_verifying_is_refused_by_the_table_check_not_the_guard()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await OpenInstalledAsync(file);

        await InsertReceiptAsync(connection, MutationId, 1, 1);

        string before = await SnapshotAsync(connection);

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(
            () => UpdateReceiptAsync(connection, "ScrubPendingReasonMask = 0"));

        Assert.Equal(raw.SQLITE_CONSTRAINT_CHECK, error.SqliteExtendedErrorCode);

        Assert.DoesNotContain(GuardMessage, error.Message, StringComparison.Ordinal);

        Assert.Equal(before, await SnapshotAsync(connection));
    }

    [Theory]
    [InlineData("MutationId", "'7C9E6679-7425-40DE-944B-E07FC1F90AE7'")]
    [InlineData("StoreCode", "3")]
    [InlineData("KeyId", "randomblob(16)")]
    [InlineData("RequestDigest", "randomblob(32)")]
    [InlineData("EffectDigest", "randomblob(32)")]
    [InlineData("ErasedItemCount", "2")]
    [InlineData("RemovedRowCount", "2")]
    [InlineData("RemovedLabelCount", "1")]
    [InlineData("RemovedRetirementSuppressionCount", "1")]
    [InlineData("AuthorshipEvidenceCode", "2")]
    [InlineData("ContextEvidenceCode", "1")]
    [InlineData("EmbeddingEvidenceCode", "2")]
    [InlineData("BackupEvidenceCode", "3")]
    [InlineData("OtherExternalEvidenceCode", "4")]
    [InlineData("RetainedCopiesMask", "1")]
    public async Task Every_other_column_is_immutable(string column, string value)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await OpenInstalledAsync(file);

        await InsertReceiptAsync(connection, MutationId, 1, 1);

        string before = await SnapshotAsync(connection);

        await AssertRefusedAsync(connection, $"{column} = {value}");

        Assert.Equal(before, await SnapshotAsync(connection));
    }

    [Theory]
    [InlineData("fingerprint of 31 bytes")]
    [InlineData("fingerprint store 4")]
    [InlineData("fingerprint key of 15 bytes")]
    [InlineData("receipt store 4")]
    [InlineData("receipt key of 15 bytes")]
    [InlineData("lowercase mutation")]
    [InlineData("undashed mutation")]
    [InlineData("mutation without the dash at position 9")]
    [InlineData("mutation without the dash at position 14")]
    [InlineData("mutation without the dash at position 19")]
    [InlineData("mutation without the dash at position 24")]
    [InlineData("request digest of 31 bytes")]
    [InlineData("effect digest of 31 bytes")]
    [InlineData("no erased item")]
    [InlineData("no removed row")]
    [InlineData("negative removed label count")]
    [InlineData("negative removed retirement suppression count")]
    [InlineData("negative retained copies mask")]
    [InlineData("evidence code 5")]
    [InlineData("context evidence code 5")]
    [InlineData("embedding evidence code 5")]
    [InlineData("backup evidence code 5")]
    [InlineData("other external evidence code 5")]
    [InlineData("verified with a reason")]
    [InlineData("pending with no reason")]
    [InlineData("unknown reason bit")]
    [InlineData("subject of 31 bytes")]
    public async Task Table_checks_refuse_malformed_rows(string shape)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await OpenInstalledAsync(file);

        (string table, string sql) = MalformedRow(shape);

        if (table == "memory_erasure_receipt_subjects")
        {
            await InsertReceiptAsync(connection, MutationId, 1, 1);
        }

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(connection, sql, MutationId, 1, 1));

        Assert.Equal(raw.SQLITE_CONSTRAINT_CHECK, error.SqliteExtendedErrorCode);

        Assert.Equal(0L, await ScalarAsync(connection, $"SELECT count(*) FROM {table};"));
    }

    [Fact]
    public async Task Deleting_a_receipt_cascades_to_its_subjects_and_fingerprints_have_no_delete_guard()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await OpenInstalledAsync(file);

        await InsertReceiptAsync(connection, MutationId, 1, 1);

        await ExecuteAsync(connection, SubjectInsertSql("randomblob(32)"), MutationId, 1, 1);

        await ExecuteAsync(connection, SubjectInsertSql("randomblob(32)"), MutationId, 1, 1);

        await ExecuteAsync(connection, FingerprintInsertSql(), MutationId, 1, 1);

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT count(*) FROM memory_erasure_receipt_subjects;"));

        await ExecuteAsync(
            connection,
            "DELETE FROM memory_erasure_receipts WHERE MutationId = $mutationId;",
            MutationId,
            1,
            1);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM memory_erasure_receipt_subjects;"));

        await ExecuteAsync(connection, "DELETE FROM memory_erasure_fingerprints;", MutationId, 1, 1);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM memory_erasure_fingerprints;"));
    }

    private static async Task<SqliteConnection> OpenInstalledAsync(EvolutionScratchDatabase file)
    {
        SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        try
        {
            GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
                connection, 1536, CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

            // The cascade below is only evidence if the connection enforces foreign keys at all.
            Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA foreign_keys;"));

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();

            throw;
        }
    }

    private static Task InsertReceiptAsync(SqliteConnection connection, string mutationId, int state, int mask) =>
        ExecuteAsync(connection, ReceiptInsertSql(), mutationId, state, mask);

    private static Task UpdateReceiptAsync(SqliteConnection connection, string assignments) =>
        ExecuteAsync(
            connection,
            $"UPDATE memory_erasure_receipts SET {assignments} WHERE MutationId = $mutationId;",
            MutationId,
            1,
            1);

    private static async Task AssertRefusedAsync(SqliteConnection connection, string assignments)
    {
        SqliteException error = await Assert.ThrowsAsync<SqliteException>(
            () => UpdateReceiptAsync(connection, assignments));

        Assert.Contains(GuardMessage, error.Message, StringComparison.Ordinal);
    }

    private static (string Table, string Sql) MalformedRow(string shape) =>
        shape switch
        {
            "fingerprint of 31 bytes" => ("memory_erasure_fingerprints", FingerprintInsertSql(fingerprint: "randomblob(31)")),

            "fingerprint store 4" => ("memory_erasure_fingerprints", FingerprintInsertSql(storeCode: "4")),

            "fingerprint key of 15 bytes" => ("memory_erasure_fingerprints", FingerprintInsertSql(keyId: "randomblob(15)")),

            "receipt store 4" => ("memory_erasure_receipts", ReceiptInsertSql(("StoreCode", "4"))),

            "receipt key of 15 bytes" => ("memory_erasure_receipts", ReceiptInsertSql(("KeyId", "randomblob(15)"))),

            "lowercase mutation" => (
                "memory_erasure_receipts",
                ReceiptInsertSql(("MutationId", "'0f8fad5b-d9cb-469f-a165-70867728950e'"))),

            "undashed mutation" => (
                "memory_erasure_receipts",
                ReceiptInsertSql(("MutationId", "'0F8FAD5BD9CB469FA16570867728950E'"))),

            // A thirty-six character upper-case identifier that is dashed everywhere but at one position,
            // so the length and case checks pass and only that position's check can refuse it.
            "mutation without the dash at position 9" => (
                "memory_erasure_receipts",
                ReceiptInsertSql(("MutationId", "'0F8FAD5BAD9CB-469F-A165-70867728950E'"))),

            "mutation without the dash at position 14" => (
                "memory_erasure_receipts",
                ReceiptInsertSql(("MutationId", "'0F8FAD5B-D9CBA469F-A165-70867728950E'"))),

            "mutation without the dash at position 19" => (
                "memory_erasure_receipts",
                ReceiptInsertSql(("MutationId", "'0F8FAD5B-D9CB-469FAA165-70867728950E'"))),

            "mutation without the dash at position 24" => (
                "memory_erasure_receipts",
                ReceiptInsertSql(("MutationId", "'0F8FAD5B-D9CB-469F-A165A70867728950E'"))),

            "request digest of 31 bytes" => ("memory_erasure_receipts", ReceiptInsertSql(("RequestDigest", "randomblob(31)"))),

            "effect digest of 31 bytes" => ("memory_erasure_receipts", ReceiptInsertSql(("EffectDigest", "randomblob(31)"))),

            "no erased item" => ("memory_erasure_receipts", ReceiptInsertSql(("ErasedItemCount", "0"))),

            "no removed row" => ("memory_erasure_receipts", ReceiptInsertSql(("RemovedRowCount", "0"))),

            "negative removed label count" => ("memory_erasure_receipts", ReceiptInsertSql(("RemovedLabelCount", "-1"))),

            "negative removed retirement suppression count" => (
                "memory_erasure_receipts",
                ReceiptInsertSql(("RemovedRetirementSuppressionCount", "-1"))),

            "negative retained copies mask" => ("memory_erasure_receipts", ReceiptInsertSql(("RetainedCopiesMask", "-1"))),

            "evidence code 5" => ("memory_erasure_receipts", ReceiptInsertSql(("AuthorshipEvidenceCode", "5"))),

            "context evidence code 5" => ("memory_erasure_receipts", ReceiptInsertSql(("ContextEvidenceCode", "5"))),

            "embedding evidence code 5" => ("memory_erasure_receipts", ReceiptInsertSql(("EmbeddingEvidenceCode", "5"))),

            "backup evidence code 5" => ("memory_erasure_receipts", ReceiptInsertSql(("BackupEvidenceCode", "5"))),

            "other external evidence code 5" => ("memory_erasure_receipts", ReceiptInsertSql(("OtherExternalEvidenceCode", "5"))),

            "verified with a reason" => (
                "memory_erasure_receipts",
                ReceiptInsertSql(("ScrubStateCode", "2"), ("ScrubPendingReasonMask", "1"))),

            "pending with no reason" => (
                "memory_erasure_receipts",
                ReceiptInsertSql(("ScrubStateCode", "1"), ("ScrubPendingReasonMask", "0"))),

            "unknown reason bit" => ("memory_erasure_receipts", ReceiptInsertSql(("ScrubPendingReasonMask", "8"))),

            "subject of 31 bytes" => ("memory_erasure_receipt_subjects", SubjectInsertSql("randomblob(31)")),

            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown malformed row shape."),
        };

    /// <summary>The receipt insert with every column at its valid default except the ones replaced.</summary>
    private static string ReceiptInsertSql(params (string Column, string Value)[] replacements)
    {
        Dictionary<string, string> values = ReceiptColumns.ToDictionary(
            static item => item.Column,
            static item => item.Value,
            StringComparer.Ordinal);

        foreach ((string column, string value) in replacements)
        {
            Assert.True(values.ContainsKey(column), $"{column} is not a receipt column.");

            values[column] = value;
        }

        return $"""
            INSERT INTO memory_erasure_receipts ({string.Join(", ", ReceiptColumns.Select(static item => item.Column))})
            VALUES ({string.Join(", ", ReceiptColumns.Select(item => values[item.Column]))});
            """;
    }

    private static string FingerprintInsertSql(
        string fingerprint = "randomblob(32)",
        string storeCode = "2",
        string keyId = "randomblob(16)") =>
        $"INSERT INTO memory_erasure_fingerprints (Fingerprint, StoreCode, KeyId) VALUES ({fingerprint}, {storeCode}, {keyId});";

    private static string SubjectInsertSql(string digest) =>
        $"INSERT INTO memory_erasure_receipt_subjects (MutationId, SubjectDigest) VALUES ($mutationId, {digest});";

    private static async Task<(long State, long Mask)> ScrubAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT ScrubStateCode, ScrubPendingReasonMask FROM memory_erasure_receipts;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Every column of every receipt, so a refused update can be proven to have changed nothing.</summary>
    private static async Task<string> SnapshotAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT * FROM memory_erasure_receipts ORDER BY MutationId;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> values = [];

        while (await reader.ReadAsync())
        {
            for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                object value = reader.GetValue(ordinal);

                values.Add(value is byte[] bytes
                    ? Convert.ToHexString(bytes)
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }

        Assert.Equal(17, values.Count);

        return string.Join('|', values);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        string mutationId,
        int state,
        int mask)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = command.Parameters.AddWithValue("$mutationId", mutationId);

        _ = command.Parameters.AddWithValue("$state", state);

        _ = command.Parameters.AddWithValue("$mask", mask);

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? result = await command.ExecuteScalarAsync();

        return result is DBNull ? null : result;
    }
}
