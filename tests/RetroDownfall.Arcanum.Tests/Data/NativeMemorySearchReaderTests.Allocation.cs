using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class NativeMemorySearchReaderTests
{
    [Fact]
    public async Task Summary_digest_validation_never_materializes_content_during_the_metadata_scan()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(default);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, default);

        string content = "ä" + new string('x', 512 * 1024);

        await using SqliteCommand seed = connection.CreateCommand();

        seed.CommandText = """
            WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<16)
            INSERT INTO Sessions(Id,Title,CreatedAt,UpdatedAt,Summary)
            SELECT printf('00000000-0000-4000-8000-%012X',v),'ordinary',
                '2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z',@content FROM n;
            INSERT INTO session_summary_artifacts(ArtifactId,SessionId,Revision,ContentDigest,SensitivityCode,
                SensitivityDigest,SummarizedThroughUtc,CreatedAtUtc)
            SELECT Id,Id,1,@digest,0,zeroblob(32),NULL,CreatedAt FROM Sessions;
            INSERT INTO session_summary_state(SessionId,CurrentArtifactId,Revision,UpdatedAtUtc)
            SELECT Id,Id,1,UpdatedAt FROM Sessions;
            """;

        seed.Parameters.AddWithValue("@content", content);

        seed.Parameters.AddWithValue("@digest", DerivedArtifactContentDigest.ForText(content).Bytes.ToArray());

        await seed.ExecuteNonQueryAsync();

        bool metadataActive = false;

        int metadataStatements = 0;

        int metadataDigests = 0;

        int chargedDigests = 0;

        connection.CreateFunction<byte[]?, byte[]?, bool>("arcanum_derived_content_matches", (value, digest) =>
        {
            if (metadataActive) metadataDigests++;
            else chargedDigests++;

            return value is not null && digest is { Length: 32 }
                && DerivedArtifactContentDigest.ForBytes(value).Bytes.AsSpan().SequenceEqual(digest);
        }, isDeterministic: true);

        raw.sqlite3_trace(connection.Handle, (object _, string sql) =>
        {
            metadataActive = sql.StartsWith("SELECT d.rowid,", StringComparison.Ordinal);

            if (metadataActive) metadataStatements++;
        }, null);

        try
        {
            var page = await NativeMemorySearchReader.ReadAsync(connection, "ä", MemorySearchScope.Session,
                null, null, new(1), 1, default, modelDisclosure: true, contentByteLimit: 1024, pageByteLimit: 2048);

            Assert.True(page.IsSuccess, page.Error.Message);

            Assert.Single(page.Value.Hits);

            Assert.True(metadataStatements > 0);

            Assert.Equal(0, metadataDigests);

            Assert.InRange(chargedDigests, 1, 8);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }
    }

    [Fact]
    public async Task Model_page_materializes_only_its_admitted_results_and_keeps_every_undisclosed_match()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(default);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, default);

        await using SqliteCommand seed = connection.CreateCommand();

        seed.CommandText = """
            WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<512)
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            SELECT printf('%064d',v),'/allocation','same.cs',v,@content,0,1200,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM n;
            """;

        seed.Parameters.AddWithValue("@content", "needle " + new string('x', 1200));

        await seed.ExecuteNonQueryAsync();

        int materialized = 0;

        long materializedBytes = 0;

        using IDisposable seam = GrimoireScopedConsumerTestSeam.Override(NativeMemorySearchReader.HydrationCheckpoint(connection),
            (observed, _) =>
            {
                materialized++;

                materializedBytes += observed.Observation;

                return ValueTask.CompletedTask;
            });

        var first = await NativeMemorySearchReader.ReadAsync(connection, "needle", MemorySearchScope.Workspace,
            null, ("/allocation", "Allocation"), new(3), 2, default, modelDisclosure: true, contentByteLimit: 1024, pageByteLimit: 8192);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(2, first.Value.Hits.Length);

        Assert.Equal(2, materialized);

        Assert.InRange(materializedBytes, 1, 2048);

        Assert.Equal(MemorySearchTruncationReason.ResultLimit, first.Value.Reason);

        HashSet<string> ids = [.. first.Value.Hits.Select(hit => hit.Result.SourceId)];

        MemorySearchPosition position = first.Value.Position;

        for (int pageNumber = 0; pageNumber < 300; pageNumber++)
        {
            materialized = 0;

            materializedBytes = 0;

            var page = await NativeMemorySearchReader.ReadAsync(connection, "needle", MemorySearchScope.Workspace,
                null, ("/allocation", "Allocation"), position, 2, default, modelDisclosure: true, contentByteLimit: 1024, pageByteLimit: 8192);

            Assert.True(page.IsSuccess, page.Error.Message);

            Assert.InRange(materialized, 0, 2);

            Assert.InRange(materializedBytes, 0, 2048);

            foreach (NativeMemorySearchHit hit in page.Value.Hits) Assert.True(ids.Add(hit.Result.SourceId));

            if (page.Value.Reason is null) break;

            position = page.Value.Position;
        }

        Assert.Equal(512, ids.Count);
    }
}
