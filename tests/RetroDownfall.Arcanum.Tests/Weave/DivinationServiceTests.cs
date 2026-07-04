using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class DivinationServiceTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public DivinationServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]
    public async Task SearchAsync_ManagedFallback_ReturnsResultsAboveThreshold_OrderedBySimilarity()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await InsertEmbeddingAsync("close", [1f, 0f, 0f]);

        await InsertEmbeddingAsync("far", [0f, 1f, 0f]);

        await InsertEmbeddingAsync("closer", [0.9f, 0.1f, 0f]);

        DivinationService service = CreateService(vecAvailable: false);

        Embedding<float> query = new(new float[] { 1f, 0f, 0f });

        Result<DivinationResult[]> result = await service.SearchAsync(
            "entry_embeddings_vec",
            "EntryId",
            "Embedding",
            query,
            maxResults: 10,
            similarityThreshold: 0.5f,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        DivinationResult[] hits = result.Value;

        // "far" (orthogonal, similarity 0) is filtered out by the 0.5 threshold.
        Assert.Equal(2, hits.Length);

        Assert.Equal("close", hits[0].Id);

        Assert.Equal("closer", hits[1].Id);

        Assert.True(hits[0].Similarity >= hits[1].Similarity);

    }

    [SkippableFact]
    public async Task SearchAsync_ManagedFallback_FiltersBelowSimilarityThreshold()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await InsertEmbeddingAsync("orthogonal", [0f, 1f, 0f]);

        DivinationService service = CreateService(vecAvailable: false);

        Embedding<float> query = new(new float[] { 1f, 0f, 0f });

        Result<DivinationResult[]> result = await service.SearchAsync(
            "entry_embeddings_vec",
            "EntryId",
            "Embedding",
            query,
            maxResults: 10,
            similarityThreshold: 0.5f,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(result.Value);

    }

    [SkippableFact]
    public async Task SearchAsync_ManagedFallback_RespectsMaxResults()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await InsertEmbeddingAsync("a", [1f, 0f, 0f]);

        await InsertEmbeddingAsync("b", [0.99f, 0.01f, 0f]);

        await InsertEmbeddingAsync("c", [0.98f, 0.02f, 0f]);

        DivinationService service = CreateService(vecAvailable: false);

        Embedding<float> query = new(new float[] { 1f, 0f, 0f });

        Result<DivinationResult[]> result = await service.SearchAsync(
            "entry_embeddings_vec",
            "EntryId",
            "Embedding",
            query,
            maxResults: 2,
            similarityThreshold: 0f,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, result.Value.Length);

    }

    [SkippableFact]
    public async Task SearchAsync_VecClaimedAvailableButTableMissing_NeverThrows_ReturnsFailure()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Phase 1 ships managed-only (no sqlite-vec native asset), so entry_embeddings_vec never
        // actually exists — this exercises the "vec0 claimed available but genuinely isn't usable"
        // path and asserts it degrades to a Result.Failure rather than throwing.
        DivinationService service = CreateService(vecAvailable: true);

        Embedding<float> query = new(new float[] { 1f, 0f, 0f });

        Result<DivinationResult[]> result = await service.SearchAsync(
            "entry_embeddings_vec",
            "EntryId",
            "Embedding",
            query,
            maxResults: 10,
            similarityThreshold: 0.5f,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.ProviderUnavailable, result.Error.Code);

    }

    private DivinationService CreateService(bool vecAvailable)
    {

        WeaveIndexAvailability availability = new();

        availability.SetAvailable(vecAvailable);

        return new DivinationService(_db!, availability, NullLogger<DivinationService>.Instance);

    }

    private async Task InsertEmbeddingAsync(string entryId, float[] vector)
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            INSERT INTO "entry_embeddings" ("EntryId", "Embedding", "Dim")
            VALUES (@id, @embedding, @dim);
            """;

        DbParameter idParam = cmd.CreateParameter();

        idParam.ParameterName = "@id";

        idParam.Value = entryId;

        cmd.Parameters.Add(idParam);

        DbParameter embeddingParam = cmd.CreateParameter();

        embeddingParam.ParameterName = "@embedding";

        embeddingParam.Value = EmbeddingBlobCodec.Encode(vector);

        cmd.Parameters.Add(embeddingParam);

        DbParameter dimParam = cmd.CreateParameter();

        dimParam.ParameterName = "@dim";

        dimParam.Value = vector.Length;

        cmd.Parameters.Add(dimParam);

        _ = await cmd.ExecuteNonQueryAsync();

    }

}
