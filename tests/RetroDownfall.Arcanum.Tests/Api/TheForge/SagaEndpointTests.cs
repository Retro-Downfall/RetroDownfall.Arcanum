using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

/// <summary>
/// RAG Phase 4 — <c>/api/saga</c> endpoint integration tests (list, divine, delete, delete-all, stats).
/// </summary>
[Collection("ApiHost")]
public sealed class SagaEndpointTests
{

    /// <summary>
    /// Matches ArcanumSettingClamps.EmbeddingsDimensions' 64-dimension floor, so SagaMemoryStore's
    /// dimension-validation guard (see InsertAsync) does not reject test inserts.
    /// </summary>
    private const int TestDimensions = 64;

    private readonly ArcanumWebApplicationFactory _factory;

    public SagaEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task List_ReturnsSeededMemories_FilteredBySessionAndQuery()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionA = Guid.NewGuid();

        Guid sessionB = Guid.NewGuid();

        await SeedMemoryAsync(factory, "mem-a", "prefers dark mode", sessionA);

        await SeedMemoryAsync(factory, "mem-b", "uses xUnit for tests", sessionB);

        HttpResponseMessage all = await client.GetAsync("/api/saga");

        Assert.Equal(HttpStatusCode.OK, all.StatusCode);

        SagaMemoryDto[] allMemories = await ReadListAsync(all);

        Assert.Equal(2, allMemories.Length);

        HttpResponseMessage bySession = await client.GetAsync($"/api/saga?sessionId={sessionA:D}");

        SagaMemoryDto[] sessionMemories = await ReadListAsync(bySession);

        Assert.Single(sessionMemories);

        Assert.Equal("mem-a", sessionMemories[0].Id);

        HttpResponseMessage byQuery = await client.GetAsync("/api/saga?q=xunit");

        SagaMemoryDto[] queryMemories = await ReadListAsync(byQuery);

        Assert.Single(queryMemories);

