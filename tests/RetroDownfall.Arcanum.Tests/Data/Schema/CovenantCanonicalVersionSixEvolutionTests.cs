using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Covenant canonical version 6: the fixed curation binding epoch, receipt entry identity, the purge of
/// curation a pre-fix reset left behind, and the entry-erasure guards, reached from a real version-5
/// installation carrying live, stale, keyless and orphaned pins.
/// </summary>
/// <remarks>
/// Each subject is one Global Confirmed pin: a curation version at revision 1, the head that points at
/// it, and its Applied receipt. <c>live.key</c> pins the epoch its key row holds, <c>stale.key</c> an
/// epoch its key row has since moved past, <c>keyless.key</c> a key no head has claimed yet, and
/// <c>orphan.key</c> a nonzero epoch whose key row a reset deleted without deleting the curation.
/// </remarks>
public sealed class CovenantCanonicalVersionSixEvolutionTests
{
    private const string Timestamp = "2026-01-01T00:00:00.0000000Z";

    /// <summary>The pinned subjects whose curation epoch equals their key's binding epoch.</summary>
    private const string BoundPinsSql =
        "SELECT c.NormalizedKey FROM covenant_curation_heads c WHERE c.IsPinned = 1 AND c.KeyEpoch = COALESCE((SELECT k.IncarnationEpoch FROM covenant_key_epochs k WHERE k.NormalizedKey = c.NormalizedKey), 0) ORDER BY 1;";

    private static readonly string[] CurationTables =
        ["covenant_curation_heads", "covenant_curation_versions", "covenant_curation_receipts"];

    /// <summary>Each pinned subject's key and the epoch its curation rows record.</summary>
    private static readonly (string Key, long Epoch)[] PinnedSubjects =
        [("live.key", 3), ("stale.key", 2), ("keyless.key", 0), ("orphan.key", 4)];

    static CovenantCanonicalVersionSixEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// The evolve path never re-runs head DDL, so an evolved installation matches a fresh one only when
    /// every transition statement reproduces its head text, including the text SQLite splices into a
    /// table declaration for an added column.
    /// </summary>
    [Fact]
    public async Task A_version_five_catalog_evolves_to_a_healthy_version_six_identical_to_a_fresh_one()
    {
        IReadOnlyDictionary<string, string> evolved;

        using (EvolutionScratchDatabase file = EvolutionScratchDatabase.Create())
        {
            await using SqliteConnection connection = await EvolveSeededAsync(file);

            evolved = await CovenantDefinitionsAsync(connection);
        }

        IReadOnlyDictionary<string, string> fresh;

        using (EvolutionScratchDatabase file = EvolutionScratchDatabase.Create())
        {
            await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            await InstallAsync(connection, GrimoireSchemaVersionChains.Default, 6);

            fresh = await CovenantDefinitionsAsync(connection);
        }

        foreach (string name in (string[])
        [
            "covenant_key_epochs",
            "covenant_mutation_receipts",
            "idx_covenant_mutation_receipts_entry",
            "covenant_key_epochs_guard_overflow",
            "covenant_key_epochs_guard_incarnation",
            "covenant_key_epochs_guard_delete",
            "covenant_curation_heads_guard_delete",
            "covenant_curation_versions_guard_delete",
            "covenant_curation_receipts_guard_delete",
            "covenant_search_outbox_guard_delete",
            "covenant_heads_key_epoch_insert",
        ])
        {
            Assert.Contains(name, fresh.Keys);
        }

        Assert.Equal(fresh.Keys.Order(StringComparer.Ordinal), evolved.Keys.Order(StringComparer.Ordinal));

        foreach ((string name, string definition) in fresh)
        {
            Assert.Equal(GrimoireSqlNormalizer.Normalize(definition), GrimoireSqlNormalizer.Normalize(evolved[name]));
        }

        // The two tables the step alters are stored exactly as SQLite spliced them, not merely alike
        // once normalized: one at the comma that opens its constraint list, one at its closing
        // parenthesis.
        Assert.Equal(fresh["covenant_mutation_receipts"], evolved["covenant_mutation_receipts"]);

        Assert.Equal(fresh["covenant_key_epochs"], evolved["covenant_key_epochs"]);
    }

    [Fact]
    public async Task The_upgrade_backfills_each_binding_epoch_from_the_dependency_epoch()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await EvolveSeededAsync(file);

