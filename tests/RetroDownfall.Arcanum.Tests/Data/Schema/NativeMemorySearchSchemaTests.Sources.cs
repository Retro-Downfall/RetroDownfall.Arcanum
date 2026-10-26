using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class NativeMemorySearchSchemaTests
{
    private const string SessionOne = "11111111-1111-4111-8111-111111111111";
    private const string SessionTwo = "22222222-2222-4222-8222-222222222222";
    private const string EntryOne = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA";
    private const string EntryTwo = "BBBBBBBB-BBBB-4BBB-8BBB-BBBBBBBBBBBB";
    private const string AttachmentOne = "CCCCCCCC-CCCC-4CCC-8CCC-CCCCCCCCCCCC";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entries_secondary_IGNORE_REPLACE_and_rowid_moves_keep_projection_and_tokens_exact(bool recursive)
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        await ExecuteAsync(connection, $"PRAGMA recursive_triggers={(recursive ? 1 : 0)};" + SessionRows()
            + EntrySql(EntryOne, 1, "oldtoken"));
        await ExecuteAsync(connection, EntrySql(EntryTwo, 1, "ignoredtoken").Replace("INSERT INTO", "INSERT OR IGNORE INTO", StringComparison.Ordinal));
        Assert.Equal(1L, await CountAsync(connection, "old"));
        Assert.Equal(0L, await CountAsync(connection, "ign"));
        await ExecuteAsync(connection, EntrySql(EntryTwo, 1, "newtoken").Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal));
        Assert.Equal(0L, await CountAsync(connection, "old"));
        Assert.Equal(1L, await CountAsync(connection, "new"));
        await ExecuteAsync(connection, EntrySql(EntryOne, 2, "thirdtoken"));
        await ExecuteAsync(connection, $"UPDATE OR IGNORE Entries SET Sequence=2,Content='ignoredtoken' WHERE Id='{EntryTwo}'");
        Assert.Equal(1L, await CountAsync(connection, "new"));
        Assert.Equal(1L, await CountAsync(connection, "thi"));
        Assert.Equal(0L, await CountAsync(connection, "ign"));
        await ExecuteAsync(connection, $"UPDATE OR REPLACE Entries SET Sequence=2,Content='changedtoken' WHERE Id='{EntryTwo}'");
        Assert.Equal(0L, await CountAsync(connection, "new"));
        Assert.Equal(0L, await CountAsync(connection, "thi"));
        Assert.Equal(1L, await CountAsync(connection, "cha"));
        await ExecuteAsync(connection, $"UPDATE Entries SET rowid=-10,SessionId='{SessionTwo}',IsPinned=1 WHERE Id='{EntryTwo}'");
        Assert.Equal(SessionTwo, await ScalarAsync(connection, "SELECT Owner FROM memory_search_documents WHERE Corpus=0"));
        Assert.Equal(1L, await CountAsync(connection, "cha"));
        await ExecuteAsync(connection, $"BEGIN; UPDATE Entries SET Content='rollbacktoken' WHERE Id='{EntryTwo}'; ROLLBACK;");
        Assert.Equal(0L, await CountAsync(connection, "rol"));
        Assert.Equal(1L, await CountAsync(connection, "cha"));
        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
        {
            await ExecuteAsync(connection, $"DELETE FROM Sessions WHERE Id='{SessionTwo}'; INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1);");
        }
        Assert.Equal(0L, await CountAsync(connection, "cha"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_search_documents WHERE Corpus=0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attachment_secondary_conflicts_metadata_edits_and_cascade_delete_keep_all_fields_exact(bool recursive)
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        await ExecuteAsync(connection, $"PRAGMA recursive_triggers={(recursive ? 1 : 0)};" + SessionRows() + $"""
            INSERT INTO SessionAttachments(Id,SessionId,State,LogicalKey,OriginalFileName,Version,RelativePath,ContentSha256,MimeType,ByteLength,Kind,CreatedAt)
            VALUES('{AttachmentOne}','{SessionOne}','Bound','notes','notes.txt',1,'notes.txt','hash','text/plain',6,'Text','2026-01-01T00:00:00.0000000Z');
            """ + AttachmentSql("first", "oldtoken"));
        await ExecuteAsync(connection, AttachmentSql("ignored", "ignoredtoken").Replace("INSERT INTO", "INSERT OR IGNORE INTO", StringComparison.Ordinal));
        Assert.Equal(1L, await CountAsync(connection, "old"));
        Assert.Equal(0L, await CountAsync(connection, "ign"));
        await ExecuteAsync(connection, AttachmentSql("second", "newtoken").Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal));
        Assert.Equal(0L, await CountAsync(connection, "old"));
        Assert.Equal(1L, await CountAsync(connection, "new"));
        await ExecuteAsync(connection, "UPDATE session_attachment_chunks SET ChunkId='moved',rowid=-20,OriginalFileName='filenameonly',LogicalKey='logicalonly' WHERE ChunkId='second'");
        Assert.Equal(1L, await CountAsync(connection, "fil"));
        Assert.Equal(1L, await CountAsync(connection, "log"));
        Assert.Equal(1L, await CountAsync(connection, "new"));
        await ExecuteAsync(connection, $"DELETE FROM SessionAttachments WHERE Id='{AttachmentOne}'; INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1);");
        Assert.Equal(0L, await CountAsync(connection, "fil"));
        Assert.Equal(0L, await CountAsync(connection, "log"));
        Assert.Equal(0L, await CountAsync(connection, "new"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nullable_summaries_refresh_on_real_effects_and_survive_ignored_source_insert(bool recursive)
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        await ExecuteAsync(connection, $"PRAGMA recursive_triggers={(recursive ? 1 : 0)};" + SessionRows());
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_search_documents WHERE Corpus=1"));
        await ExecuteAsync(connection, $"UPDATE Sessions SET Summary='summarytoken' WHERE Id='{SessionOne}'");
        Assert.Equal(1L, await CountAsync(connection, "sum"));
        await ExecuteAsync(connection, $"INSERT OR IGNORE INTO Sessions(Id,CreatedAt,UpdatedAt,Summary) VALUES('{SessionOne}','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z','ignoredtoken')");
        Assert.Equal(1L, await CountAsync(connection, "sum"));
        Assert.Equal(0L, await CountAsync(connection, "ign"));
        await ExecuteAsync(connection, $"UPDATE Sessions SET Summary=NULL WHERE Id='{SessionOne}'");
        Assert.Equal(0L, await CountAsync(connection, "sum"));
        await ExecuteAsync(connection, $"UPDATE Sessions SET Summary='replacementtoken',Title='new title' WHERE Id='{SessionOne}'");
        Assert.Equal(1L, await CountAsync(connection, "rep"));
        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
        {
            await ExecuteAsync(connection, $"DELETE FROM Sessions WHERE Id='{SessionOne}'; INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1);");
        }
        Assert.Equal(0L, await CountAsync(connection, "rep"));
    }

    private static string SessionRows() => $"""
        INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES
        ('{SessionOne}','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z'),
        ('{SessionTwo}','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
        """;

    private static string EntrySql(string id, int sequence, string content) => $"""
        INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
        VALUES('{id}','{SessionOne}',0,'{content}','test','2026-01-01T00:00:00.0000000Z',{sequence});
        """;

    private static string AttachmentSql(string id, string content) => $"""
        INSERT INTO session_attachment_chunks(ChunkId,GenerationId,SessionId,AttachmentId,LogicalKey,Version,OriginalFileName,MimeType,ContentSha256,ChunkIndex,CharacterStart,CharacterEnd,StartLine,EndLine,Content,EmbeddingDimension,ExtractedAt,IndexedAt)
        VALUES('{id}','generation','{SessionOne}','{AttachmentOne}','notes',1,'notes.txt','text/plain','hash',0,0,6,1,1,'{content}',64,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
        """;
}