        Assert.Equal("mem-b", queryMemories[0].Id);

    }

    [SkippableFact]
    public async Task List_RespectsLimitAndOffset()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        for (int i = 0; i < 3; i++)
        {

            await SeedMemoryAsync(factory, $"mem-{i}", $"memory {i}", sessionId: null);

        }

        HttpResponseMessage response = await client.GetAsync("/api/saga?limit=2&offset=0");

        SagaMemoryDto[] page = await ReadListAsync(response);

        Assert.Equal(2, page.Length);

    }

    [SkippableFact]
    public async Task List_QueryTooLong_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        string oversizedQuery = new('x', 4_097);

        HttpResponseMessage response = await client.GetAsync($"/api/saga?q={oversizedQuery}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SagaMemoryDto[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal("Validation.InvalidBody", body.Error?.Code);

    }

    [SkippableFact]
    public async Task Divine_WhenDisabled_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostDivineAsync(client, new SagaSearchRequest("hello"));

        // FeatureDisabled means an operator turned Saga off in config, not that the caller lacks
        // permission, so it maps to 503 (retry later) rather than 403.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await AssertDivineErrorCodeAsync(response, "Embeddings.FeatureDisabled");

    }

    [SkippableFact]
    public async Task Divine_EmptyQuery_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostDivineAsync(client, new SagaSearchRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await AssertDivineErrorCodeAsync(response, "Validation.InvalidBody");

    }

    [SkippableFact]
    public async Task Divine_ProviderUnavailable_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService { Available = false });

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostDivineAsync(client, new SagaSearchRequest("hello"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await AssertDivineErrorCodeAsync(response, "Embeddings.ProviderUnavailable");

    }

    [SkippableFact]
    public async Task Divine_DivinationSearchFails_PreservesOriginalErrorCode_NotRemappedTo500()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // A divination search failure (e.g. the embedding provider going down mid-search) must
        // surface its own error code/status (Embeddings.ProviderUnavailable -> 503) rather than
        // being remapped to the generic Saga.SearchFailed (-> 500) — matching
        // SessionDivinationEndpoints' identical divination-failure handling, and preserving the
        // real "provider is down, try again" signal instead of hiding it behind an opaque 500.
        FakeWeaveService weave = new() { QueryVector = Vec(1f) };

        FailingDivinationService divination = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Embeddings = settings.Embeddings with
                {
                    Enabled = true,
                    SagaEnabled = true,
                    Provider = "test",
                    Model = "test-embed",
                    SimilarityThreshold = 0f,
                    Dimensions = TestDimensions,
                },
            },
            ServiceOverrides = services =>
            {

                services.RemoveAll<IWeaveService>();

                services.AddSingleton<IWeaveService>(weave);

                services.RemoveAll<IDivinationService>();

                services.AddScoped<IDivinationService>(_ => divination);

            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostDivineAsync(client, new SagaSearchRequest("hello"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await AssertDivineErrorCodeAsync(response, "Embeddings.ProviderUnavailable");

    }

    [SkippableFact]
    public async Task Divine_HappyPath_ReturnsMemoriesWithSimilarities()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWeaveService weave = new() { QueryVector = Vec(1f) };

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(weave);

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-1", "the operator prefers dark mode", sessionId: null, vector: Vec(1f));

        HttpResponseMessage response = await PostDivineAsync(client, new SagaSearchRequest("what theme do I like?"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SagaSearchResult result = await ReadDivineResultAsync(response);

        SagaMemoryDto memory = Assert.Single(result.Memories);

        Assert.Equal("mem-1", memory.Id);

        Assert.Single(result.Similarities);

        Assert.True(result.Similarities[0] > 0.99f);

    }

    [SkippableFact]
    public async Task DeleteSingle_RemovesMemory_ReturnsNoContent_404WhenMissing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-1", "to be deleted", sessionId: null);

        HttpResponseMessage deleteResponse = await client.DeleteAsync("/api/saga/mem-1");

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        HttpResponseMessage listResponse = await client.GetAsync("/api/saga");

        Assert.Empty(await ReadListAsync(listResponse));

        HttpResponseMessage missingResponse = await client.DeleteAsync("/api/saga/mem-1");

        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);

    }

    [SkippableFact]
    public async Task DeleteAll_RequiresConfirm_400WithoutConfirm_204WithConfirm()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        await SeedMemoryAsync(factory, "mem-1", "one", sessionId: null);

        await SeedMemoryAsync(factory, "mem-2", "two", sessionId: null);

        HttpResponseMessage withoutConfirm = await client.DeleteAsync("/api/saga");

        Assert.Equal(HttpStatusCode.BadRequest, withoutConfirm.StatusCode);

        string json = await withoutConfirm.Content.ReadAsStringAsync();

        ApiResponse<string>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseString);

        Assert.Equal("Saga.NotEmpty", body?.Error?.Code);

        HttpResponseMessage withConfirm = await client.DeleteAsync("/api/saga?confirm=true");

        Assert.Equal(HttpStatusCode.NoContent, withConfirm.StatusCode);

        HttpResponseMessage listResponse = await client.GetAsync("/api/saga");

        Assert.Empty(await ReadListAsync(listResponse));

    }

    [SkippableFact]
    public async Task Stats_ReportsCountsAndSessionCount()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionA = Guid.NewGuid();

        await SeedMemoryAsync(factory, "mem-1", "one", sessionA);

        await SeedMemoryAsync(factory, "mem-2", "two", Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync("/api/saga/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SagaStats>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSagaStats);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.Equal(2, body.Data!.TotalCount);

        Assert.Equal(2, body.Data.SessionCount);

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory(IWeaveService weaveService) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Embeddings = settings.Embeddings with
                {
                    Enabled = true,
                    SagaEnabled = true,
                    Provider = "test",
                    Model = "test-embed",
                    SimilarityThreshold = 0f,
                    Dimensions = TestDimensions,
                },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<IWeaveService>();

                services.AddSingleton(weaveService);

            },
        };

    private static async Task SeedMemoryAsync(
        ArcanumWebApplicationFactory factory,
        string id,
        string content,
        Guid? sessionId,
        float[]? vector = null)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        ISagaMemoryStore store = scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>();

        await store.InsertAsync(
            id,
            content,
            DateTimeOffset.UtcNow,
            sessionId,
            tags: null,
            source: "extraction",
            vector ?? Vec(1f),
            CancellationToken.None);

    }

    /// <summary>Builds a <see cref="TestDimensions"/>-length vector with <paramref name="leading"/> in its first slots and zeros elsewhere.</summary>
    private static float[] Vec(params float[] leading)
    {

        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;

    }

    private static async Task<HttpResponseMessage> PostDivineAsync(HttpClient client, SagaSearchRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.SagaSearchRequest);

        return await client.PostAsync("/api/saga/divine", new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    private static async Task<SagaMemoryDto[]> ReadListAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SagaMemoryDto[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        return body.Data ?? [];

    }

    private static async Task<SagaSearchResult> ReadDivineResultAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SagaSearchResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSagaSearchResult);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data!;

    }

    private static async Task AssertDivineErrorCodeAsync(HttpResponseMessage response, string expectedCode)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SagaSearchResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSagaSearchResult);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(expectedCode, body.Error?.Code);

    }

    private sealed class FailingDivinationService : IDivinationService
    {

        public Task<Result<DivinationResult[]>> SearchAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<DivinationResult[]>.Failure(
                new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated divination provider outage.")));

        public Task<Result<DivinationResult[]>> SearchScopedAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            string scopeTableName,
            string scopeJoinColumn,
            string scopeFilterColumn,
            string scopeFilterValue,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<DivinationResult[]>.Failure(
                new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated divination provider outage.")));

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public float[] QueryVector { get; set; } = Vec(1f);

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(QueryVector)));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the Saga endpoints.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the Saga endpoints.");

    }

}
