using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Version 3 rebuilds Covenant's append-only version ledger without translating its existing evidence.
/// </summary>
public sealed class CovenantUngatedRetirementEvolutionTests
{

    private const string Timestamp = "2026-08-30T00:00:00.0000000+00:00";

    private const string HistoricalWardDigest =
        "X'0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20'";

    private const string HistoricalContentHash =
        "X'202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F'";

    private static readonly string[] RebuiltIndexNames =
    [
        "idx_covenant_heads_campaign_active",
        "idx_covenant_heads_campaign_cleanup",
        "idx_covenant_heads_global_active",
        "idx_covenant_version_attachment_provenance_attachment",
        "idx_covenant_versions_entry_created",
        "idx_covenant_versions_source_turn",
        "ux_covenant_heads_current_version",
        "ux_covenant_heads_search_row",
        "ux_covenant_versions_entry_lane_revision",
        "ux_covenant_versions_head_candidate",
        "ux_covenant_versions_mutation",
    ];

    private static readonly string[] RebuiltTriggerNames =
    [
        "covenant_heads_key_epoch_delete",
        "covenant_heads_key_epoch_insert",
        "covenant_heads_key_epoch_update",
        "covenant_heads_validate_insert",
        "covenant_heads_validate_update",
        "covenant_version_attachment_provenance_guard_delete",
        "covenant_version_attachment_provenance_guard_update",
        "covenant_versions_guard_delete",
        "covenant_versions_guard_update",
    ];

    [Fact]
    public void The_shipped_chain_pins_the_fingerprint_the_version_two_tree_published()
    {

        GrimoireSchemaVersionChain canonical = GrimoireSchemaVersionChains.Default
            .ForTier(GrimoireSchemaTransactionTier.CovenantCanonical);

        Assert.Equal(
            CovenantCanonicalSchemaVersionTwoFixture.Fingerprint,
            canonical.SourceDefinitionFingerprintFor(2));

    }

