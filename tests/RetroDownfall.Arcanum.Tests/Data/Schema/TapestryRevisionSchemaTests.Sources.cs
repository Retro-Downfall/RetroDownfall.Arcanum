using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class TapestryRevisionSchemaTests
{
    private const string Session = "11111111-1111-4111-8111-111111111111";

    private const string OtherSession = "22222222-2222-4222-8222-222222222222";

    private const string Entry = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA";

    private const string OtherEntry = "BBBBBBBB-BBBB-4BBB-8BBB-BBBBBBBBBBBB";

    private const string Attachment = "CCCCCCCC-CCCC-4CCC-8CCC-CCCCCCCCCCCC";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_secondary_unique_replacement_invalidates_and_clears_replaced_hash(bool update)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, "PRAGMA recursive_triggers=OFF;" + Sessions()
            + EntryRow(Entry, 1) + (update ? EntryRow(OtherEntry, 2) : string.Empty)
            + $"INSERT INTO tapestry_leaf_hashes(SourceKind,SourceId,ContentSha256,IsBlank) VALUES('Entry','{(update ? OtherEntry : Entry)}','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0);");

        await ExecuteAsync(connection, update
            ? $"UPDATE OR REPLACE Entries SET Sequence=2 WHERE Id='{Entry}';"
            : EntryRow(OtherEntry, 1).Replace("INSERT INTO Entries", "INSERT OR REPLACE INTO Entries", StringComparison.Ordinal));

        Assert.Equal(update ? "3:1" : "2:1", await RevisionsAsync(connection, Session, "Session"));

        await using SqliteCommand count = connection.CreateCommand();

        count.CommandText = "SELECT COUNT(*) FROM tapestry_leaf_hashes WHERE SourceKind='Entry'";

        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Entry_scope_move_invalidates_old_and_new_scope_but_metadata_does_not()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Sessions() + EntryRow(Entry, 1));

        Assert.Equal("1:0", await RevisionsAsync(connection, Session, "Session"));

        await ExecuteAsync(connection, $"UPDATE Entries SET IsPinned=1, ModelUsed='other' WHERE Id='{Entry}';");

        Assert.Equal("1:0", await RevisionsAsync(connection, Session, "Session"));

        await ExecuteAsync(connection, $"UPDATE Entries SET SessionId='{OtherSession}' WHERE Id='{Entry}';");

        Assert.Equal("2:1", await RevisionsAsync(connection, Session, "Session"));

        Assert.Equal("1:1", await RevisionsAsync(connection, OtherSession, "Session"));
    }

    [Fact]
    public async Task Attachment_version_deactivation_and_secondary_replacement_are_destructive()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, AttachmentOwner() + AttachmentChunk("first", "text/plain"));

        Assert.Equal("1:0", await RevisionsAsync(connection, Session, "SessionAttachment"));

        await ExecuteAsync(connection, AttachmentChunk("replacement", "text/plain").Replace("INSERT INTO session_attachment_chunks", "INSERT OR REPLACE INTO session_attachment_chunks", StringComparison.Ordinal));

        Assert.Equal("2:1", await RevisionsAsync(connection, Session, "SessionAttachment"));

        await ExecuteAsync(connection, "UPDATE session_attachment_chunks SET RetrievalScope=NULL;");

        Assert.Equal("3:2", await RevisionsAsync(connection, Session, "SessionAttachment"));
    }

    [Fact]
    public async Task Document_mime_registry_is_excluded_from_revision_corpus()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, AttachmentOwner());

        foreach (SessionAttachmentComprehensionFormat format in SessionAttachmentComprehensionPolicy.Formats)
        {
            await ExecuteAsync(connection, AttachmentChunk("document", " " + format.MimeType.ToUpperInvariant() + "; charset=utf-8 ")
                .Replace("INSERT INTO session_attachment_chunks", "INSERT OR REPLACE INTO session_attachment_chunks", StringComparison.Ordinal));

            Assert.Null(await RevisionsAsync(connection, Session, "SessionAttachment"));
        }
    }

    [Fact]
    public async Task Conflicting_ignore_is_conservative_and_failed_statement_restores_revisions()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Chunk("a", "/scope", "one") + Chunk("b", "/unrelated", "safe"));

        await ExecuteAsync(connection, Chunk("a", "/scope", "ignored").Replace("INSERT INTO", "INSERT OR IGNORE INTO", StringComparison.Ordinal));

        Assert.Equal("2:1", await RevisionsAsync(connection, "/scope"));

        Assert.Equal("1:0", await RevisionsAsync(connection, "/unrelated"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, Chunk("a", "/scope", "fails")));

        Assert.Equal("2:1", await RevisionsAsync(connection, "/scope"));
    }

    private static string Sessions() => $"""
        INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES
        ('{Session}','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z'),
        ('{OtherSession}','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
        """;

    private static string EntryRow(string id, int sequence) => $"""
        INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
        VALUES('{id}','{Session}',0,'source','model','2026-01-01T00:00:00.0000000Z',{sequence});
        """;

    private static string AttachmentOwner() => $"""
        INSERT INTO SessionAttachments(Id,SessionId,State,LogicalKey,OriginalFileName,Version,RelativePath,ContentSha256,MimeType,ByteLength,Kind,CreatedAt)
        VALUES('{Attachment}','{Session}','Bound','notes','notes.txt',1,'notes.txt','hash','text/plain',6,'Text','2026-01-01T00:00:00.0000000Z');
        """;

    private static string AttachmentChunk(string id, string mime) => $"""
        INSERT INTO session_attachment_chunks(ChunkId,GenerationId,SessionId,AttachmentId,LogicalKey,Version,OriginalFileName,MimeType,ContentSha256,ChunkIndex,CharacterStart,CharacterEnd,StartLine,EndLine,Content,EmbeddingDimension,ExtractedAt,IndexedAt,RetrievalScope)
        VALUES('{id}','g','{Session}','{Attachment}','notes',1,'notes.txt','{mime}','hash',0,0,6,1,1,'source',64,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z','Latest');
        """;
}
