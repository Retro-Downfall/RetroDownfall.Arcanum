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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Complete_scope_replacement_preserves_both_graphs(bool upgraded, bool update)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (upgraded)
        {
            await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentyFiveFixture.ChainSet(), 64, CancellationToken.None);
        }

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Generation("g", "/scope") + Generation("next", "/scope")
            + Node("a", "g", 1, "Summary") + Node("leaf", "g", 0, "Leaf", parent: "'a'")
            + Node("next-a", "next", 1, "Summary") + Node("next-leaf", "next", 0, "Leaf", parent: "'next-a'")
            + "INSERT INTO tapestry_node_embeddings VALUES('leaf',x'01020304',64),('next-leaf',x'05060708',64);"
            + "UPDATE tapestry_generations SET Status='Complete' WHERE GenerationId='g';");

        string mutation = update
            ? "UPDATE OR REPLACE tapestry_generations SET Status='Complete' WHERE GenerationId='next';"
            : Generation("replacement", "/scope").Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal)
                .Replace("'Building'", "'Complete'", StringComparison.Ordinal);

        await AssertConstraintAsync(connection, mutation);

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_generations"));

        Assert.Equal("g", await ScalarAsync(connection, "SELECT GenerationId FROM tapestry_generations WHERE Status='Complete'"));

        Assert.Equal("Building", await ScalarAsync(connection, "SELECT Status FROM tapestry_generations WHERE GenerationId='next'"));

        Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_nodes"));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_edges"));

        Assert.Equal("01020304", await ScalarAsync(connection, "SELECT hex(Embedding) FROM tapestry_node_embeddings WHERE NodeId='leaf'"));

        Assert.Equal("05060708", await ScalarAsync(connection, "SELECT hex(Embedding) FROM tapestry_node_embeddings WHERE NodeId='next-leaf'"));

        await ExecuteAsync(connection, "UPDATE tapestry_generations SET Status='Superseded' WHERE GenerationId='g'; UPDATE tapestry_generations SET Status='Complete' WHERE GenerationId='next';");

        Assert.Equal("next", await ScalarAsync(connection, "SELECT GenerationId FROM tapestry_generations WHERE Status='Complete'"));

        await ExecuteAsync(connection, "DELETE FROM tapestry_generations WHERE GenerationId='g';");

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_nodes"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_parent_delete_requires_detaching_children_or_deleting_owner(bool upgraded)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (upgraded)
        {
            await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentyFiveFixture.ChainSet(), 64, CancellationToken.None);
        }

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Generation("g", "/scope") + Node("a", "g", 1, "Summary")
            + Node("leaf", "g", 0, "Leaf", parent: "'a'"));

        await AssertConstraintAsync(connection, "DELETE FROM tapestry_nodes WHERE NodeId='a';");

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_nodes"));

        Assert.Equal("a", await ScalarAsync(connection, "SELECT ParentNodeId FROM tapestry_nodes WHERE NodeId='leaf'"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_edges"));

        await ExecuteAsync(connection, "UPDATE tapestry_nodes SET ParentNodeId=NULL WHERE NodeId='leaf'; DELETE FROM tapestry_nodes WHERE NodeId='a';");

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_nodes"));

        await ExecuteAsync(connection, Node("b", "g", 1, "Summary")
            + "UPDATE tapestry_nodes SET ParentNodeId='b' WHERE NodeId='leaf'; DELETE FROM tapestry_generations WHERE GenerationId='g';");

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_nodes"));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    public async Task Corrupt_generation_upgrade_removes_optional_mirror_only_for_rejected_nodes(int version)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection,
            version == 24 ? CoreSchemaVersionTwentyFourFixture.ChainSet() : CoreSchemaVersionTwentyFiveFixture.ChainSet(), 64, CancellationToken.None);

        await ExecuteAsync(connection, Generation("valid", "/scope") + Node("kept", "valid", 0, "Leaf")
            + Generation("bad", "/scope") + Node("bad-leaf", "bad", 0, "Leaf", parent: "'missing'")
            + Node("bad-root", "bad", 1, "Summary")
            + "INSERT INTO tapestry_node_embeddings VALUES('kept',x'01020304',64),('bad-leaf',x'05060708',64),('bad-root',x'090a0b0c',64);"
            + "CREATE TABLE tapestry_node_embeddings_vec (NodeId TEXT PRIMARY KEY, Embedding BLOB NOT NULL);"
            + "INSERT INTO tapestry_node_embeddings_vec SELECT NodeId,Embedding FROM tapestry_node_embeddings;");

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_node_embeddings_vec"));

        Assert.Equal("01020304", await ScalarAsync(connection, "SELECT hex(Embedding) FROM tapestry_node_embeddings_vec WHERE NodeId='kept'"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_node_embeddings"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_generations"));
    }

    [Fact]
    public async Task Corrupt_generation_upgrade_never_opens_unavailable_legacy_vector_mirror()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentyFiveFixture.ChainSet(), 64, CancellationToken.None);

        // Model an imported legacy catalog in this disposable database without loading a module.
        await ExecuteAsync(connection, Generation("bad", "/scope") + Node("bad-leaf", "bad", 0, "Leaf", parent: "'missing'")
            + "INSERT INTO tapestry_node_embeddings VALUES('bad-leaf',x'01020304',64);"
            + "PRAGMA writable_schema=ON;"
            + "INSERT INTO sqlite_schema(type,name,tbl_name,rootpage,sql) VALUES('table','tapestry_node_embeddings_vec','tapestry_node_embeddings_vec',0,"
            + "'CREATE VIRTUAL TABLE tapestry_node_embeddings_vec USING vec0(NodeId TEXT PRIMARY KEY, Embedding FLOAT[64])');"
            + "PRAGMA writable_schema=RESET;");

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        Assert.True(result.Core.IsHealthy, result.Core.DiagnosticCode);

        Assert.Equal(26, result.Core.SchemaVersion);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_node_embeddings"));

        SqliteException unavailable = await Assert.ThrowsAsync<SqliteException>(() => ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_node_embeddings_vec"));

        Assert.Contains("no such module: vec0", unavailable.Message, StringComparison.Ordinal);
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
