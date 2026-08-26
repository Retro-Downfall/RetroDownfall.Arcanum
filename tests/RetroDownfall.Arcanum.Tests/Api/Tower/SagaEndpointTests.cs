using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

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
                Features = settings.Features with
                {
                    Embeddings = true,
                    Saga = true,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                        Dimensions = TestDimensions,
                    },
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

    /// <summary>
    /// The acceptance criterion, through the route an operator and a client both reach: a memory
    /// extracted in one Campaign is not returned to a search made as a session in another.
    /// </summary>
    /// <remarks>
    /// Every memory here is written through the real store, so its scope is the one production derives
    /// from the owning Session's binding rather than one the test stated. The two Campaign memories and
    /// the installation-scoped one share a vector, so similarity cannot be what separates the results —
    /// what a search returns, or fails to return, is the scope predicate's doing.
    /// </remarks>
    [SkippableFact]
    public async Task Divine_WhenCampaignScopingIsOn_ReturnsThisSessionsCampaignAndTheGlobalMemoriesOnly()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid campaignA = new("A0000000-0000-4000-8000-0000000000C1");

        Guid campaignB = new("B0000000-0000-4000-8000-0000000000C2");

        await using ArcanumWebApplicationFactory factory =
            CreateEnabledFactory(new FakeWeaveService(), campaignScopedMemory: true);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionA = await SeedCampaignSessionAsync(factory, campaignA);

        Guid sessionB = await SeedCampaignSessionAsync(factory, campaignB);

        await SeedMemoryAsync(factory, "mem-a", "campaign A concluded something", sessionA);

        await SeedMemoryAsync(factory, "mem-b", "campaign B concluded something", sessionB);

        await SeedMemoryAsync(factory, "mem-global", "an installation-scoped conclusion", sessionId: null);

        Assert.Equal(
            ["mem-a", "mem-global"],
            await DivineIdsAsync(client, sessionA));

        Assert.Equal(
            ["mem-b", "mem-global"],
            await DivineIdsAsync(client, sessionB));

    }

    /// <summary>
    /// With the gate off the same route returns every memory, which is what it has always returned.
    /// </summary>
    [SkippableFact]
    public async Task Divine_WhenCampaignScopingIsOff_ReturnsEveryMemoryRegardlessOfCampaign()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid campaignA = new("A0000000-0000-4000-8000-0000000000C3");

        Guid campaignB = new("B0000000-0000-4000-8000-0000000000C4");

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionA = await SeedCampaignSessionAsync(factory, campaignA);

        Guid sessionB = await SeedCampaignSessionAsync(factory, campaignB);

        await SeedMemoryAsync(factory, "mem-a", "campaign A concluded something", sessionA);

        await SeedMemoryAsync(factory, "mem-b", "campaign B concluded something", sessionB);

        Assert.Equal(
            ["mem-a", "mem-b"],
            await DivineIdsAsync(client, sessionA));

    }

    /// <summary>The ids a divination returns, ordered so a comparison is about membership, not rank.</summary>
    private static async Task<string[]> DivineIdsAsync(HttpClient client, Guid? sessionId)
    {

        using HttpResponseMessage response = await client.PostAsync(
            "/api/saga/divine",
            new StringContent(
                JsonSerializer.Serialize(
                    new SagaSearchRequest("what did we decide?", null, sessionId),
                    ArcanumJsonContext.Default.SagaSearchRequest),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SagaSearchResult>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSagaSearchResult);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess);

        return [.. body.Data!.Memories.Select(static memory => memory.Id).Order(StringComparer.Ordinal)];

    }

    /// <summary>
    /// A Campaign and a Session bound to it, written the way production states a Session's authority.
    /// </summary>
    /// <remarks>
    /// The binding goes through the same false-by-default write scope production borrows: nothing may
    /// state what a Session is bound to without it, this suite included.
    /// </remarks>
    private static async Task<Guid> SeedCampaignSessionAsync(
        ArcanumWebApplicationFactory factory,
        Guid campaignId)
    {

        Guid sessionId = Guid.NewGuid();

        string now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(CancellationToken.None);

        }

        await ExecuteAsync(
            connection,
            """
            INSERT OR IGNORE INTO "Campaigns"
                ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, $name, $name, $path, 0, '{}', $now, $now);
            """,
            ("$id", campaignId.ToString()),
            ("$name", campaignId.ToString("N")),
            ("$path", $"/campaigns/{campaignId:N}"),
            ("$now", now));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($id, $campaignId, 'active', $now, $now);
            """,
            ("$id", sessionId.ToString()),
            ("$campaignId", campaignId.ToString()),
            ("$now", now));

        using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES ($id, 2, $campaignId, $now);
            """,
            ("$id", sessionId.ToString()),
            ("$campaignId", campaignId.ToString()),
            ("$now", now));

        return sessionId;

    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory(
        IWeaveService weaveService,
        bool campaignScopedMemory = false) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with
                {
                    Embeddings = true,
                    Saga = true,
                    CampaignScopedMemory = campaignScopedMemory,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                        Dimensions = TestDimensions,
                    },
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

        public Task<Result<DivinationResult[]>> SearchCampaignScopedAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            DivinationCampaignScope scope,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken) =>
            SearchAsync(tableName, primaryKeyColumn, embeddingColumn, queryEmbedding, maxResults, similarityThreshold, cancellationToken);

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
