using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class CovenantReviewSchemaEvolutionTests
{
    static CovenantReviewSchemaEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task Version_four_upgrade_seeds_only_the_current_head_and_future_heads_append_events()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, CovenantCanonicalSchemaVersionFourFixture.ChainSet(), 1536, CancellationToken.None);

        Assert.Equal(4, installed.CovenantCanonical.SchemaVersion);

        await ExecuteAsync(connection, SeedHistorySql);

        await ReviewSchemaEvolutionHarness.UpgradeAsync(connection);

        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM covenant_review_events;"));
        Assert.Equal("covenant-v2", await ScalarStringAsync(connection, "SELECT VersionId FROM covenant_review_events;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM covenant_review_markers;"));
        Assert.DoesNotContain(
            "Content",
            await ScalarStringAsync(
                connection,
                "SELECT group_concat(sql, ' ') FROM sqlite_schema WHERE name LIKE 'covenant_review_%';"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "AUTOINCREMENT",
            await ScalarStringAsync(
                connection,
                "SELECT sql FROM sqlite_schema WHERE name = 'covenant_review_events';"),
            StringComparison.Ordinal);

        await ExecuteAsync(connection, FutureHeadSql);

        Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT count(*) FROM covenant_review_events;"));
        Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT max(Sequence) FROM covenant_review_events;"));
    }

    private const string SeedHistorySql =
        """
        UPDATE covenant_state SET NextSearchRowId = 10 WHERE StateKey = 1;
        INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
        VALUES ('covenant-entry', 2, 'campaign', 'key', 'key', '2026-01-01T00:00:00.0000000Z');
        INSERT INTO covenant_versions (
            VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
            AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
            RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
            AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
            RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
            AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
        VALUES
            ('covenant-v1', 'covenant-entry', 1, 1, 1, 'one', 'one', randomblob(32), randomblob(32),
             3, 3, 1, 1, 1, NULL, NULL, NULL, NULL, NULL, NULL, 'mutation-v1', randomblob(32),
             randomblob(32), randomblob(32), NULL, 0, randomblob(32), '2026-01-01T00:00:00.0000000Z'),
            ('covenant-v2', 'covenant-entry', 1, 2, 1, 'two', 'two', randomblob(32), randomblob(32),
             3, 3, 1, 1, 1, NULL, NULL, NULL, NULL, NULL, NULL, 'mutation-v2', randomblob(32),
             randomblob(32), randomblob(32), 'covenant-v1', 0, randomblob(32), '2026-01-02T00:00:00.0000000Z');
        INSERT INTO covenant_heads (
            EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode,
            ScopeCode, CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc)
        VALUES ('covenant-entry', 1, 'covenant-v2', 2, 1, 2, 'campaign', 'key', 3, 1, 1,
                '2026-01-02T00:00:00.0000000Z');
        """;

    private const string FutureHeadSql =
        """
        INSERT INTO covenant_versions (
            VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
            AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
            RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
            AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
            RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
            AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
        VALUES ('covenant-v3', 'covenant-entry', 1, 3, 1, 'three', 'three', randomblob(32), randomblob(32),
                5, 3, 1, 1, 1, NULL, NULL, NULL, NULL, NULL, NULL, 'mutation-v3', randomblob(32),
                randomblob(32), randomblob(32), 'covenant-v2', 0, randomblob(32),
                '2026-01-03T00:00:00.0000000Z');
        UPDATE covenant_heads
        SET CurrentVersionId = 'covenant-v3', CurrentLaneRevision = 3, CompiledByteCost = 5,
            UpdatedAtUtc = '2026-01-03T00:00:00.0000000Z'
        WHERE EntryId = 'covenant-entry' AND LaneCode = 1;
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
