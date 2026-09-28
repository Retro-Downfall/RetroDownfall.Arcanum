using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class MemoryReviewSchemaEvolutionTests
{
    static MemoryReviewSchemaEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task Version_eleven_upgrade_seeds_only_the_current_head_and_future_heads_append_events()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, CoreSchemaVersionElevenFixture.ChainSet(), 1536, CancellationToken.None);

        Assert.Equal(11, installed.Core.SchemaVersion);

        await ExecuteAsync(connection, SeedHistorySql);

        await ReviewSchemaEvolutionHarness.UpgradeAsync(connection);

        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM annal_review_events;"));
        Assert.Equal("annal-v2", await ScalarStringAsync(connection, "SELECT VersionId FROM annal_review_events;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM annal_review_decision_receipts;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM annal_review_markers;"));
        Assert.Equal(
            "BLOB|1",
            await ScalarStringAsync(
                connection,
                "SELECT type || '|' || \"notnull\" FROM pragma_table_info('annal_review_markers') WHERE name = 'MarkerGeneration';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO annal_review_markers
                (SubjectStoreCode, ScopeKindCode, CampaignId, MarkerGeneration, ReviewedThroughSequence, Revision)
            VALUES (2, 1, NULL, zeroblob(16), 0, 1);
            """));
        Assert.DoesNotContain(
            "Content",
            await ScalarStringAsync(
                connection,
                "SELECT group_concat(sql, ' ') FROM sqlite_schema WHERE name LIKE 'annal_review_%';"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "AUTOINCREMENT",
            await ScalarStringAsync(
                connection,
                "SELECT sql FROM sqlite_schema WHERE name = 'annal_review_events';"),
            StringComparison.Ordinal);

        await ExecuteAsync(connection, FutureHeadSql);

        Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT count(*) FROM annal_review_events;"));
        Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT max(Sequence) FROM annal_review_events;"));
    }

    private const string SeedHistorySql =
        """
        INSERT INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
        VALUES ('annal-claim', 1, 'memory', '2026-01-01T00:00:00.0000000Z');
        INSERT INTO annal_versions
            (VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, SensitivityCode,
             ContentHash, ValidFromUtc, RecordedAtUtc, ContentHashFormatCode, PredecessorVersionId)
        VALUES
            ('annal-v1', 'annal-claim', 1, 1, 4, 1, 0, zeroblob(32),
             '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', 2, NULL),
            ('annal-v2', 'annal-claim', 2, 2, 1, 1, 0, zeroblob(32),
             '2026-01-02T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z', 2, 'annal-v1');
        INSERT INTO annal_heads
            (ClaimId, SubjectStoreCode, CurrentVersionId, CurrentRevision, CurrentOperationCode, UpdatedAtUtc)
        VALUES ('annal-claim', 1, 'annal-v2', 2, 2, '2026-01-02T00:00:00.0000000Z');
        """;

    private const string FutureHeadSql =
        """
        INSERT INTO annal_versions
            (VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, SensitivityCode,
             ContentHash, ValidFromUtc, RecordedAtUtc, ContentHashFormatCode, PredecessorVersionId)
        VALUES ('annal-v3', 'annal-claim', 3, 2, 1, 1, 0, zeroblob(32),
                '2026-01-03T00:00:00.0000000Z', '2026-01-03T00:00:00.0000000Z', 2, 'annal-v2');
        UPDATE annal_heads
        SET CurrentVersionId = 'annal-v3', CurrentRevision = 3, CurrentOperationCode = 2,
            UpdatedAtUtc = '2026-01-03T00:00:00.0000000Z'
        WHERE ClaimId = 'annal-claim';
        """;

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException());
    }
}
