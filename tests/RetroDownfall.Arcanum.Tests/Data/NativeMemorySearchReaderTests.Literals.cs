using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class NativeMemorySearchReaderTests
{
    public static IEnumerable<object[]> LiteralCases()
    {
        foreach ((string query, string text) in new (string, string)[]
        {
            ("abc", "xxABCyy"), ("abc", "before\0ABCafter"), ("ABCafter", "before\0ABCafter"),
            ("a\0b", "before a\0b after"), ("a", "a"), ("ab", "abc"),
            ("a%b", "before a%b after"), ("a_b", "before a_b after"), ("a\"b", "a\"b"),
            ("AND", "AND OR NEAR(foo)"), ("😀ab", "prefix😀ab"), ("e\u0301xy", "e\u0301xy"),
            ("écl", "Éclair"), ("Écl", "Éclair"), ("Σab", "σab"), ("𐐀ab", "𐐨ab"),
        })
        {
            foreach ((int phase, int field) in new[] { (0, 0), (1, 0), (2, 0), (2, 1), (2, 2), (3, 0), (3, 1) })
                yield return [phase, field, query, text];
        }
    }

    [Theory]
    [MemberData(nameof(LiteralCases))]
    public async Task Native_index_and_fallback_retain_the_original_each_field_oracle(int phase, int field, string query, string text)
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        string[] fields = phase switch { 0 => ["Content"], 1 => ["Summary"], 2 => ["Content", "OriginalFileName", "LogicalKey"], _ => ["Content", "RelativePath"] };
        string table = phase switch { 0 => "Entries", 1 => "Sessions", 2 => "session_attachment_chunks", _ => "workspace_file_chunks" };
        string[] values = fields.Select((_, index) => index == field ? text : "void").ToArray();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES('11111111-1111-4111-8111-111111111111','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');";
        _ = await command.ExecuteNonQueryAsync();
        command.CommandText = phase switch
        {
            0 => "INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA','11111111-1111-4111-8111-111111111111',0,@v0,'test','2026-01-01T00:00:00.0000000Z',1)",
            1 => "UPDATE Sessions SET Summary=@v0",
            2 => """
                INSERT INTO SessionAttachments(Id,SessionId,State,LogicalKey,OriginalFileName,Version,RelativePath,ContentSha256,MimeType,ByteLength,Kind,CreatedAt)
                VALUES('CCCCCCCC-CCCC-4CCC-8CCC-CCCCCCCCCCCC','11111111-1111-4111-8111-111111111111','Bound','void','void',1,'void','hash','text/plain',4,'Text','2026-01-01T00:00:00.0000000Z');
                INSERT INTO session_attachment_chunks(ChunkId,SessionId,AttachmentId,LogicalKey,Version,OriginalFileName,MimeType,ContentSha256,ChunkIndex,CharacterStart,CharacterEnd,StartLine,EndLine,Content,EmbeddingDimension,ExtractedAt,IndexedAt)
                VALUES('chunk','11111111-1111-4111-8111-111111111111','CCCCCCCC-CCCC-4CCC-8CCC-CCCCCCCCCCCC',@v2,1,@v1,'text/plain','hash',0,0,4,1,1,@v0,64,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
                """,
            _ => "INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt) VALUES('chunk','/fake/workspace',@v1,0,@v0,0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')",
        };
        for (int index = 0; index < values.Length; index++) _ = command.Parameters.AddWithValue("@v" + index, values[index]);
        _ = await command.ExecuteNonQueryAsync();
        command.Parameters.Clear();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE " + string.Join(" OR ", fields.Select(name => $"instr(lower({name}),lower(@query))>0"));
        _ = command.Parameters.AddWithValue("@query", query);
        long expected = (long)(await command.ExecuteScalarAsync())!;
        MemorySearchScope scope = phase < 2 ? MemorySearchScope.Session : phase == 2 ? MemorySearchScope.Attachments : MemorySearchScope.Workspace;
        NativeMemorySearchPage page = await ReadNativeAsync(connection, query, scope, null, null, new(phase), 20, CancellationToken.None);
        Assert.Equal(expected, page.Hits.LongLength);
        Assert.Null(page.Reason);
        await using SqliteCommand integrity = connection.CreateCommand();
        integrity.CommandText = "INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1)";
        _ = await integrity.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Sparse_candidate_sort_retains_native_BINARY_path_order_for_nonBMP_paths()
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            VALUES('bmp','/fake/workspace','a'||char(57344),0,'needletoken',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z'),
            ('astral','/fake/workspace','a'||char(65536),0,'needletoken',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
            """;
        _ = await command.ExecuteNonQueryAsync();
        NativeMemorySearchPage first = await ReadNativeAsync(connection, "needletoken", MemorySearchScope.Workspace, null, null, new(3), 1, CancellationToken.None);
        Assert.Equal("bmp", Assert.Single(first.Hits).Result.SourceId);
        Assert.NotNull(first.Reason);
        NativeMemorySearchPage second = await ReadNativeAsync(connection, "needletoken", MemorySearchScope.Workspace, null, null, first.Position, 1, CancellationToken.None);
        Assert.Equal("astral", Assert.Single(second.Hits).Result.SourceId);
        Assert.Null(second.Reason);
    }

    [Fact]
    public async Task Nonmatching_oversized_sparse_candidate_consumes_examined_bytes_and_resumes_before_the_old_match()
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            VALUES('large','/fake/workspace','a',0,@large,0,4,'2026-01-01T00:00:00.0000000Z','2026-01-02T00:00:00.0000000Z'),
            ('old','/fake/workspace','a',0,'abcneedle',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
            """;
        _ = command.Parameters.AddWithValue("@large", "abc" + new string('x', 3 * 1024 * 1024));
        _ = await command.ExecuteNonQueryAsync();
        NativeMemorySearchPage first = await ReadNativeAsync(connection, "abcneedle", MemorySearchScope.Workspace, null, null, new(3), 20, CancellationToken.None);
        Assert.Empty(first.Hits);
        Assert.Equal(MemorySearchTruncationReason.ExaminedBytes, first.Reason);
        NativeMemorySearchPage second = await ReadNativeAsync(connection, "abcneedle", MemorySearchScope.Workspace, null, null, first.Position, 20, CancellationToken.None);
        Assert.Equal("old", Assert.Single(second.Hits).Result.SourceId);
        Assert.Null(second.Reason);
    }
}
