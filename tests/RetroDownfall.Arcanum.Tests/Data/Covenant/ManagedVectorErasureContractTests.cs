using RetroDownfall.Arcanum.Core.Covenant;
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
