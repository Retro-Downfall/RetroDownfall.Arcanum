using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class TapestryDagSchemaEvolutionTests
{
    [Fact]
    public void Capture_published_core_twenty_five()
    {
        Assert.Equal(CoreSchemaVersionTwentyFiveFixture.PublishedFingerprint, CoreSchemaVersionTwentyFiveFixture.Fingerprint);

        Assert.Equal(CoreSchemaVersionTwentyFourFixture.PublishedFingerprint, CoreSchemaVersionTwentyFourFixture.Fingerprint);
    }

    [Fact]
    public async Task Fresh_schema_supports_multiple_parents_and_rejects_published_mutation()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Generation("g", "/scope") + Node("leaf", "g", 0, "Leaf")
            + Node("a", "g", 1, "Summary") + Node("b", "g", 2, "Summary") + Node("root", "g", 3, "Summary"));

        await ExecuteAsync(connection, "INSERT INTO tapestry_edges (GenerationId, ParentNodeId, ChildNodeId, MembershipReason) VALUES ('g','a','leaf','Posterior'), ('g','b','leaf','Posterior'), ('g','root','a','Posterior'), ('g','root','b','Posterior');");

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_edges WHERE ChildNodeId='leaf'"));

        Assert.Equal(4L, await ScalarAsync(connection,
            "WITH RECURSIVE reachable(NodeId) AS (SELECT 'root' UNION SELECT edge.ChildNodeId FROM tapestry_edges edge JOIN reachable ON edge.ParentNodeId=reachable.NodeId WHERE edge.GenerationId='g') SELECT COUNT(*) FROM reachable"));

        await ExecuteAsync(connection, "UPDATE tapestry_generations SET Status='Complete' WHERE GenerationId='g';");

        await AssertConstraintAsync(connection, "UPDATE tapestry_nodes SET Content='changed' WHERE NodeId='leaf';");
    }

    [Theory]
    [InlineData("UPDATE tapestry_nodes SET Content='changed' WHERE NodeId='leaf'")]
    [InlineData("DELETE FROM tapestry_nodes WHERE NodeId='leaf'")]
    [InlineData("DELETE FROM tapestry_node_embeddings WHERE NodeId='leaf'")]
    [InlineData("UPDATE tapestry_node_embeddings SET Embedding=x'00' WHERE NodeId='leaf'")]
    [InlineData("INSERT OR REPLACE INTO tapestry_node_embeddings VALUES('leaf',x'00',64)")]
    [InlineData("UPDATE tapestry_generations SET Status='Building' WHERE GenerationId='g'")]
    [InlineData("UPDATE tapestry_generations SET ScopeId='/moved' WHERE GenerationId='g'")]
    public async Task Published_graph_cannot_be_mutated_or_reopened(string mutation)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Generation("g", "/scope") + Node("leaf", "g", 0, "Leaf")
            + "INSERT INTO tapestry_node_embeddings VALUES('leaf',x'00000000',64); UPDATE tapestry_generations SET Status='Complete' WHERE GenerationId='g';");

        await AssertConstraintAsync(connection, mutation);
    }

    [Theory]
    [InlineData("UPDATE tapestry_nodes SET ScopeId='/other' WHERE NodeId='leaf'")]
    [InlineData("UPDATE tapestry_nodes SET ParentNodeId='missing' WHERE NodeId='leaf'")]
    [InlineData("UPDATE tapestry_nodes SET ParentNodeId='leaf' WHERE NodeId='leaf'")]
    [InlineData("UPDATE tapestry_nodes SET ParentNodeId='other' WHERE NodeId='leaf'")]
    public async Task Legacy_writes_reject_invalid_scope_and_parent(string mutation)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Generation("g", "/scope") + Generation("other-g", "/other")
            + Node("leaf", "g", 0, "Leaf") + Node("other", "other-g", 1, "Summary", "/other"));

        await AssertConstraintAsync(connection, mutation);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    public async Task Legacy_upgrade_preserves_valid_identity_and_removes_only_corrupt_generations(int version)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection,
            version == 24 ? CoreSchemaVersionTwentyFourFixture.ChainSet() : CoreSchemaVersionTwentyFiveFixture.ChainSet(), 64, CancellationToken.None);

        await ExecuteAsync(connection, "INSERT INTO workspace_file_chunks (ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt) VALUES ('source','/scope','kept.txt',0,'authoritative',0,13,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');");

        await ExecuteAsync(connection, Generation("valid", "/scope") + Node("root", "valid", 1, "Summary")
            + Node("leaf", "valid", 0, "Leaf", parent: "'root'")
            + Generation("dangling", "/scope") + Node("bad", "dangling", 0, "Leaf", parent: "'absent'")
            + Generation("cross", "/scope") + Node("cross-leaf", "cross", 0, "Leaf", parent: "'root'")
            + Generation("cycle", "/scope") + Node("x", "cycle", 1, "Summary", parent: "'y'") + Node("y", "cycle", 1, "Summary", parent: "'x'")
            + Generation("scope", "/scope") + Node("wrong-scope", "scope", 0, "Leaf", "/other")
            + "UPDATE tapestry_generations SET Status='Complete' WHERE GenerationId='valid';");

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        Assert.True(result.Core.IsHealthy, result.Core.DiagnosticCode);

        Assert.Equal(26, result.Core.SchemaVersion);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_generations"));

        Assert.Equal("authoritative", await ScalarAsync(connection, "SELECT Content FROM workspace_file_chunks WHERE ChunkId='source'"));

        Assert.Equal("legacy-v1", await ScalarAsync(connection, "SELECT AlgorithmVersion FROM tapestry_generations WHERE GenerationId='valid'"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_edges WHERE GenerationId='valid' AND ParentNodeId='root' AND ChildNodeId='leaf' AND MembershipReason='Legacy'"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_generations WHERE ValidatedInvalidationRevision IS NULL"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task Legacy_build_after_upgrade_synchronizes_only_its_own_parent_edge()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentyFiveFixture.ChainSet(), 64, CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Generation("g", "/scope") + Node("leaf", "g", 0, "Leaf")
            + Node("a", "g", 1, "Summary") + Node("b", "g", 2, "Summary")
            + "INSERT INTO tapestry_edges (GenerationId,ParentNodeId,ChildNodeId,MembershipReason) VALUES('g','b','leaf','Posterior');"
            + "UPDATE tapestry_nodes SET ParentNodeId='a',ParentScopeKey='g#a' WHERE NodeId='leaf';");

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_edges"));

        await ExecuteAsync(connection, "UPDATE tapestry_nodes SET ParentNodeId=NULL,ParentScopeKey='g#root' WHERE NodeId='leaf';");

        Assert.Equal("b", await ScalarAsync(connection, "SELECT ParentNodeId FROM tapestry_edges"));
    }

    private static string Generation(string id, string scope) => $"""
        INSERT INTO tapestry_generations (GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt)
        VALUES ('{id}','Workspace','{scope}','Building','legacy-v1','settings','recipe',64,'corpus','2026-01-01T00:00:00.0000000Z');
        """;

    private static string Node(string id, string generation, int layer, string kind, string scope = "/scope", string parent = "NULL") => $"""
        INSERT INTO tapestry_nodes (NodeId,GenerationId,ScopeKind,ScopeId,Layer,ParentScopeKey,NodeKind,ParentNodeId,SourceLabel,ContentHash,EmbeddingDimension,CreatedAt)
        VALUES ('{id}','{generation}','Workspace','{scope}',{layer},'{generation}#root','{kind}',{parent},'label','hash',64,'2026-01-01T00:00:00.0000000Z');
        """;

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task AssertConstraintAsync(SqliteConnection connection, string sql)
    {
        SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql));

        Assert.Equal(19, error.SqliteErrorCode);
    }
}
