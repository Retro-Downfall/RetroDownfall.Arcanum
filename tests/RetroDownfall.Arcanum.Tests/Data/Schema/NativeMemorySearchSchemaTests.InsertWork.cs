using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class NativeMemorySearchSchemaTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _projectionWorkOutput;

    public NativeMemorySearchSchemaTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _projectionWorkOutput = output;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Projection_writes_do_not_scan_the_existing_corpus(bool attachment, bool update, bool evolved)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (evolved)
        {
            await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtyOneFixture.ChainSet(), 64, CancellationToken.None);
        }
        else
        {
            await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        }

        await ExecuteAsync(connection, SessionRows() + $"""
            INSERT INTO SessionAttachments(Id,SessionId,State,LogicalKey,OriginalFileName,Version,RelativePath,ContentSha256,MimeType,ByteLength,Kind,CreatedAt)
            VALUES('{AttachmentOne}','{SessionOne}','Bound','notes','notes.txt',1,'notes.txt','hash','text/plain',6,'Text','2026-01-01T00:00:00.0000000Z');
            """);

        string seed = attachment ? $"""
            WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<2048)
            INSERT INTO session_attachment_chunks(ChunkId,GenerationId,SessionId,AttachmentId,LogicalKey,Version,OriginalFileName,MimeType,ContentSha256,ChunkIndex,CharacterStart,CharacterEnd,StartLine,EndLine,Content,EmbeddingDimension,ExtractedAt,IndexedAt)
            SELECT printf('chunk-%d',v),'generation','{SessionOne}','{AttachmentOne}','notes',1,'notes.txt','text/plain','hash',v,0,6,1,1,'ordinarytoken',64,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM n;
            """ : $"""
            WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<2048)
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
            SELECT printf('%08X-0000-0000-0000-%012X',v,v),'{SessionOne}',0,'ordinarytoken','test','2026-01-01T00:00:00.0000000Z',v FROM n;
            """;

        await ExecuteAsync(connection, seed);

        if (evolved)
        {
            await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, CancellationToken.None);
        }

        string write = attachment
            ? update ? "UPDATE session_attachment_chunks SET Content='measuredtoken' WHERE ChunkId='chunk-1'"
                : AttachmentSql("measured", "measuredtoken")
            : update ? "UPDATE Entries SET Content='measuredtoken' WHERE Id='00000001-0000-0000-0000-000000000001'"
                : EntrySql(EntryOne, 0, "measuredtoken");

        long steps = 0;

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { steps++; return 0; }, null);

        try
        {
            await ExecuteAsync(connection, write);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Assert.Equal(1L, await CountAsync(connection, "mea"));

        _projectionWorkOutput.WriteLine($"Projection work: attachment={attachment}, update={update}, evolved={evolved}, corpus=2048, native VM instructions={steps}.");

        Assert.True(steps <= 5_000, $"A {(update ? "update" : "insert")} into {(attachment ? "attachment chunks" : "Entries")} visited {steps} native VM instructions for one row in a 2,048-row corpus.");
    }
}