    [Fact]
    public async Task Evolving_version_two_preserves_historical_Covenant_rows_and_their_relationships()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CovenantCanonicalSchemaVersionTwoFixture.ChainSet(),
            1536,
            CancellationToken.None);

        await SeedVersionTwoRowsAsync(connection);

        GrimoireSchemaInstallResult evolved = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.CovenantCanonical.Health);

        Assert.Equal(3, evolved.CovenantCanonical.SchemaVersion);

        Assert.Equal(
            "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20",
            await ScalarStringAsync(
                connection,
                "SELECT hex(WardReceiptDigest) FROM covenant_versions WHERE VersionId = 'historical-one';"));

        Assert.Equal(
            2L,
            await ScalarLongAsync(
                connection,
                "SELECT AuthorizationModeCode FROM covenant_versions WHERE VersionId = 'historical-one';"));

        Assert.Equal(
            "historical-one",
            await ScalarStringAsync(
                connection,
                "SELECT PredecessorVersionId FROM covenant_versions WHERE VersionId = 'historical-two';"));

        Assert.Equal(
            "historical-two",
            await ScalarStringAsync(connection, "SELECT CurrentVersionId FROM covenant_heads WHERE EntryId = 'historical-entry';"));

        Assert.Equal(
            1L,
            await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM covenant_version_attachment_provenance WHERE VersionId = 'historical-one';"));

        Assert.Equal(
            new CovenantHeadSnapshot(
                "historical-entry",
                1,
                "historical-two",
                2,
                1,
                2,
                "historical-campaign",
                "historical.key",
                8,
                3,
                1,
                Timestamp),
            await ReadHeadAsync(connection, "historical-entry", 1));

        Assert.Equal(
            new AttachmentProvenanceSnapshot(
                "historical-one",
                0,
                "attachment",
                "version",
                "proof.txt",
                "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F",
                1,
                null,
                null,
                "turn",
                null),
            await ReadAttachmentProvenanceAsync(connection, "historical-one", 0));

        Assert.Equal(
            1L,
            await ScalarLongAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM covenant_heads AS head
                JOIN covenant_versions AS successor
                    ON successor.VersionId = head.CurrentVersionId
                    AND successor.EntryId = head.EntryId
                    AND successor.LaneCode = head.LaneCode
                    AND successor.LaneRevision = head.CurrentLaneRevision
                    AND successor.OperationCode = head.CurrentOperationCode
                JOIN covenant_versions AS predecessor
                    ON predecessor.VersionId = successor.PredecessorVersionId
                JOIN covenant_version_attachment_provenance AS provenance
                    ON provenance.VersionId = predecessor.VersionId
                WHERE head.EntryId = 'historical-entry'
                    AND predecessor.VersionId = 'historical-one'
                    AND successor.VersionId = 'historical-two'
                    AND provenance.Ordinal = 0;
                """));

        Assert.Equal(
            1L,
            await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM covenant_versions WHERE VersionId = 'ordinary-one' AND OriginCode = 1 AND WardReceiptDigest IS NULL AND AuthorizationModeCode IS NULL;"));

        Assert.Equal(0L, await CountRowsAsync(connection, "PRAGMA foreign_key_check;"));

        Assert.Equal(
            RebuiltIndexNames,
            await ReadNamesAsync(
                connection,
                "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL AND tbl_name IN ('covenant_versions', 'covenant_heads', 'covenant_version_attachment_provenance') ORDER BY name;"));

        Assert.Equal(
            RebuiltTriggerNames,
            await ReadNamesAsync(
                connection,
                "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name IN ('covenant_versions', 'covenant_heads', 'covenant_version_attachment_provenance') ORDER BY name;"));

        SqliteException deleteDenied = await AssertSqliteExceptionAsync(
            connection,
            "DELETE FROM covenant_versions WHERE VersionId = 'historical-one';");

        Assert.Contains("authorized cleanup scope", deleteDenied.Message, StringComparison.Ordinal);

        SqliteException updateDenied = await AssertSqliteExceptionAsync(
            connection,
            "UPDATE covenant_versions SET CompiledByteCost = 1 WHERE VersionId = 'historical-one';");

        Assert.Contains("append-only", updateDenied.Message, StringComparison.Ordinal);

        SqliteException insertDenied = await AssertSqliteExceptionAsync(
            connection,
            """
            INSERT INTO covenant_heads (
                EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode,
                ScopeCode, CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc)
            VALUES ('ordinary-entry', 1, 'ordinary-one', 1, 1, 2, 'ordinary-campaign', 'ordinary.key', 1, 1, 3, '2026-08-30T00:00:00.0000000+00:00');
            """);

        Assert.Contains("compiled byte cost", insertDenied.Message, StringComparison.Ordinal);

        SqliteException headUpdateDenied = await AssertSqliteExceptionAsync(
            connection,
            "UPDATE covenant_heads SET OriginCode = 1 WHERE EntryId = 'historical-entry' AND LaneCode = 1;");

        Assert.Contains("origin of its current version", headUpdateDenied.Message, StringComparison.Ordinal);

        await ExecuteAsync(
            connection,
            ReceiptFreeAgentApprovedRetirement("receipt-free-evolved", "ordinary-entry", laneRevision: 2, predecessor: "ordinary-one"));

        Assert.Equal(
            1L,
            await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM covenant_versions WHERE VersionId = 'receipt-free-evolved' AND OriginCode = 3 AND WardReceiptDigest IS NULL AND AuthorizationModeCode IS NULL;"));

    }

    [Fact]
    public async Task Evolved_version_three_enforces_the_complete_Ward_tuple_matrix()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CovenantCanonicalSchemaVersionTwoFixture.ChainSet(),
            1536,
            CancellationToken.None);

        GrimoireSchemaInstallResult evolved = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.CovenantCanonical.Health);

        await AssertWardTupleMatrixAsync(connection);

    }

    [Fact]
    public async Task Evolved_and_fresh_version_three_databases_have_the_same_complete_canonical_catalog()
    {

        IReadOnlyDictionary<string, string> evolved = await CanonicalDefinitionsAsync(evolve: true);

        IReadOnlyDictionary<string, string> fresh = await CanonicalDefinitionsAsync(evolve: false);

        Assert.Equal(fresh.Keys.OrderBy(static name => name, StringComparer.Ordinal), evolved.Keys.OrderBy(static name => name, StringComparer.Ordinal));

        foreach ((string name, string definition) in fresh)
        {

            Assert.Equal(GrimoireSqlNormalizer.Normalize(definition), GrimoireSqlNormalizer.Normalize(evolved[name]));

        }

        Assert.Contains("covenant_heads_validate_insert", evolved.Keys);

        Assert.Contains("covenant_heads_validate_update", evolved.Keys);

        Assert.Contains("covenant_version_attachment_provenance", evolved.Keys);

    }

    private static async Task<IReadOnlyDictionary<string, string>> CanonicalDefinitionsAsync(bool evolve)
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (evolve)
        {

            _ = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                CovenantCanonicalSchemaVersionTwoFixture.ChainSet(),
                1536,
                CancellationToken.None);

        }

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            1536,
            CancellationToken.None);

        Dictionary<string, string> definitions = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT name, sql FROM sqlite_master WHERE name LIKE 'covenant_%' AND sql IS NOT NULL ORDER BY name;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            definitions[reader.GetString(0)] = reader.GetString(1);

        }

        return definitions;

    }

    private static async Task SeedVersionTwoRowsAsync(SqliteConnection connection)
    {

        await ExecuteAsync(connection, "UPDATE covenant_state SET NextSearchRowId = 10 WHERE StateKey = 1;");

        await ExecuteAsync(
            connection,
            "INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc) VALUES ('historical-entry', 2, 'historical-campaign', 'historical.key', 'historical.key', '2026-08-30T00:00:00.0000000+00:00');");

        await ExecuteAsync(connection, HistoricalAgentApprovedVersion("historical-one", "historical-entry", 1, null));

        await ExecuteAsync(connection, HistoricalAgentApprovedVersion("historical-two", "historical-entry", 2, "historical-one"));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO covenant_heads (
                EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode,
                ScopeCode, CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc)
            VALUES ('historical-entry', 1, 'historical-two', 2, 1, 2, 'historical-campaign', 'historical.key', 8, 3, 1, '2026-08-30T00:00:00.0000000+00:00');
            """);

        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO covenant_version_attachment_provenance (
                VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
                SourceRangeKindCode, SourceStart, SourceEnd, SourceTurnId, MaterializationReference)
            VALUES ('historical-one', 0, 'attachment', 'version', 'proof.txt', {HistoricalContentHash}, 1, NULL, NULL, 'turn', NULL);
            """);

        await ExecuteAsync(
            connection,
            "INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc) VALUES ('ordinary-entry', 2, 'ordinary-campaign', 'ordinary.key', 'ordinary.key', '2026-08-30T00:00:00.0000000+00:00');");

        await ExecuteAsync(connection, OrdinaryVersion("ordinary-one", "ordinary-entry"));

    }

    private static string HistoricalAgentApprovedVersion(string versionId, string entryId, int revision, string? predecessor) =>
        $"""
        INSERT INTO covenant_versions (
            VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
            AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
            RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
            AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
            RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
            AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
        VALUES (
            '{versionId}', '{entryId}', 1, {revision}, 1, 'historical authored', 'historical compiled',
            randomblob(32), randomblob(32), 8, 3, 1, 1, 3, 'turn-{revision}', 'tool-{revision}', randomblob(32),
            NULL, {HistoricalWardDigest}, 2, 'mutation-{versionId}', randomblob(32), randomblob(32), randomblob(32),
            {(predecessor is null ? "NULL" : $"'{predecessor}'")}, 1, randomblob(32), '{Timestamp}');
        """;

    private static string OrdinaryVersion(string versionId, string entryId) =>
        $"""
        INSERT INTO covenant_versions (
            VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
            AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
            RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
            AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
            RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
            AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
        VALUES (
            '{versionId}', '{entryId}', 1, 1, 1, 'ordinary authored', 'ordinary compiled',
            randomblob(32), randomblob(32), 8, 3, 1, 1, 1, NULL, NULL, NULL, NULL, NULL, NULL,
            'mutation-{versionId}', randomblob(32), randomblob(32), randomblob(32), NULL, 0, randomblob(32), '{Timestamp}');
        """;

    private static string ReceiptFreeAgentApprovedRetirement(
        string versionId,
        string entryId,
        int laneRevision,
        string predecessor) =>
        $"""
        INSERT INTO covenant_versions (
            VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
            AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
            RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
            AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
            RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
            AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
        VALUES (
            '{versionId}', '{entryId}', 1, {laneRevision}, 2, NULL, NULL, NULL, NULL, 0, 0, 1, 1, 3,
            'retire-turn', 'retire-tool', randomblob(32), NULL, NULL, NULL, 'mutation-{versionId}',
            randomblob(32), randomblob(32), randomblob(32), '{predecessor}', 0, randomblob(32), '{Timestamp}');
        """;

    private static async Task AssertWardTupleMatrixAsync(SqliteConnection connection)
    {

        (long Origin, string WardDigest, string AuthorizationMode, bool Accepted)[] cases =
        [
            (3, "NULL", "NULL", true),
            (3, HistoricalWardDigest, "2", true),
            (3, HistoricalWardDigest, "3", true),
            (3, HistoricalWardDigest, "1", false),
            (3, HistoricalWardDigest, "NULL", false),
            (3, "NULL", "2", false),
            (1, HistoricalWardDigest, "NULL", false),
            (1, "NULL", "2", false),
            (2, HistoricalWardDigest, "NULL", false),
            (2, "NULL", "3", false),
        ];

        for (int index = 0; index < cases.Length; index++)
        {

            (long origin, string wardDigest, string authorizationMode, bool accepted) = cases[index];

            string entryId = $"matrix-entry-{index}";

            string versionId = $"matrix-version-{index}";

            await ExecuteAsync(
                connection,
                $"INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc) VALUES ('{entryId}', 2, 'matrix-campaign', 'matrix.key.{index}', 'matrix.key.{index}', '{Timestamp}');");

            string sql = VersionWithWardTuple(
                versionId,
                entryId,
                origin,
                wardDigest,
                authorizationMode);

            if (accepted)
            {

                await ExecuteAsync(connection, sql);

            }
            else
            {

                _ = await AssertSqliteExceptionAsync(connection, sql);

            }

            Assert.Equal(
                accepted ? 1L : 0L,
                await ScalarLongAsync(
                    connection,
                    $"SELECT COUNT(*) FROM covenant_versions WHERE VersionId = '{versionId}';"));

        }

    }

    private static string VersionWithWardTuple(
        string versionId,
        string entryId,
        long origin,
        string wardDigest,
        string authorizationMode)
    {

        long laneCode = origin == 2 ? 2 : 1;

        string sourceTurnId = origin == 1 ? "NULL" : "'matrix-turn'";

        string sourceToolCallId = origin == 1 ? "NULL" : "'matrix-tool'";

        string basePlanDigest = origin == 1 ? "NULL" : "randomblob(32)";

        return
            $"""
            INSERT INTO covenant_versions (
                VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
                AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
                RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
                AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
                RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
                AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
            VALUES (
                '{versionId}', '{entryId}', {laneCode}, 1, 2, NULL, NULL, NULL, NULL, 0, 0, 1, 1, {origin},
                {sourceTurnId}, {sourceToolCallId}, {basePlanDigest}, NULL, {wardDigest}, {authorizationMode},
                'mutation-{versionId}', randomblob(32), randomblob(32), randomblob(32), NULL, 0, randomblob(32), '{Timestamp}');
            """;

    }

    private static async Task<CovenantHeadSnapshot> ReadHeadAsync(
        SqliteConnection connection,
        string entryId,
        long laneCode)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode,
                ScopeCode, CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc
            FROM covenant_heads
            WHERE EntryId = $entryId AND LaneCode = $laneCode;
            """;

        _ = command.Parameters.AddWithValue("$entryId", entryId);

        _ = command.Parameters.AddWithValue("$laneCode", laneCode);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return new CovenantHeadSnapshot(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetString(11));

    }

    private static async Task<AttachmentProvenanceSnapshot> ReadAttachmentProvenanceAsync(
        SqliteConnection connection,
        string versionId,
        long ordinal)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, hex(ContentHash),
                SourceRangeKindCode, SourceStart, SourceEnd, SourceTurnId, MaterializationReference
            FROM covenant_version_attachment_provenance
            WHERE VersionId = $versionId AND Ordinal = $ordinal;
            """;

        _ = command.Parameters.AddWithValue("$versionId", versionId);

        _ = command.Parameters.AddWithValue("$ordinal", ordinal);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return new AttachmentProvenanceSnapshot(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));

    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static async Task<SqliteException> AssertSqliteExceptionAsync(SqliteConnection connection, string sql) =>
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql));

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(CancellationToken.None);

        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture);

    }

    private static async Task<long> CountRowsAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        long count = 0;

        while (await reader.ReadAsync(CancellationToken.None))
        {

            count++;

        }

        return count;

    }

    private static async Task<string[]> ReadNamesAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        List<string> names = [];

        while (await reader.ReadAsync(CancellationToken.None))
        {

            names.Add(reader.GetString(0));

        }

        return [.. names];

    }

    private sealed record CovenantHeadSnapshot(
        string EntryId,
        long LaneCode,
        string CurrentVersionId,
        long CurrentLaneRevision,
        long CurrentOperationCode,
        long ScopeCode,
        string? CampaignId,
        string NormalizedKey,
        long CompiledByteCost,
        long OriginCode,
        long SearchRowId,
        string UpdatedAtUtc);

    private sealed record AttachmentProvenanceSnapshot(
        string VersionId,
        long Ordinal,
        string AttachmentId,
        string AttachmentVersionIdentity,
        string LogicalKey,
        string ContentHash,
        long SourceRangeKindCode,
        long? SourceStart,
        long? SourceEnd,
        string? SourceTurnId,
        string? MaterializationReference);

}
