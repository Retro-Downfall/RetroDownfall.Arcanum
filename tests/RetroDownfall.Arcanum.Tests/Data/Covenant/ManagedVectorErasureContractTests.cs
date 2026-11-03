using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed class ManagedVectorErasureContractTests
{
    [Theory]
    [InlineData(SensitiveArtifactKind.AssistantEntry, "entry_embeddings")]
    [InlineData(SensitiveArtifactKind.Saga, "saga_memory_embeddings")]
    [InlineData(SensitiveArtifactKind.Embedding, "entry_embeddings")]
    public void Erasure_targets_only_durable_embeddings_after_admission(
        SensitiveArtifactKind kind,
        string embeddingTable)
    {
        CovenantArtifactPurgePlan plan = CovenantArtifactPurgePlans.Resolve(kind);

        string[] tables = [.. plan.Projections.Select(target => target.Table), .. plan.Artifact is { } artifact ? new[] { artifact.Table } : []];

        Assert.Contains(embeddingTable, tables);

        Assert.DoesNotContain(tables, table => table.EndsWith("_vec", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SensitiveArtifactKind.AssistantEntry, "entry_embeddings_quantized")]
    [InlineData(SensitiveArtifactKind.Saga, "saga_memory_embeddings_quantized")]
    [InlineData(SensitiveArtifactKind.Embedding, "entry_embeddings_quantized")]
    public void Erasure_explicitly_counts_compact_copies_before_the_parent(SensitiveArtifactKind kind, string table)
    {
        CovenantArtifactPurgePlan plan = CovenantArtifactPurgePlans.Resolve(kind);

        Assert.Contains(plan.Projections, target => target.Table == table && target.RequiredFromCoreVersion == 37);

        if (kind == SensitiveArtifactKind.Saga)
        {
            Assert.True(MemoryErasureTableCodes.For(table) > MemoryErasureTableCodes.For("saga_memory_embeddings"));
        }
    }

    [Theory]
    [InlineData(36)]
    [InlineData(37)]
    public async Task Count_and_delete_include_compact_copies_only_when_the_schema_owns_them(int version)
    {
        SqliteNativeRuntime.Instance.Initialize();

        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"""
            CREATE TABLE grimoire_feature_schemas (FamilyCode INTEGER, TransactionTierCode INTEGER, SchemaVersion INTEGER);
            INSERT INTO grimoire_feature_schemas VALUES (0, 0, {version});
            CREATE TABLE entry_embeddings (EntryId TEXT PRIMARY KEY, Embedding BLOB, Dim INTEGER);
            INSERT INTO entry_embeddings VALUES ('11111111-1111-4111-8111-111111111111', X'0000803F', 1);
            """;

        await command.ExecuteNonQueryAsync();

        if (version >= 37)
        {
            command.CommandText = Assert.Single(GrimoireSchemaCatalog.CoreObjects,
                item => item.Name == "entry_embeddings_quantized").Sql;

            await command.ExecuteNonQueryAsync();

            command.CommandText = "INSERT INTO entry_embeddings_quantized VALUES ('11111111-1111-4111-8111-111111111111', X'01')";

            await command.ExecuteNonQueryAsync();
        }

        CovenantArtifactPlanTally measured = await CovenantArtifactPlanRunner.RunAsync(connection, null,
            SensitiveArtifactKind.Embedding, "11111111111141118111111111111111", CovenantArtifactPlanMode.Count, CancellationToken.None);

        Assert.Equal(version >= 37 ? 1 : 0, Assert.Single(measured.Targets, target => target.Table == "entry_embeddings_quantized").Rows);

        CovenantArtifactPlanTally removed = await CovenantArtifactPlanRunner.RunAsync(connection, null,
            SensitiveArtifactKind.Embedding, "11111111111141118111111111111111", CovenantArtifactPlanMode.Delete, CancellationToken.None);

        Assert.Equal(measured.Targets.Sum(target => target.Rows), removed.Targets.Sum(target => target.Rows));
    }

    [Fact]
    public void Runtime_retention_and_erasure_do_not_depend_on_retired_mirrors()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RetroDownfall.Arcanum.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        string infrastructure = Path.Combine(root.FullName, "src", "RetroDownfall.Arcanum.Infrastructure");

        string[] paths =
        [
            .. Directory.GetFiles(Path.Combine(infrastructure, "Data"), "DataRetentionService*.cs"),
            Path.Combine(infrastructure, "Data", "RetentionSchemaCensus.cs"),
            Path.Combine(infrastructure, "Data", "Covenant", "CovenantArtifactPlanRunner.cs"),
            Path.Combine(infrastructure, "Data", "Covenant", "CovenantProtectedArtifactErasureKernel.cs"),
            Path.Combine(infrastructure, "Memory", "SagaMemoryErasureService.cs"),
            Path.Combine(infrastructure, "Backup", "BackupRestoreErasureEvidenceApplier.cs"),
        ];

        foreach (string path in paths)
        {
            string source = File.ReadAllText(path);

            Assert.False(source.Contains("_embeddings_vec", StringComparison.Ordinal), path);

            Assert.False(source.Contains("SagaVectorMirror", StringComparison.Ordinal), path);
        }
    }
}
