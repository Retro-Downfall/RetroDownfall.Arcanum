using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>Operator reset endpoint backing service — clears embedding tables and companion metadata.</summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class EmbeddingsResetServiceTests : IAsyncLifetime
{

    private const int TestDimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SagaMemoryStore? _sagaStore;

    private EmbeddingsResetService? _resetService;

    public EmbeddingsResetServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        WeaveIndexAvailability availability = new();

        _sagaStore = new SagaMemoryStore(
            _db,
            availability,
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings
                        {
                            Dimensions = TestDimensions,
                        },
                    },
                }));

        _resetService = new EmbeddingsResetService(_db, availability);

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

    private static float[] Vec(params float[] leading)
    {

        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;

    }

    [SkippableFact]
    public async Task ResetAsync_SagaScope_ClearsMemoriesAndEmbeddingsAndWatermarks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await _sagaStore!.InsertAsync(
            "mem-1",
            "a",
            DateTimeOffset.UtcNow,
            sessionId,
            null,
            "extraction",
            Vec(1f),
            CancellationToken.None);

        await _sagaStore.SetWatermarkAsync(sessionId, DateTimeOffset.UtcNow, CancellationToken.None);

        EmbeddingsResetResult result = await _resetService!.ResetAsync(EmbeddingsResetScope.Saga, CancellationToken.None);

        Assert.Equal(0, await _sagaStore.CountAsync(CancellationToken.None));

        Assert.Null(await _sagaStore.GetWatermarkAsync(sessionId, CancellationToken.None));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_memories"));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_memory_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_extraction_watermarks"));

    }

    [SkippableFact]
    public async Task ResetAsync_AllScope_CoversSagaTables()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await _sagaStore!.InsertAsync(
            "mem-2",
            "b",
            DateTimeOffset.UtcNow,
            sessionId,
            null,
            "extraction",
            Vec(2f),
            CancellationToken.None);

        EmbeddingsResetResult result = await _resetService!.ResetAsync(EmbeddingsResetScope.All, CancellationToken.None);

        Assert.Equal(0, await _sagaStore.CountAsync(CancellationToken.None));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_memories"));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_memory_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("entry_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("workspace_file_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("workspace_file_chunks"));

        Assert.True(result.DeletedRowCounts.ContainsKey("session_attachment_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("session_attachment_chunks"));

        Assert.True(result.DeletedRowCounts.ContainsKey("session_attachment_index_state"));

        Assert.True(result.DeletedRowCounts.ContainsKey("tapestry_generations"));

        Assert.True(result.DeletedRowCounts.ContainsKey("tapestry_nodes"));

        Assert.True(result.DeletedRowCounts.ContainsKey("tapestry_node_embeddings"));

    }

    [SkippableFact]
    public async Task ResetAsync_TapestryScope_DropsTreeTablesAndNothingElse()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await _sagaStore!.InsertAsync(
            "mem-3",
            "c",
            DateTimeOffset.UtcNow,
            sessionId,
            null,
            "extraction",
            Vec(3f),
            CancellationToken.None);

        EmbeddingsResetResult result = await _resetService!.ResetAsync(
            EmbeddingsResetScope.Tapestry,
            CancellationToken.None);

        // Trees are derived data: dropping them must not touch the leaf corpora they were woven from,
        // or any other feature's embeddings.
        Assert.Equal(1, await _sagaStore.CountAsync(CancellationToken.None));

        Assert.Equal(
            [
                "tapestry_generations",
                "tapestry_node_embeddings",
                "tapestry_node_embeddings_vec",
                "tapestry_nodes",
            ],
            result.DeletedRowCounts.Keys.Order(StringComparer.Ordinal));

    }

}
