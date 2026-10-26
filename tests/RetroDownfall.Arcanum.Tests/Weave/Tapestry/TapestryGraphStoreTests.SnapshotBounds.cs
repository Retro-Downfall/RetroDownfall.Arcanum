using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    [Theory]
    [InlineData(TapestryScopeKind.Workspace)]
    [InlineData(TapestryScopeKind.Session)]
    [InlineData(TapestryScopeKind.SessionAttachment)]
    public async Task SnapshotPaging_UsesScopeAndIdRangeWithoutSorting(TapestryScopeKind kind)
    {
        TapestryScope scope = await SeedSnapshotScopeAsync(kind);

        await AddSnapshotSourcesAsync(scope, 1, 1200);

        (TapestryBuildSnapshot snapshot, _, List<string> queries) = await MeasureSnapshotAsync(scope);

        Assert.Equal(1200, snapshot.Corpus.LeafCount);

        Assert.Equal(3, queries.Count);

        string id = kind == TapestryScopeKind.Session ? "Id" : "ChunkId";

        foreach ((string query, int page) in queries.Select((query, page) => (query, page)))
        {
            await using DbCommand explain = _db.Database.GetDbConnection().CreateCommand();

            explain.CommandText = "EXPLAIN QUERY PLAN " + query;

            AddSnapshotParameter(explain, "@scopeId", scope.Id);

            AddSnapshotParameter(explain, "@after", page == 0 ? DBNull.Value : SnapshotSourceId(page * 512));

            AddSnapshotParameter(explain, "@limit", 512);

            List<string> details = [];

            await using DbDataReader reader = await explain.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                details.Add(reader.GetString(3));
            }

            string plan = string.Join("; ", details);

            output.WriteLine($"{kind} page {page}: {plan}");

            Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);

            Assert.Contains("tapestry_scope_id", plan, StringComparison.Ordinal);

            if (page > 0)
            {
                Assert.Contains(id + ">?", plan, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(TapestryScopeKind.Workspace)]
    [InlineData(TapestryScopeKind.Session)]
    [InlineData(TapestryScopeKind.SessionAttachment)]
    public async Task SnapshotPaging_DoublingTheCorpusHasLinearVmWork(TapestryScopeKind kind)
    {
        TapestryScope scope = await SeedSnapshotScopeAsync(kind);

        await AddSnapshotSourcesAsync(scope, 1, 10000);

        (TapestryBuildSnapshot small, long smallSteps, _) = await MeasureSnapshotAsync(scope);

        await AddSnapshotSourcesAsync(scope, 10001, 20000);

        (TapestryBuildSnapshot large, long largeSteps, _) = await MeasureSnapshotAsync(scope);

        output.WriteLine($"{kind}: 10,000 rows = {smallSteps:N0}; 20,000 rows = {largeSteps:N0} VM steps.");

        Assert.Equal(10000, small.Corpus.LeafCount);

        Assert.Equal(20000, large.Corpus.LeafCount);

        Assert.Equal(20000, large.CorpusRevision);

        Assert.Equal(0, large.InvalidationRevision);

        Assert.Equal(TapestryHash.OfCorpus(Enumerable.Range(1, 20000).Select(index => (SnapshotSourceId(index), TapestryHash.OfContent("body")))), large.Corpus.Fingerprint);

        Assert.True(largeSteps < smallSteps * 2.5,
            $"{kind}: doubling 10,000 to 20,000 rows took {smallSteps:N0} then {largeSteps:N0} VM steps.");

        Assert.True(largeSteps < 20000L * 256,
            $"{kind}: a 20,000-row snapshot exceeded its linear VM budget: {largeSteps:N0} steps.");
    }

    private async Task<(TapestryBuildSnapshot Snapshot, long Steps, List<string> Queries)> MeasureSnapshotAsync(TapestryScope scope)
    {
        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        long steps = 0;

        List<string> queries = [];

        raw.sqlite3_progress_handler(connection.Handle, 100, (object _) => { steps += 100; return 0; }, null);

        raw.sqlite3_trace(connection.Handle, (object _, string sql) =>
        {
            if (sql.Contains("SELECT s.", StringComparison.Ordinal) && sql.Contains("s.Content FROM", StringComparison.Ordinal))
            {
                queries.Add(sql);
            }
        }, null);

        try
        {
            TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(scope, CancellationToken.None);

            return (snapshot, steps, queries);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);

            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }
    }

    private async Task<TapestryScope> SeedSnapshotScopeAsync(TapestryScopeKind kind)
    {
        const string session = "AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA";

        await ExecuteAsync($"""
            INSERT INTO Sessions(Id,Status,CreatedAt,UpdatedAt) VALUES('{session}','active','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
            INSERT INTO SessionAttachments(Id,SessionId,State,LogicalKey,OriginalFileName,Version,RelativePath,ContentSha256,MimeType,ByteLength,Kind,CreatedAt)
            VALUES('{session}','{session}','Bound','snapshot','snapshot.txt',1,'snapshot.txt','hash','text/plain',4,'Text','2026-01-01T00:00:00.0000000Z');
            """);

        return kind switch
        {
            TapestryScopeKind.Workspace => new(kind, "/snapshot"),
            TapestryScopeKind.Session => TapestryScope.ForSession(Guid.Parse(session)),
            _ => TapestryScope.ForSessionAttachment(Guid.Parse(session)),
        };
    }

    private Task AddSnapshotSourcesAsync(TapestryScope scope, int first, int last)
    {
        string rows = $"WITH RECURSIVE numbers(n) AS (VALUES({first}) UNION ALL SELECT n+1 FROM numbers WHERE n<{last}) ";

        const string id = "printf('%08X-1111-4111-8111-111111111111',n)";

        string insert = scope.Kind switch
        {
            TapestryScopeKind.Workspace => $"INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt) SELECT {id},'{scope.Id}','a.cs',n,'body',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM numbers",
            TapestryScopeKind.Session => $"INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) SELECT {id},'{scope.Id}',0,'body','model','2026-01-01T00:00:00.0000000Z',n FROM numbers",
            _ => $"INSERT INTO session_attachment_chunks(ChunkId,SessionId,AttachmentId,LogicalKey,Version,OriginalFileName,MimeType,ContentSha256,ChunkIndex,CharacterStart,CharacterEnd,StartLine,EndLine,Content,EmbeddingDimension,ExtractedAt,IndexedAt,RetrievalScope) SELECT {id},'{scope.Id}','AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA','snapshot',1,'snapshot.txt','text/plain','hash',n,0,4,1,1,'body',8,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z','{scope.Id}' FROM numbers",
        };

        return ExecuteAsync(rows + insert);
    }

    private static string SnapshotSourceId(int index) => index.ToString("X8", CultureInfo.InvariantCulture) + "-1111-4111-8111-111111111111";

    private static void AddSnapshotParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);
    }
}
