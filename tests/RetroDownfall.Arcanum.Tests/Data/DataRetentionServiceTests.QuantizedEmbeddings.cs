using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(36)]
    [InlineData(37)]
    public async Task Compact_pruning_without_the_companion_table_is_allowed_only_before_Core37(int version)
    {
        RequireSqlCipher();

        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection historical = await scratch.OpenAsync(CancellationToken.None);

        if (version == 36)
        {
            await GrimoireSchemaTestInstaller.InstallAsync(historical, CoreSchemaVersionThirtySixFixture.ChainSet(), 64, CancellationToken.None);

            await _db!.DisposeAsync();

            _db = GrimoireFixture.CreateContext(historical);
        }

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        await _db!.Database.OpenConnectionAsync();

        await using var command = _db.Database.GetDbConnection().CreateCommand();

        if (version == 37)
        {
            command.CommandText = "DROP TABLE entry_embeddings_quantized";

            await command.ExecuteNonQueryAsync();
        }

        var settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Assert.Single(plan.CandidateIds);

        var applied = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

        if (version == 37)
        {
            Assert.True(applied.IsFailure);

            command.CommandText = "SELECT Embedding FROM entry_embeddings";

            Assert.Equal(new byte[] { 0, 0, 128, 63 }, (byte[])(await command.ExecuteScalarAsync())!);
        }
        else
        {
            Assert.True(applied.IsSuccess, applied.Error.Message);

            Assert.True(applied.Value.Reconciled);

            Assert.Equal(1, applied.Value.DerivedRecordsDeleted);

            Assert.Equal(0, await CountAllAsync("entry_embeddings"));
        }
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Compact_pruning_deletes_both_copies_once_and_parent_guard_failure_rolls_both_back(bool foreignKeys, bool refuseParent)
    {
        RequireSqlCipher();

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await QuantizedEmbeddingStore.UpsertAsync(connection, null, "entry_embeddings", Canonical(entryId),
            new float[] { 1 }, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT Representation FROM entry_embeddings_quantized";

        byte[] compact = (byte[])(await command.ExecuteScalarAsync())!;

        command.CommandText = foreignKeys ? "PRAGMA foreign_keys=ON" : "PRAGMA foreign_keys=OFF";

        await command.ExecuteNonQueryAsync();

        if (refuseParent)
        {
            command.CommandText = """
                CREATE TEMP TRIGGER refuse_embedding_delete BEFORE DELETE ON main.entry_embeddings
                BEGIN SELECT RAISE(ABORT, 'fixture refuses parent deletion'); END;
                """;

            await command.ExecuteNonQueryAsync();
        }

        var settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Assert.Single(plan.CandidateIds);

        Assert.Equal(2, plan.DerivedRecords);

        var applied = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

        if (refuseParent)
        {
            Assert.True(applied.IsFailure);

            command.CommandText = "SELECT Embedding FROM entry_embeddings";

            Assert.Equal(new byte[] { 0, 0, 128, 63 }, (byte[])(await command.ExecuteScalarAsync())!);

            command.CommandText = "SELECT Representation FROM entry_embeddings_quantized";

            Assert.Equal(compact, (byte[])(await command.ExecuteScalarAsync())!);
        }
        else
        {
            Assert.True(applied.IsSuccess, applied.Error.Message);

            Assert.True(applied.Value.Reconciled);

            Assert.Equal(2, applied.Value.DerivedRecordsDeleted);

            Assert.Equal(0, await CountAllAsync("entry_embeddings"));

            Assert.Equal(0, await CountAllAsync("entry_embeddings_quantized"));
        }
    }

    [SkippableFact]
    public async Task Core36_retention_plans_graph_counts_and_embeddings_reset_do_not_require_new_companions()
    {
        RequireSqlCipher();

        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtySixFixture.ChainSet(), 64, CancellationToken.None);

        await _db!.DisposeAsync();

        _db = GrimoireFixture.CreateContext(connection);

        var settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule();

        DataRetentionService retention = CreateService(settings);

        DataRetentionPlan prune = await retention.PlanAsync(new DataRetentionRequest(DataRetentionOperation.Prune), CancellationToken.None);

        Assert.Empty(prune.Conflicts);

        var graph = await SeedWorkspaceResetGraphAsync();

        DataRetentionPlan workspace = await retention.PlanAsync(WorkspaceResetRequest(graph), CancellationToken.None);

        Assert.Empty(workspace.Conflicts);

        Assert.Equal(0, await TapestryStore.ReadRetentionGraphRowsAsync(connection, null, Guid.NewGuid(), true, CancellationToken.None));

        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IGrimoireOrdinaryConnectionFactory>(FixtureOrdinaryConnectionFactory.For(_db))
            .AddSingleton(FixtureLabeledArtifactGuard.For(_db))
            .BuildServiceProvider();

        EmbeddingsResetResult reset = await new EmbeddingsResetService(_db, services).ResetAsync(EmbeddingsResetScope.All, CancellationToken.None);

        Assert.DoesNotContain(reset.DeletedRowCounts.Keys, name => name.EndsWith("_quantized", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Factory_reset_inventory_counts_compact_rows_and_the_reset_removes_them()
    {
        RequireSqlCipher();

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        DataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        DataRetentionPlan before = await service.PlanAsync(request, CancellationToken.None);

        await _db!.Database.OpenConnectionAsync();

        await QuantizedEmbeddingStore.UpsertAsync(_db.Database.GetDbConnection(), null, "entry_embeddings",
            entryId.ToString("D").ToUpperInvariant(), new float[] { 1 }, CancellationToken.None);

        DataRetentionPlan after = await service.PlanAsync(request, CancellationToken.None);

        Assert.Equal(before.DerivedRecords + 1, after.DerivedRecords);

        _ = await ReconcileFactoryResetV0Async(service, "quantized-factory-recovery-test");

        Assert.Equal(0, await CountAllAsync("entry_embeddings_quantized"));
    }
}