        Assert.Equal(
            [("live.key", 3L, 3L), ("stale.key", 5L, 5L)],
            await KeyEpochRowsAsync(connection));
    }

    /// <summary>
    /// The backfill moves the binding epoch to where each existing pin already points, so a live pin
    /// stays live without rewriting an append-only curation row, and a pin recorded against an earlier
    /// incarnation of its key stays inert without being deleted.
    /// </summary>
    [Fact]
    public async Task The_upgrade_keeps_live_curation_live_and_disarmed_curation_inert()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await EvolveSeededAsync(file);

        Assert.Equal(["keyless.key", "live.key"], await ReadStringsAsync(connection, BoundPinsSql));

        foreach (string table in CurationTables)
        {
            Assert.Equal(1L, await CountAsync(connection, table, "stale.key"));
        }
    }

    /// <summary>
    /// A pin recorded before any head claimed its key binds epoch 0. The first head creates the key row
    /// at binding epoch 0 and later heads leave it there, so the pin keeps applying to the key it was
    /// recorded for.
    /// </summary>
    [Fact]
    public async Task A_keyless_pin_stays_bound_across_the_first_head_for_its_key()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await EvolveSeededAsync(file);

        await ExecuteAsync(connection, KeylessHistorySql);

        Assert.Equal((1L, 0L), await KeyEpochPairAsync(connection, "keyless.key"));

        await ExecuteAsync(
            connection,
            $"UPDATE covenant_heads SET UpdatedAtUtc = '2026-01-02T00:00:00.0000000Z' WHERE EntryId = 'keyless-entry' AND LaneCode = 1;");

        Assert.Equal((2L, 0L), await KeyEpochPairAsync(connection, "keyless.key"));

        Assert.Contains("keyless.key", await ReadStringsAsync(connection, BoundPinsSql));
    }

    /// <summary>
    /// A key the upgrade bound keeps that binding epoch while head changes move its dependency epoch, so
    /// its live pin stays bound. Every change goes through a production writer and reaches one of the
    /// three key-epoch triggers' conflict branches: the kernel's first head for the key reaches the
    /// insert trigger's, its second write the update trigger's, and owner cleanup removing a deleted
    /// Campaign's head for the same key the delete trigger's. Each change advances the dependency epoch
    /// by exactly one.
    /// </summary>
    [Fact]
    public async Task An_upgraded_key_keeps_its_binding_epoch_across_head_writes()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await EvolveSeededAsync(file);

        Assert.Equal((3L, 3L), await KeyEpochPairAsync(connection, "live.key"));

        CovenantMutationReceipt created = await ApplyOperatorSetAsync(
            connection, CovenantOperationScope.Global, "live.key", "First text.", expectedRevision: 0, expectedKeyEpoch: 3);

        Assert.Equal(1L, created.ResultingLaneRevision);

        Assert.Equal((4L, 3L), await KeyEpochPairAsync(connection, "live.key"));

        CovenantMutationReceipt advanced = await ApplyOperatorSetAsync(
            connection, CovenantOperationScope.Global, "live.key", "Second text.", expectedRevision: 1, expectedKeyEpoch: 4);

        Assert.Equal(2L, advanced.ResultingLaneRevision);

        Assert.Equal((5L, 3L), await KeyEpochPairAsync(connection, "live.key"));

        Guid campaignId = CovenantOperationGateFixture.CampaignOne;

        await AddCampaignAsync(connection, campaignId);

        CovenantMutationReceipt scoped = await ApplyOperatorSetAsync(
            connection,
            CovenantOperationScope.ForCampaign(campaignId),
            "live.key",
            "Campaign text.",
            expectedRevision: 0,
            expectedKeyEpoch: 5);

        Assert.Equal(1L, scoped.ResultingLaneRevision);

        Assert.Equal((6L, 3L), await KeyEpochPairAsync(connection, "live.key"));

        await DeleteCampaignAsync(connection, campaignId);

        CovenantCleanupOutcome cleaned = await RunOwnerCleanupAsync(connection);

        Assert.Equal(1, cleaned.CampaignsCleaned);

        Assert.Equal(1, cleaned.HeadsRemoved);

        Assert.Equal(
            1L,
            await CountRowsAsync(connection, "SELECT count(*) FROM covenant_heads WHERE NormalizedKey = 'live.key';"));

        Assert.Equal(
            0L,
            await CountRowsAsync(connection, "SELECT count(*) FROM covenant_heads WHERE CampaignId IS NOT NULL;"));

        Assert.Equal((7L, 3L), await KeyEpochPairAsync(connection, "live.key"));

        Assert.Contains("live.key", await ReadStringsAsync(connection, BoundPinsSql));
    }

    /// <summary>
    /// Before version 6 a family reset deleted key rows and left their curation behind. A nonzero epoch
    /// with no key row can only be such a leftover, because every nonzero epoch was read from a key row
    /// that existed when the curation was written; the upgrade removes it so it cannot bind to a key
    /// that is later re-created. Epoch 0 is a legitimate keyless pin and stays.
    /// </summary>
    [Fact]
    public async Task The_upgrade_purges_curation_left_behind_by_a_pre_fix_reset()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await EvolveSeededAsync(file);

        foreach (string table in CurationTables)
        {
            Assert.Equal(0L, await CountAsync(connection, table, "orphan.key"));

            Assert.Equal(1L, await CountAsync(connection, table, "keyless.key"));
        }
    }

    /// <summary>
    /// Installs version 5 through its reconstruction, seeds the four pinned subjects there, and hands the
    /// same database the shipped chain, which is the whole of an upgrade as an operator reaches it.
    /// </summary>
    private static async Task<SqliteConnection> EvolveSeededAsync(EvolutionScratchDatabase file)
    {
        SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        try
        {
            await InstallAsync(connection, CovenantCanonicalSchemaVersionFiveFixture.ChainSet(), 5);

            await ExecuteAsync(
                connection,
                $"""
                INSERT INTO covenant_key_epochs (NormalizedKey, KeyEpoch, UpdatedAtUtc)
                VALUES ('live.key', 3, '{Timestamp}'), ('stale.key', 5, '{Timestamp}');
                """);

            foreach ((string key, long epoch) in PinnedSubjects)
            {
                await ExecuteAsync(connection, PinnedSubjectSql(key, epoch));
            }

            await InstallAsync(connection, GrimoireSchemaVersionChains.Default, 6);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();

            throw;
        }
    }

    /// <summary>
    /// Commits one operator set of a Confirmed key through the kernel, bound to the dataset,
    /// key-reclamation and Campaign-registry epochs the evolved database carries.
    /// </summary>
    private static async Task<CovenantMutationReceipt> ApplyOperatorSetAsync(
        SqliteConnection connection,
        CovenantOperationScope scope,
        string key,
        string authored,
        long expectedRevision,
        long expectedKeyEpoch)
    {
        CovenantMutationBatch batch = new(
            new Guid((byte[])(await ScalarAsync(connection, "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;"))!),
            (long)(await ScalarAsync(connection, "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;"))!,
            (long)(await ScalarAsync(connection, "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;"))!,
            CovenantMutationFixture.CommitTime,
            [
                CovenantMutationFixture.OperatorSet(scope, key, authored, expectedRevision, expectedKeyEpoch),
            ]);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied =
            await new CovenantMutationKernel(new CovenantQuotaGuard(), MemoryErasureTestKeys.Isolated())
                .ApplyBatchAsync(
                    batch,
                    new CovenantMutationTransaction(connection, transaction),
                    CovenantAgentErasureGate.None,
                    CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        CovenantMutationReceipt receipt = Assert.Single(applied.Value);

        Assert.Equal(CovenantMutationOutcome.Applied, receipt.Outcome);

        await transaction.CommitAsync(CancellationToken.None);

        return receipt;
    }

    /// <summary>
    /// Registers a Campaign in Core and advances the registry epoch, as registering one does.
    /// </summary>
    private static async Task AddCampaignAsync(SqliteConnection connection, Guid campaignId)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, 'erasure', 'erasure', '/tmp/erasure', 1, '{}', $at, $at);
            UPDATE campaign_registry_state SET RegistryEpoch = RegistryEpoch + 1 WHERE StateKey = 1;
            """;

        _ = command.Parameters.AddWithValue("$id", campaignId);

        _ = command.Parameters.AddWithValue("$at", Timestamp);

        _ = await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes the Campaign from Core, which journals the owner deletion owner cleanup catches up on.
    /// </summary>
    private static async Task DeleteCampaignAsync(SqliteConnection connection, Guid campaignId)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """DELETE FROM "Campaigns" WHERE "Id" = $id;""";

        _ = command.Parameters.AddWithValue("$id", campaignId);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// Runs one owner-cleanup batch the way the maintenance host does, under a cleanup lease bound to
    /// the evolved database's dataset generation.
    /// </summary>
    private static async Task<CovenantCleanupOutcome> RunOwnerCleanupAsync(SqliteConnection connection)
    {
        Guid generation = new((byte[])(await ScalarAsync(connection, "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;"))!);

        FakeCovenantAvailability availability = new();

        availability.Mutate(current => current with { DatasetGeneration = generation });

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        await using CovenantCleanupLease lease =
            (await gate.AcquireCleanupAsync(CovenantOperationScope.Global, CancellationToken.None)).Value;

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None);

        Result<CovenantCleanupOutcome> outcome = await new CovenantCleanupWorker().RunBatchAsync(
            lease,
            new CovenantMutationTransaction(connection, transaction),
            CancellationToken.None,
            CovenantCleanupWorker.DefaultBatchSize);

        Assert.True(outcome.IsSuccess, outcome.IsFailure ? outcome.Error.Message : string.Empty);

        await transaction.CommitAsync(CancellationToken.None);

        return outcome.Value;
    }

    private static async Task InstallAsync(SqliteConnection connection, GrimoireSchemaVersionChainSet chains, int version)
    {
        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, chains, 1536, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.CovenantCanonical.Health);

        Assert.Equal(version, result.CovenantCanonical.SchemaVersion);
    }

    private static string PinnedSubjectSql(string key, long epoch) =>
        $"""
        INSERT INTO covenant_curation_versions (
            CurationVersionId, ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, CurationKindCode,
            Revision, PredecessorVersionId, MutationId, RequestIdempotencyDigest, AuthorizationDigest,
            FinalMutationDigest, CreatedAtUtc)
        VALUES (
            'curation-{key}', 1, NULL, '{key}', 1, {epoch}, 1,
            1, NULL, 'mutation-{key}', randomblob(32), randomblob(32),
            randomblob(32), '{Timestamp}');
        INSERT INTO covenant_curation_heads (
            ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, IsPinned, IsMasked, CurrentVersionId,
            CurrentRevision, UpdatedAtUtc)
        VALUES (1, NULL, '{key}', 1, {epoch}, 1, 0, 'curation-{key}', 1, '{Timestamp}');
        INSERT INTO covenant_curation_receipts (
            MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, CurationKindCode,
            ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, OutcomeCode, ResultingVersionId,
            ResultingRevision, ResponseReceiptDigest, CommittedAtUtc)
        VALUES (
            'mutation-{key}', randomblob(32), randomblob(32), randomblob(32), 1,
            1, NULL, '{key}', 1, {epoch}, 1, 'curation-{key}',
            1, randomblob(32), '{Timestamp}');
        """;

    private const string KeylessHistorySql =
        """
        UPDATE covenant_state SET NextSearchRowId = 10 WHERE StateKey = 1;
        INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
        VALUES ('keyless-entry', 1, NULL, 'keyless.key', 'keyless.key', '2026-01-01T00:00:00.0000000Z');
        INSERT INTO covenant_versions (
            VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
            AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
            RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
            AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
            RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
            AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
        VALUES ('keyless-v1', 'keyless-entry', 1, 1, 1, 'one', 'one', randomblob(32), randomblob(32),
                3, 3, 1, 1, 1, NULL, NULL, NULL, NULL, NULL, NULL, 'keyless-mutation-v1', randomblob(32),
                randomblob(32), randomblob(32), NULL, 0, randomblob(32), '2026-01-01T00:00:00.0000000Z');
        INSERT INTO covenant_heads (
            EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode,
            ScopeCode, CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc)
        VALUES ('keyless-entry', 1, 'keyless-v1', 1, 1, 1, NULL, 'keyless.key', 3, 1, 1,
                '2026-01-01T00:00:00.0000000Z');
        """;

    private static async Task<IReadOnlyDictionary<string, string>> CovenantDefinitionsAsync(SqliteConnection connection)
    {
        Dictionary<string, string> definitions = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();

        // By the table an object belongs to as well as by its own name, so the indexes and triggers on
        // a Covenant table are compared along with the table.
        command.CommandText =
            "SELECT name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND (name LIKE 'covenant_%' OR tbl_name LIKE 'covenant_%');";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            definitions.Add(reader.GetString(0), reader.GetString(1));
        }

        return definitions;
    }

    private static async Task<IReadOnlyList<(string Key, long KeyEpoch, long IncarnationEpoch)>> KeyEpochRowsAsync(
        SqliteConnection connection)
    {
        List<(string, long, long)> rows = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT NormalizedKey, KeyEpoch, IncarnationEpoch FROM covenant_key_epochs ORDER BY NormalizedKey;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return rows;
    }

    private static async Task<(long KeyEpoch, long IncarnationEpoch)> KeyEpochPairAsync(
        SqliteConnection connection,
        string key)
    {
        (string _, long keyEpoch, long incarnationEpoch) =
            Assert.Single(await KeyEpochRowsAsync(connection), row => row.Key == key);

        return (keyEpoch, incarnationEpoch);
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        List<string> values = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table, string key)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT count(*) FROM {table} WHERE NormalizedKey = $key;";

        _ = command.Parameters.AddWithValue("$key", key);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountRowsAsync(SqliteConnection connection, string sql) =>
        (long)(await ScalarAsync(connection, sql))!;

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }
}
