using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestSchemaEvolutionTests
{
    private const string At = "2026-10-09T00:00:00.0000000Z";

    private static readonly string InputHash = new('A', 64);

    private static readonly string OutputHash = new('B', 64);

    [Fact]
    public async Task A_fresh_installation_accepts_content_free_long_rest_receipts()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_receipts;"));

        await ReceiptAsync(scratch.Connection, "no-change", applied: false);

        Assert.Equal(1L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_receipts WHERE OutcomeCode = 2 AND SurvivorVersionId IS NULL;"));
    }

    [Fact]
    public async Task An_input_preserves_its_bounded_immutable_snapshot_for_historical_replay()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "source");

        await ReceiptAsync(scratch.Connection, "r", applied: false);

        const string insert = "INSERT INTO long_rest_receipt_inputs (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson) VALUES ('r', 1, 'source', 'source-v1', 1, zeroblob(32), $hash, $snapshot);";

        await ExecuteAsync(scratch.Connection, insert, ("$hash", InputHash), ("$snapshot", "{}"));

        Assert.Equal("{}", await ScalarAsync(scratch.Connection, "SELECT SnapshotJson FROM long_rest_receipt_inputs;"));

        await ExecuteAsync(scratch.Connection, "DELETE FROM long_rest_receipt_inputs;");

        foreach (object invalid in new object[] { DBNull.Value, "", "a", new string('a', 1048577) })
        {
            SqliteException refused = await Assert.ThrowsAsync<SqliteException>(
                () => ExecuteAsync(scratch.Connection, insert, ("$hash", InputHash), ("$snapshot", invalid)));

            Assert.Equal(19, refused.SqliteErrorCode);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_no_change_receipt_preserves_the_requested_exact_survivor_until_erasure(bool foreignKeys)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "requested");

        await ExecuteAsync(scratch.Connection,
            "INSERT INTO long_rest_receipts (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash, RequestedSurvivorVersionId, CreatedAtUtc) VALUES ('r', 1, 3, 2, 1, $input, $output, 'requested-v1', $at);",
            ("$input", InputHash), ("$output", OutputHash), ("$at", At));

        Assert.Equal("requested-v1", await ScalarAsync(scratch.Connection, "SELECT RequestedSurvivorVersionId FROM long_rest_receipts;"));

        if (!foreignKeys)
        {
            await ExecuteAsync(scratch.Connection, "PRAGMA foreign_keys = OFF;");
        }

        await using SqliteTransaction transaction = scratch.Connection.BeginTransaction();

        await AnnalsClaimWriter.DeleteClaimsForSubjectAsync(
            scratch.Connection, transaction, AnnalSubjectStore.Saga, "requested", CancellationToken.None);

        await transaction.CommitAsync();

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_receipts;"));
    }

    [Fact]
    public async Task Version_sixteen_upgrades_without_rewriting_existing_memory_or_claims()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult old = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, CoreSchemaVersionSixteenFixture.ChainSet(), 64, CancellationToken.None);

        Assert.Equal(16, old.Core.SchemaVersion);

        await MemoryAsync(connection, "inherited");

        string[] before = await RowsAsync(connection,
            "SELECT m.Content || '|' || v.VersionId || '|' || v.OriginCode || '|' || hex(v.ContentHash) FROM saga_memories m JOIN annal_claims c ON c.SubjectId = m.Id JOIN annal_versions v ON v.ClaimId = c.ClaimId;");

        GrimoireSchemaInstallResult upgraded = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, upgraded.Core.Health);

        Assert.Equal(17, upgraded.Core.SchemaVersion);

        Assert.Equal(before, await RowsAsync(connection,
            "SELECT m.Content || '|' || v.VersionId || '|' || v.OriginCode || '|' || hex(v.ContentHash) FROM saga_memories m JOIN annal_claims c ON c.SubjectId = m.Id JOIN annal_versions v ON v.ClaimId = c.ClaimId;"));

        await ReceiptAsync(connection, "after-upgrade", applied: false);

        await using Scratch fresh = await Scratch.StartAsync();

        Assert.Equal(await NewDefinitionsAsync(fresh.Connection), await NewDefinitionsAsync(connection));
    }

    [Fact]
    public void The_version_sixteen_source_pin_recognizes_the_published_tree()
    {
        Assert.Equal("C29DE2AB1E2D134F8F2C1D989F908127CBAE8B1EF417BAE149ABE8FC1BEC4E5C", CoreSchemaVersionSixteenFixture.Fingerprint);

        Assert.Equal(CoreSchemaVersionSixteenFixture.Fingerprint,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).SourceDefinitionFingerprintFor(16));

        Assert.Equal(CoreSchemaVersionFifteenFixture.PublishedFingerprint, CoreSchemaVersionFifteenFixture.Fingerprint);
    }

    [Theory]
    [InlineData("long_rest_receipts", "ReasonCode = 1")]
    [InlineData("long_rest_receipt_inputs", "SnapshotHash = 'CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC'")]
    [InlineData("long_rest_suppressions", "ReceiptId = 'r'")]
    public async Task Durable_transformations_refuse_updates_but_allow_subject_erasure(string table, string assignment)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await SeedTransformationAsync(scratch.Connection);

        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(scratch.Connection, $"UPDATE {table} SET {assignment};"));

        Assert.Equal(19, refused.SqliteErrorCode);

        await ExecuteAsync(scratch.Connection, "DELETE FROM long_rest_receipts WHERE ReceiptId = 'r';");

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_receipt_inputs;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(2L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM annal_versions;"));
    }

    [Theory]
    [InlineData("pinned-source")]
    [InlineData("pinned-survivor")]
    [InlineData("retired-source")]
    [InlineData("retired-survivor")]
    [InlineData("other-scope")]
    [InlineData("other-store")]
    [InlineData("sensitive")]
    [InlineData("sensitivity-ledger")]
    [InlineData("stale-source")]
    [InlineData("stale-survivor")]
    [InlineData("not-input")]
    [InlineData("wrong-receipt-output")]
    [InlineData("unembedded-survivor")]
    [InlineData("different-exact-hash")]
    [InlineData("different-exact-format")]
    public async Task A_suppression_refuses_an_unproven_or_protected_exact_version(string defect)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "source", store: defect == "other-store" ? 2 : 1,
            scope: defect == "other-scope" ? 3 : 1, sensitivity: defect == "sensitive" ? 1 : 0,
            format: defect == "different-exact-format" ? 2 : 1,
            contentHash: defect == "different-exact-hash" ? Enumerable.Repeat((byte)1, 32).ToArray() : null);

        await MemoryAsync(scratch.Connection, "survivor");

        await MemoryAsync(scratch.Connection, "other");

        if (defect.StartsWith("pinned-", StringComparison.Ordinal))
        {
            await ExecuteAsync(scratch.Connection, "UPDATE saga_memories SET PinnedAtUtc = $at WHERE Id = $id;",
                ("$at", At), ("$id", defect[7..]));
        }

        if (defect.StartsWith("retired-", StringComparison.Ordinal))
        {
            await ExecuteAsync(scratch.Connection, "UPDATE saga_memories SET RetiredAtUtc = $at WHERE Id = $id;",
                ("$at", At), ("$id", defect[8..]));
        }

        if (defect.StartsWith("unembedded-", StringComparison.Ordinal))
        {
            await ExecuteAsync(scratch.Connection, "DELETE FROM saga_memory_embeddings WHERE MemoryId = $id;",
                ("$id", defect[11..]));
        }

        if (defect.StartsWith("stale-", StringComparison.Ordinal))
        {
            string stale = defect[6..];

            await ExecuteAsync(scratch.Connection,
                "INSERT INTO annal_versions (VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, SensitivityCode, ContentHash, ValidFromUtc, RecordedAtUtc, PredecessorVersionId) VALUES ($version, $claim, 2, 2, 1, 1, 0, zeroblob(32), $at, $at, $predecessor); UPDATE annal_heads SET CurrentVersionId = $version, CurrentRevision = 2, CurrentOperationCode = 2 WHERE ClaimId = $claim;",
                ("$at", At), ("$version", stale + "-v2"), ("$claim", stale + "-claim"), ("$predecessor", stale + "-v1"));
        }

        if (defect == "sensitivity-ledger")
        {
            await ExecuteAsync(scratch.Connection,
                "INSERT INTO artifact_sensitivity (LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode, ExactGenerationIds, ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ArtifactLabelDigest, CreatedAtUtc) VALUES ('label', 6, 'SOURCE', 1, 1, zeroblob(16), 1, zeroblob(32), zeroblob(32), zeroblob(32), $at);",
                ("$at", At));
        }

        await ReceiptAsync(scratch.Connection, "r", applied: true,
            survivor: defect == "wrong-receipt-output" ? "other" : "survivor");

        if (defect != "not-input")
        {
            await InputAsync(scratch.Connection, "r", "source", 1);
        }

        await InputAsync(scratch.Connection, "r", "survivor", 2);

        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(
            () => SuppressionAsync(scratch.Connection, "source", "survivor", "r"));

        Assert.Equal(19, refused.SqliteErrorCode);

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_suppressions;"));
    }

    [Fact]
    public async Task Suppression_cannot_form_a_chain_or_eliminate_a_previous_output()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await SeedTransformationAsync(scratch.Connection);

        await MemoryAsync(scratch.Connection, "other");

        await ReceiptAsync(scratch.Connection, "toward-source", applied: true, survivor: "source");

        await InputAsync(scratch.Connection, "toward-source", "other", 1);

        await InputAsync(scratch.Connection, "toward-source", "source", 2);

        await Assert.ThrowsAsync<SqliteException>(() => SuppressionAsync(scratch.Connection, "other", "source", "toward-source"));

        await ReceiptAsync(scratch.Connection, "away-from-survivor", applied: true, survivor: "other");

        await InputAsync(scratch.Connection, "away-from-survivor", "survivor", 1);

        await InputAsync(scratch.Connection, "away-from-survivor", "other", 2);

        await Assert.ThrowsAsync<SqliteException>(() => SuppressionAsync(scratch.Connection, "survivor", "other", "away-from-survivor"));

        Assert.Equal(1L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_suppressions;"));
    }

    [Theory]
    [InlineData(true, "source", true)]
    [InlineData(true, "survivor", true)]
    [InlineData(false, "source", true)]
    [InlineData(false, "survivor", true)]
    [InlineData(true, "source", false)]
    [InlineData(false, "source", false)]
    public async Task Erasing_any_endpoint_removes_the_whole_receipt_and_preserves_unrelated_history(
        bool foreignKeys, string erased, bool applied)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await SeedTransformationAsync(scratch.Connection, applied);

        await MemoryAsync(scratch.Connection, "other");

        await ReceiptAsync(scratch.Connection, "unrelated", applied: false);

        await InputAsync(scratch.Connection, "unrelated", "other", 1);

        if (!foreignKeys)
        {
            await ExecuteAsync(scratch.Connection, "PRAGMA foreign_keys = OFF;");
        }

        await using SqliteTransaction transaction = scratch.Connection.BeginTransaction();

        await AnnalsClaimWriter.DeleteClaimsForSubjectAsync(
            scratch.Connection, transaction, AnnalSubjectStore.Saga, erased, CancellationToken.None);

        await transaction.CommitAsync();

        Assert.Equal(["unrelated"], await RowsAsync(scratch.Connection, "SELECT ReceiptId FROM long_rest_receipts;"));

        Assert.Equal(1L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_receipt_inputs;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(2L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM annal_versions;"));

        Assert.Equal(3L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM saga_memories;"));
    }

    [Fact]
    public async Task The_shared_erasure_plan_counts_every_receipt_companion_before_parent_cascades()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await SeedTransformationAsync(scratch.Connection);

        IReadOnlyList<AnnalsErasureStep> steps = AnnalsErasurePlan.ForSubjectQuery(
            AnnalSubjectStore.Saga, "SELECT Id FROM saga_memories WHERE Id = 'source'");

        foreach ((string table, long count) in new[]
        {
            ("long_rest_receipts", 1L),
            ("long_rest_receipt_inputs", 2L),
            ("long_rest_suppressions", 1L),
        })
        {
            AnnalsErasureStep step = Assert.Single(steps, item => item.Table == table);

            Assert.Equal(count, await ScalarAsync(scratch.Connection, $"SELECT count(*) FROM {table} WHERE {step.Predicate};"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_protected_purge_and_staged_restore_runner_remove_a_complete_transformation(bool eraseSurvivor)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        Guid source = new("AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA");

        Guid survivor = new("BBBBBBBB-2222-4222-8222-BBBBBBBBBBBB");

        await MemoryAsync(scratch.Connection, source.ToString());

        await MemoryAsync(scratch.Connection, survivor.ToString());

        await ReceiptAsync(scratch.Connection, "r", applied: true, survivor: survivor.ToString());

        await InputAsync(scratch.Connection, "r", source.ToString(), 1);

        await InputAsync(scratch.Connection, "r", survivor.ToString(), 2);

        await SuppressionAsync(scratch.Connection, source.ToString(), survivor.ToString(), "r");

        string key = CovenantIdentitySql.Key(eraseSurvivor ? survivor : source);

        await using SqliteTransaction transaction = scratch.Connection.BeginTransaction();

        CovenantArtifactPlanTally counted = await CovenantArtifactPlanRunner.RunAsync(
            scratch.Connection, transaction, SensitiveArtifactKind.Saga, key, CovenantArtifactPlanMode.Count, CancellationToken.None);

        Assert.Equal(1L, Assert.Single(counted.Targets, static item => item.Table == "long_rest_receipts").Rows);

        Assert.Equal(2L, Assert.Single(counted.Targets, static item => item.Table == "long_rest_receipt_inputs").Rows);

        Assert.Equal(1L, Assert.Single(counted.Targets, static item => item.Table == "long_rest_suppressions").Rows);

        _ = await CovenantArtifactPlanRunner.RunAsync(
            scratch.Connection, transaction, SensitiveArtifactKind.Saga, key, CovenantArtifactPlanMode.Delete, CancellationToken.None);

        await transaction.CommitAsync();

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_receipt_inputs;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(1L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM saga_memories;"));

        Assert.Equal(1L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM annal_versions;"));
    }

    private static Task<string[]> NewDefinitionsAsync(SqliteConnection connection) => RowsAsync(connection,
        "SELECT type || ':' || name || ':' || sql FROM sqlite_master WHERE name LIKE 'long_rest_%' OR name LIKE 'idx_long_rest_%' ORDER BY type, name;");

    private static async Task SeedTransformationAsync(SqliteConnection connection, bool applied = true)
    {
        await MemoryAsync(connection, "source");

        await MemoryAsync(connection, "survivor");

        await ReceiptAsync(connection, "r", applied, applied ? "survivor" : null);

        await InputAsync(connection, "r", "source", 1);

        await InputAsync(connection, "r", "survivor", 2);

        if (applied)
        {
            await SuppressionAsync(connection, "source", "survivor", "r");
        }
    }

    private static Task MemoryAsync(
        SqliteConnection connection, string id, int store = 1, int scope = 1, int sensitivity = 0,
        int format = 1, byte[]? contentHash = null) =>
        ExecuteAsync(connection,
            "INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode) VALUES ($id, $id, $at, $scope); INSERT INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc) VALUES ($claim, $store, $id, $at); INSERT INTO annal_versions (VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, SensitivityCode, ContentHashFormatCode, ContentHash, ValidFromUtc, RecordedAtUtc) VALUES ($version, $claim, 1, 1, 3, $scope, $sensitivity, $format, $hash, $at, $at); INSERT INTO saga_memory_embeddings (MemoryId, Embedding, Dim) VALUES ($id, zeroblob(256), 64); INSERT INTO annal_heads (ClaimId, SubjectStoreCode, CurrentVersionId, CurrentRevision, CurrentOperationCode, UpdatedAtUtc) VALUES ($claim, $store, $version, 1, 1, $at);",
            ("$id", id), ("$claim", id + "-claim"), ("$version", id + "-v1"), ("$store", store), ("$scope", scope), ("$sensitivity", sensitivity), ("$format", format), ("$hash", contentHash ?? new byte[32]), ("$at", At));

    private static Task ReceiptAsync(SqliteConnection connection, string receipt, bool applied, string? survivor = null) =>
        ExecuteAsync(connection,
            "INSERT INTO long_rest_receipts (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash, SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc) VALUES ($receipt, 1, 1, $outcome, $reason, $input, $output, $memory, $version, $at);",
            ("$receipt", receipt), ("$outcome", applied ? 1 : 2), ("$reason", applied ? 0 : 1), ("$input", InputHash), ("$output", OutputHash),
            ("$memory", (object?)survivor ?? DBNull.Value), ("$version", survivor is null ? DBNull.Value : survivor + "-v1"), ("$at", At));

    private static Task InputAsync(SqliteConnection connection, string receipt, string memory, int ordinal) =>
        ExecuteAsync(connection,
            "INSERT INTO long_rest_receipt_inputs (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson) VALUES ($receipt, $ordinal, $memory, $version, 1, zeroblob(32), $hash, '{}');",
            ("$receipt", receipt), ("$ordinal", ordinal), ("$memory", memory), ("$version", memory + "-v1"), ("$hash", InputHash));

    private static Task SuppressionAsync(SqliteConnection connection, string source, string survivor, string receipt) =>
        ExecuteAsync(connection,
            "INSERT INTO long_rest_suppressions (SourceVersionId, SurvivorVersionId, ReceiptId) VALUES ($source, $survivor, $receipt);",
            ("$source", source + "-v1"), ("$survivor", survivor + "-v1"), ("$receipt", receipt));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in values)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task<string[]> RowsAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        List<string> values = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private sealed class Scratch(EvolutionScratchDatabase file, SqliteConnection connection) : IAsyncDisposable
    {
        internal SqliteConnection Connection => connection;

        internal static async Task<Scratch> StartAsync()
        {
            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
                connection, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

            return new Scratch(file, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();

            file.Dispose();
        }
    }
}
