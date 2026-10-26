using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class TapestryRevisionSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenuineCore26LegacyGenerationRequiresRevisionStableCorpusAndExactEmbeddingProof(bool editRevert)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentySixFixture.ChainSet(), 64, CancellationToken.None);

        await ExecuteAsync(connection, Chunk("legacy", "/scope", "one"));

        string hash = TapestryHash.OfContent("one");

        string corpus = TapestryHash.OfCorpus([("legacy", hash)]);

        string embedding = TapestryHash.OfEmbeddingModel("provider", "model");

        string settings = TapestryHash.OfSettings(5, 8, 24, 256, 512, 8, embedding);

        await ExecuteAsync(connection, $"""
            INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt)
            VALUES('legacy-generation','Workspace','/scope','Building','{SphericalKMeans.AlgorithmVersion}','{settings}','{TapestryHash.SummaryRecipeVersion}',8,'{corpus}','2026-01-01T00:00:00.0000000Z');
            INSERT INTO tapestry_nodes(NodeId,GenerationId,ScopeKind,ScopeId,Layer,ParentScopeKey,NodeKind,SourceKind,SourceId,SourceLabel,ContentHash,EmbeddingDimension,CreatedAt)
            VALUES('leaf','legacy-generation','Workspace','/scope',0,'legacy-generation#root','Leaf','WorkspaceFileChunk','legacy','a.cs','{hash}',8,'2026-01-01T00:00:00.0000000Z');
            INSERT INTO tapestry_node_embeddings(NodeId,Embedding,Dim) VALUES('leaf',x'0000803F00000000000000000000000000000000000000000000000000000000',8);
            UPDATE tapestry_generations SET Status='Complete',LayerCount=1,NodeCount=1,RootNodeCount=1 WHERE GenerationId='legacy-generation';
            """);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        Assert.Equal(1L, await RevisionScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_generations WHERE ValidatedInvalidationRevision IS NULL AND EmbeddingIdentity IS NULL"));

        if (editRevert)
        {
            await ExecuteAsync(connection, "UPDATE workspace_file_chunks SET Content='edited' WHERE ChunkId='legacy'; UPDATE workspace_file_chunks SET Content='one' WHERE ChunkId='legacy';");
        }

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        TapestryStore store = new(db, new WeaveIndexAvailability());

        TapestryScope scope = new(TapestryScopeKind.Workspace, "/scope");

        TapestryRetrievalIdentity identity = new(embedding, 8, new(5, 8, 24, 256, 512));

        Assert.Equal(corpus, (await store.ReadBuildSnapshotAsync(scope, CancellationToken.None)).Corpus.Fingerprint);

        var lookup = await store.GetRetrievableGenerationAsync(scope, identity, CancellationToken.None);

        Assert.Equal(!editRevert, lookup.IsSuccess);

        if (!editRevert)
        {
            Assert.Equal(0L, lookup.Value.Generation.ValidatedInvalidationRevision);

            Assert.Null(lookup.Value.Generation.EmbeddingIdentity);

            Assert.True((await store.GetRetrievableGenerationAsync(scope, identity with { EmbeddingIdentity = "wrong" }, CancellationToken.None)).IsFailure);

            await ExecuteAsync(connection, Chunk("appended", "/scope", "two"));

            Assert.True((await store.GetRetrievableGenerationAsync(scope, identity, CancellationToken.None)).IsSuccess);

            Assert.Single(await store.HydrateRetrievedNodesAsync(lookup.Value, [("leaf", 1f)], TapestryRetrievalMode.CollapsedTree, CancellationToken.None));
        }
        else
        {
            Assert.Equal(1L, await RevisionScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_generations WHERE ValidatedInvalidationRevision IS NULL"));
        }
    }
}
