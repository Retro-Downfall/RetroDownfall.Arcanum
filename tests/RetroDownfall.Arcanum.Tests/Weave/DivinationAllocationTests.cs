using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Weave;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class DivinationAllocationTests(GrimoireFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private const int Dimensions = 768;

    private const int DistractorCount = 1024;

    private const long AllocationBudget = 768 * 1024;

    private ArcanumDbContext? _db;

    public Task InitializeAsync()
    {
        _db = fixture.CreateContext(fixture.CopyDatabase());

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task General_managed_scan_does_not_allocate_a_vector_blob_per_row(bool secondQuery)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCorpusAsync();

        DivinationService service = new(_db!, NullLogger<DivinationService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        Embedding<float> query = Query(secondQuery);

        Task<Result<DivinationResult[]>> Search() => service.SearchAsync(
            "entry_embeddings", "EntryId", "Embedding", query, 4, 0.1f, CancellationToken.None);

        Assert.True((await Search()).IsSuccess);

        (Result<DivinationResult[]> result, long allocated) = MeasureSynchronousScan(Search);

        Assert.True(result.IsSuccess);

        Assert.Equal(Expected(secondQuery), result.Value.Select(static hit => (hit.Id,
            BitConverter.SingleToInt32Bits(hit.Similarity))).ToArray());

        output.WriteLine($"General scan allocated {allocated:N0} bytes for {DistractorCount + 5} {Dimensions}-dimension rows.");

        Assert.True(allocated < AllocationBudget,
            $"Managed scan allocated {allocated:N0} bytes; budget {AllocationBudget:N0}. A 768-dimension BLOB copy per row alone exceeds 3 MiB.");
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_managed_scan_does_not_allocate_a_vector_blob_per_row(bool secondQuery)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCorpusAsync();

        SessionDivinationService service = new(_db!, FixtureOrdinaryConnectionFactory.For(_db!),
            NullLogger<SessionDivinationService>.Instance);

        Embedding<float> query = Query(secondQuery);

        Task<Result<SemanticSearchResult>> Search() => service.SearchAsync(
            query, null, null, 4, 0.1f, CancellationToken.None);

        Assert.True((await Search()).IsSuccess);

        (Result<SemanticSearchResult> result, long allocated) = MeasureSynchronousScan(Search);

        Assert.True(result.IsSuccess);

        Assert.Equal(Expected(secondQuery), result.Value.Results.Select(static hit =>
            (hit.EntryId.ToString("D").ToUpperInvariant(), BitConverter.SingleToInt32Bits(hit.Similarity))).ToArray());

        Assert.False(result.Value.HasMore);

        output.WriteLine($"Session scan allocated {allocated:N0} bytes for {DistractorCount + 5} {Dimensions}-dimension rows.");

        Assert.True(allocated < AllocationBudget,
            $"Session scan allocated {allocated:N0} bytes; budget {AllocationBudget:N0}. A 768-dimension BLOB copy per row alone exceeds 3 MiB.");
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_scans_preserve_unreadable_dimension_and_no_signal_classification(bool sessionSearch)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCorpusAsync();

        await using SqliteCommand corrupt = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        corrupt.CommandText = """
            UPDATE entry_embeddings SET Embedding = CASE EntryId
                WHEN '00000001-0000-4000-8000-000000000000' THEN @text
                WHEN '00000002-0000-4000-8000-000000000000' THEN 42
                WHEN '00000003-0000-4000-8000-000000000000' THEN X'010203'
                WHEN '00000004-0000-4000-8000-000000000000' THEN @short
                WHEN '00000005-0000-4000-8000-000000000000' THEN @zero
                WHEN '00000006-0000-4000-8000-000000000000' THEN @nan
                ELSE Embedding END;
            """;

        // The text has a valid vector byte length. Calling sqlite3_column_blob before checking
        // the storage class would coerce it into bytes and misclassify it as a usable vector.
        corrupt.Parameters.AddWithValue("@text", new string('A', Dimensions * sizeof(float)));

        corrupt.Parameters.AddWithValue("@short", EmbeddingBlobCodec.Encode([1f, 0f]));

        corrupt.Parameters.AddWithValue("@zero", EmbeddingBlobCodec.Encode(new float[Dimensions]));

        corrupt.Parameters.AddWithValue("@nan", EmbeddingBlobCodec.Encode(Padded([float.NaN])));

        _ = await corrupt.ExecuteNonQueryAsync();

        string warning;

        if (sessionSearch)
        {
            TestCapturingLogger<SessionDivinationService> logger = new();

            SessionDivinationService service = new(_db!, FixtureOrdinaryConnectionFactory.For(_db!), logger);

            Result<SemanticSearchResult> result = await service.SearchAsync(Query(false), null, null, 4, 0.1f, CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal(Expected(false), result.Value.Results.Select(static hit =>
                (hit.EntryId.ToString("D").ToUpperInvariant(), BitConverter.SingleToInt32Bits(hit.Similarity))).ToArray());

            warning = Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning).Message;
        }
        else
        {
            TestCapturingLogger<DivinationService> logger = new();

            DivinationService service = new(_db!, logger,
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            Result<DivinationResult[]> result = await service.SearchAsync(
                "entry_embeddings", "EntryId", "Embedding", Query(false), 4, 0.1f, CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal(Expected(false), result.Value.Select(static hit =>
                (hit.Id, BitConverter.SingleToInt32Bits(hit.Similarity))).ToArray());

            warning = Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning).Message;
        }

        Assert.Contains("1 of another width, 3 unreadable, 2 with no direction", warning, StringComparison.Ordinal);
    }

    private static (T Result, long Allocated) MeasureSynchronousScan<T>(Func<Task<T>> search)
    {
        // Microsoft.Data.Sqlite executes its asynchronous API synchronously. Require that property
        // here so a future asynchronous dependency cannot silently undercount another thread's work.
        long before = GC.GetAllocatedBytesForCurrentThread();

        Task<T> pending = search();

        Assert.True(pending.IsCompletedSuccessfully, "Allocation measurement requires a synchronous SQLite scan.");

        T result = pending.GetAwaiter().GetResult();

        return (result, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static Embedding<float> Query(bool secondQuery) => new(Padded(
        secondQuery ? [0.25f, 0.5f, 0.75f] : [1f, 0f, 0f]));

    private static (string Id, int SimilarityBits)[] Expected(bool secondQuery) => secondQuery
        ? [(Id(4), 0x3F6DB6DB), (Id(2), 0x3EA63926), (Id(3), 0x3EA63926), (Id(1), 0x3E88D677)]
        : [(Id(1), 0x3F800000), (Id(2), 0x3F7E6F2A), (Id(3), 0x3F7E6F2A), (Id(4), 0x3E88D677)];

    private static string Id(int value) => $"A000000{value}-0000-4000-8000-000000000000";

    private static float[] Padded(float[] prefix)
    {
        float[] vector = new float[Dimensions];

        prefix.CopyTo(vector, 0);

        return vector;
    }

    private async Task SeedCorpusAsync()
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Sessions(Id,Title,Status,CreatedAt,UpdatedAt)
            VALUES ('B0000000-0000-4000-8000-000000000000','Allocation corpus','active',@time,@time);
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM rows WHERE n < @count)
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
            SELECT printf('%08X-0000-4000-8000-000000000000',n),
                'B0000000-0000-4000-8000-000000000000',1,'Orthogonal distractor','',@time,n FROM rows;
            INSERT INTO entry_embeddings(EntryId,Embedding,Dim)
            SELECT Id,@orthogonal,768 FROM Entries;
            """;

        command.Parameters.AddWithValue("@time", "2026-10-26T12:00:00.0000000Z");

        command.Parameters.AddWithValue("@count", DistractorCount);

        command.Parameters.AddWithValue("@orthogonal", EmbeddingBlobCodec.Encode(Padded([0f, 0f, 0f, 1f])));

        _ = await command.ExecuteNonQueryAsync();

        float[][] prefixes = [[1f, 0f, 0f], [0.9f, 0.1f, 0f], [0.9f, 0.1f, 0f], [0.25f, 0.75f, 0.5f], [-1f, 0f, 0f]];

        for (int i = 0; i < prefixes.Length; i++)
        {
            await using SqliteCommand insert = connection.CreateCommand();

            insert.CommandText = """
                INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
                VALUES (@id,'B0000000-0000-4000-8000-000000000000',1,'Frozen winner','',@time,@sequence);
                INSERT INTO entry_embeddings(EntryId,Embedding,Dim) VALUES (@id,@vector,768);
                """;

            insert.Parameters.AddWithValue("@id", Id(i + 1));

            insert.Parameters.AddWithValue("@time", "2026-10-26T12:00:00.0000000Z");

            insert.Parameters.AddWithValue("@sequence", DistractorCount + i + 1);

            insert.Parameters.AddWithValue("@vector", EmbeddingBlobCodec.Encode(Padded(prefixes[i])));

            _ = await insert.ExecuteNonQueryAsync();
        }
    }
}
