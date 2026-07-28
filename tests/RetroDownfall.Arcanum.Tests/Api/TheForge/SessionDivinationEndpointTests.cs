using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

/// <summary>
/// RAG Phase 2 — <c>POST /api/sessions/divine</c> integration tests.
/// </summary>
[Collection("ApiHost")]
public sealed class SessionDivinationEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SessionDivinationEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task Divine_WhenDisabled_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostDivineAsync(client, new SemanticSearchRequest("hello"));

        // FeatureDisabled means an operator turned this off in config, not that the caller lacks
        // permission, so it maps to 503 (retry later) rather than 403.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await AssertErrorCodeAsync(response, "Embeddings.FeatureDisabled");

    }

    [SkippableFact]
    public async Task Divine_EmptyQuery_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostDivineAsync(client, new SemanticSearchRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await AssertErrorCodeAsync(response, "Validation.InvalidBody");

    }

    [SkippableFact]
    public async Task Divine_InvalidStatus_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostDivineAsync(
            client,
            new SemanticSearchRequest("hello", Status: "not-a-real-status"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await AssertErrorCodeAsync(response, "Validation.InvalidBody");

    }

    [SkippableFact]
    public async Task Divine_ProviderUnavailable_Returns503()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService { Available = false });

        HttpClient client = enabled.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostDivineAsync(client, new SemanticSearchRequest("hello"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await AssertErrorCodeAsync(response, "Embeddings.ProviderUnavailable");

    }

    [SkippableFact]
    public async Task Divine_HappyPath_ReturnsJoinedSessionAndEntryMetadata()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWeaveService weave = new();

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(weave);

        HttpClient client = enabled.CreateAuthenticatedClient();

        (Guid sessionId, Guid entryId, string? title) = await SeedEmbeddedEntryAsync(
            enabled,
            content: "The root cause was a stale cache entry in the resolver.",
            vector: [1f, 0f, 0f],
            campaignId: null,
            status: "active");

        HttpResponseMessage response = await PostDivineAsync(client, new SemanticSearchRequest("cache invalidation bug"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SemanticSearchResult result = await ReadResultAsync(response);

        SemanticSessionSearchResult hit = Assert.Single(result.Results);

        Assert.Equal(sessionId, hit.SessionId);

        Assert.Equal(title, hit.SessionTitle);

        Assert.Equal(entryId, hit.EntryId);

        Assert.Equal("user", hit.EntryRole);

        Assert.Contains("stale cache", hit.EntryContentPreview, StringComparison.Ordinal);

        Assert.True(hit.Similarity > 0.99f);

    }

    [SkippableFact]
    public async Task Divine_FiltersByCampaignAndStatus()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWeaveService weave = new();

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(weave);

        HttpClient client = enabled.CreateAuthenticatedClient();

        Guid targetCampaignId = await SeedCampaignAsync(enabled);

        Guid otherCampaignId = await SeedCampaignAsync(enabled);

        (Guid matchingSessionId, _, _) = await SeedEmbeddedEntryAsync(
            enabled,
            content: "matching campaign entry",
            vector: [1f, 0f, 0f],
            campaignId: targetCampaignId,
            status: "active");

        (Guid otherCampaignSessionId, _, _) = await SeedEmbeddedEntryAsync(
            enabled,
            content: "other campaign entry",
            vector: [1f, 0f, 0f],
            campaignId: otherCampaignId,
            status: "active");

        (Guid archivedSessionId, _, _) = await SeedEmbeddedEntryAsync(
            enabled,
            content: "archived same-campaign entry",
            vector: [1f, 0f, 0f],
            campaignId: targetCampaignId,
            status: "archived");

        HttpResponseMessage response = await PostDivineAsync(
            client,
            new SemanticSearchRequest("entry", CampaignId: targetCampaignId, Status: "active"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SemanticSearchResult result = await ReadResultAsync(response);

        SemanticSessionSearchResult hit = Assert.Single(result.Results);

        Assert.Equal(matchingSessionId, hit.SessionId);

        Assert.NotEqual(otherCampaignSessionId, hit.SessionId);

        Assert.NotEqual(archivedSessionId, hit.SessionId);

    }

    [SkippableFact]
    public async Task Divine_LimitIsClampedToConfiguredMaximum()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWeaveService weave = new();

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(weave);

        HttpClient client = enabled.CreateAuthenticatedClient();

        for (int i = 0; i < 3; i++)
        {

            await SeedEmbeddedEntryAsync(
                enabled,
                content: $"clamp test entry {i}",
                vector: [1f, 0f, 0f],
                campaignId: null,
                status: "active");

        }

        // 0 is below the 1-50 clamp range and is coerced up to 1.
        HttpResponseMessage response = await PostDivineAsync(client, new SemanticSearchRequest("clamp test", Limit: 0));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SemanticSearchResult result = await ReadResultAsync(response);

        Assert.Single(result.Results);

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory(IWeaveService weaveService) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Embeddings = settings.Embeddings with
                {
                    Enabled = true,
                    SessionSearchEnabled = true,
                    Provider = "test",
                    Model = "test-embed",
                    SimilarityThreshold = 0f,
                },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<IWeaveService>();

                services.AddSingleton(weaveService);

            },
        };

    private static async Task<HttpResponseMessage> PostDivineAsync(HttpClient client, SemanticSearchRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.SemanticSearchRequest);

        return await client.PostAsync("/api/sessions/divine", new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    private static async Task<SemanticSearchResult> ReadResultAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SemanticSearchResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSemanticSearchResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        return body.Data!;

    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expectedCode)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SemanticSearchResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSemanticSearchResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(expectedCode, body.Error?.Code);

    }

    private static async Task<Guid> SeedCampaignAsync(ArcanumWebApplicationFactory factory)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        ICampaignRepository repository = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

        string workspaceRoot = Path.Combine(factory.TempHome, "session-divination-campaigns", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workspaceRoot);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Campaign campaign = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Campaign-{Guid.NewGuid():N}",
            Path = workspaceRoot,
            Type = WorkspaceType.Campaign,
            Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Campaign saved = (await repository
            .AddAsync(campaign, CancellationToken.None)).Value;

        return saved.Id;

    }

    /// <summary>
    /// Creates a session + entry via the real repositories, then writes a matching
    /// <c>entry_embeddings</c> row directly (mirroring what <c>EntryWeavingService</c> would do once it
    /// ticks) so Divination has something deterministic to find.
    /// </summary>
    private static async Task<(Guid SessionId, Guid EntryId, string? Title)> SeedEmbeddedEntryAsync(
        ArcanumWebApplicationFactory factory,
        string content,
        float[] vector,
        Guid? campaignId,
        string status)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        ISessionRepository sessionRepository = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

        string title = $"Session-{Guid.NewGuid():N}";

        Session session = await sessionRepository.CreateAsync(campaignId, title, CancellationToken.None);

        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = content,
        };

        // Add the entry while the session is still active — SessionRepository.AddEntryAsync rejects
        // appends to an already-archived session (Session.Archived), so any status change must happen
        // after seeding, not before.
        Result<Entry> added = await sessionRepository.AddEntryAsync(session.Id, entry, CancellationToken.None);

        Assert.True(added.IsSuccess);

        if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {

            session.Status = status;

            await sessionRepository.UpdateSessionAsync(session, CancellationToken.None);

        }

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await InsertEntryEmbeddingAsync(db, added.Value.Id, vector);

        return (session.Id, added.Value.Id, title);

    }

    private static async Task InsertEntryEmbeddingAsync(ArcanumDbContext db, Guid entryId, float[] vector)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            INSERT INTO "entry_embeddings" ("EntryId", "Embedding", "Dim")
            VALUES (@entryId, @embedding, @dim)
            """;

        DbParameter idParam = cmd.CreateParameter();

        idParam.ParameterName = "@entryId";

        // EF's SQLite provider stores Guid columns as uppercase "D"-format text; matching that here is
        // what lets the endpoint's `"Entries"."Id" IN (...)` join find this row.
        idParam.Value = entryId.ToString().ToUpperInvariant();

        cmd.Parameters.Add(idParam);

        DbParameter embeddingParam = cmd.CreateParameter();

        embeddingParam.ParameterName = "@embedding";

        embeddingParam.Value = System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(vector).ToArray();

        cmd.Parameters.Add(embeddingParam);

        DbParameter dimParam = cmd.CreateParameter();

        dimParam.ParameterName = "@dim";

        dimParam.Value = vector.Length;

        cmd.Parameters.Add(dimParam);

        _ = await cmd.ExecuteNonQueryAsync();

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public float[] QueryVector { get; set; } = [1f, 0f, 0f];

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(QueryVector)));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the session divination endpoint.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the session divination endpoint.");

    }

}
