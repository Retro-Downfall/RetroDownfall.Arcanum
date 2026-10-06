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
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

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

        FixtureOrdinaryConnectionFactory connections = new();

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(weave, connections);

        HttpClient client = enabled.CreateAuthenticatedClient();

        (Guid sessionId, Guid entryId, string? title) = await SeedEmbeddedEntryAsync(
            enabled,
            content: "The root cause was a stale cache entry in the resolver.",
            vector: [1f, 0f, 0f],
            campaignId: null,
            status: "active");

        using ScopedConsumerPause pause = new("SessionDivinationEndpoints.JoinSessionMetadataAsync");

        Task<HttpResponseMessage> searching = PostDivineAsync(
            client,
            new SemanticSearchRequest("cache invalidation bug"));

        try
        {
            await pause.WaitUntilEnteredAsync();

            Assert.Equal(GrimoireScopedConsumerFinalUseKind.ReaderMaterialized, pause.FinalUse.Kind);

            Assert.Equal(1, pause.FinalUse.Observation);

            Assert.Equal(1, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));
        }
        finally
        {
            pause.Release();

            _ = await searching.WaitAsync(TimeSpan.FromSeconds(10));
        }

        HttpResponseMessage response = await searching;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SemanticSearchResult result = await ReadResultAsync(response);

        SemanticSessionSearchResult hit = Assert.Single(result.Results);

        Assert.Equal(sessionId, hit.SessionId);

        Assert.Equal(title, hit.SessionTitle);

        Assert.Equal(entryId, hit.EntryId);

        Assert.Equal("user", hit.EntryRole);

        Assert.Contains("stale cache", hit.EntryContentPreview, StringComparison.Ordinal);

        Assert.True(hit.Similarity > 0.99f);

        Assert.Equal(CovenantSqliteConnectionMode.ReadOnly, connections.Modes[^1]);

        Assert.Equal(0, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));
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
    public async Task Divine_FilteredByCampaign_returns_matches_ranked_below_the_global_cutoff()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        Guid targetCampaignId = await SeedCampaignAsync(enabled);

        Guid otherCampaignId = await SeedCampaignAsync(enabled);

        const int limit = 3;

        // limit + 1 entries of another Campaign rank closer than the one entry of the target, so the
        // global nearest-`limit` cut never reaches it: filtering after that cut returned nothing.
        for (int i = 0; i < limit + 1; i++)
        {
            await SeedEmbeddedEntryAsync(
                enabled,
                content: $"closer entry of another campaign {i}",
                vector: [1f, 0f, 0f],
                campaignId: otherCampaignId,
                status: "active");
        }

        (Guid targetSessionId, _, _) = await SeedEmbeddedEntryAsync(
            enabled,
            content: "farther entry of the target campaign",
            vector: [0.9f, 0.1f, 0f],
            campaignId: targetCampaignId,
            status: "active");

        HttpResponseMessage response = await PostDivineAsync(
            client,
            new SemanticSearchRequest("entry", CampaignId: targetCampaignId, Limit: limit));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SemanticSearchResult result = await ReadResultAsync(response);

        SemanticSessionSearchResult hit = Assert.Single(result.Results);

        Assert.Equal(targetSessionId, hit.SessionId);

        Assert.False(result.HasMore);
    }

    [SkippableFact]
    public async Task Divine_over_fetch_keeps_the_similarity_threshold()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        Guid targetCampaignId = await SeedCampaignAsync(enabled);

        Guid otherCampaignId = await SeedCampaignAsync(enabled);

        for (int i = 0; i < 4; i++)
        {
            await SeedEmbeddedEntryAsync(
                enabled,
                content: $"closer entry of another campaign {i}",
                vector: [1f, 0f, 0f],
                campaignId: otherCampaignId,
                status: "active");
        }

        (Guid aboveThresholdSessionId, _, _) = await SeedEmbeddedEntryAsync(
            enabled,
            content: "above the threshold",
            vector: [0.9f, 0.1f, 0f],
            campaignId: targetCampaignId,
            status: "active");

        // Orthogonal to the query: similarity 0, far under the 0.70 default threshold. Widening the
        // candidate window must not let it back in.
        await SeedEmbeddedEntryAsync(
            enabled,
            content: "below the threshold",
            vector: [0f, 1f, 0f],
            campaignId: targetCampaignId,
            status: "active");

        HttpResponseMessage response = await PostDivineAsync(
            client,
            new SemanticSearchRequest("entry", CampaignId: targetCampaignId, Limit: 3));

        SemanticSearchResult result = await ReadResultAsync(response);

        Assert.Equal(aboveThresholdSessionId, Assert.Single(result.Results).SessionId);
    }

    [SkippableFact]
    public async Task Divine_HasMore_is_true_only_when_more_matching_hits_exist_than_the_limit()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        Guid campaignId = await SeedCampaignAsync(enabled);

        for (int i = 0; i < 3; i++)
        {
            await SeedEmbeddedEntryAsync(
                enabled,
                content: $"matching entry {i}",
                vector: [1f, 0f, 0f],
                campaignId: campaignId,
                status: "active");
        }

        SemanticSearchResult truncated = await ReadResultAsync(
            await PostDivineAsync(client, new SemanticSearchRequest("entry", CampaignId: campaignId, Limit: 2)));

        Assert.Equal(2, truncated.Results.Length);

        Assert.True(truncated.HasMore);

        SemanticSearchResult complete = await ReadResultAsync(
            await PostDivineAsync(client, new SemanticSearchRequest("entry", CampaignId: campaignId, Limit: 3)));

        Assert.Equal(3, complete.Results.Length);

        Assert.False(complete.HasMore);
    }

    [SkippableFact]
    public async Task Divine_widens_past_the_first_window_when_closer_entries_of_another_campaign_fill_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        Guid targetCampaignId = await SeedCampaignAsync(enabled);

        Guid otherCampaignId = await SeedCampaignAsync(enabled);

        // limit 1 looks at 4 candidates first; five closer entries of another Campaign fill that whole
        // window, so only a wider second search can reach the one entry of the target.
        for (int i = 0; i < 5; i++)
        {
            await SeedEmbeddedEntryAsync(
                enabled,
                content: $"closer entry of another campaign {i}",
                vector: [1f, 0f, 0f],
                campaignId: otherCampaignId,
                status: "active");
        }

        (Guid targetSessionId, _, _) = await SeedEmbeddedEntryAsync(
            enabled,
            content: "farther entry of the target campaign",
            vector: [0.9f, 0.1f, 0f],
            campaignId: targetCampaignId,
            status: "active");

        SemanticSearchResult result = await ReadResultAsync(
            await PostDivineAsync(client, new SemanticSearchRequest("entry", CampaignId: targetCampaignId, Limit: 1)));

        Assert.Equal(targetSessionId, Assert.Single(result.Results).SessionId);

        Assert.False(result.HasMore);
    }

    [SkippableFact]
    public async Task Divine_retries_a_missed_first_window_once_at_the_cap_and_reports_more_when_the_cap_filled()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Every window comes back full of candidates no Session owns, so no search is ever exhausted and
        // nothing survives the filter.
        RecordingDivinationService divination = new(_ => []);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService(), divination: divination);

        HttpClient client = enabled.CreateAuthenticatedClient();

        SemanticSearchResult result = await ReadResultAsync(
            await PostDivineAsync(client, new SemanticSearchRequest("entry", CampaignId: Guid.NewGuid(), Limit: 1)));

        // One search at 4 x limit and one at the 500 cap. Doubling in between repeated the whole search and
        // join for a few more candidates each time, eight searches for limit 1.
        Assert.Equal([4, 500], divination.Windows);

        Assert.Empty(result.Results);

        // The cap filled without exhausting the ranking, so another matching hit could exist.
        Assert.True(result.HasMore);
    }

    [SkippableFact]
    public async Task Divine_reports_a_hit_both_windows_return_once()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid? targetEntryId = null;

        // The target ranks first in both windows; the rest of every window is candidates no Session owns.
        RecordingDivinationService divination = new(
            _ => targetEntryId is { } id ? [id.ToString().ToUpperInvariant()] : []);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService(), divination: divination);

        HttpClient client = enabled.CreateAuthenticatedClient();

        Guid campaignId = await SeedCampaignAsync(enabled);

        (Guid sessionId, Guid entryId, _) = await SeedEmbeddedEntryAsync(
            enabled,
            content: "the only entry of the campaign",
            vector: [1f, 0f, 0f],
            campaignId: campaignId,
            status: "active");

        targetEntryId = entryId;

        SemanticSearchResult result = await ReadResultAsync(
            await PostDivineAsync(client, new SemanticSearchRequest("entry", CampaignId: campaignId, Limit: 2)));

        Assert.Equal([8, 500], divination.Windows);

        SemanticSessionSearchResult hit = Assert.Single(result.Results);

        Assert.Equal(sessionId, hit.SessionId);

        Assert.Equal(entryId, hit.EntryId);
    }

    [SkippableFact]
    public async Task Divine_preview_is_the_first_200_characters_and_never_splits_a_surrogate_pair()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabled = CreateEnabledFactory(new FakeWeaveService());

        HttpClient client = enabled.CreateAuthenticatedClient();

        Guid longCampaignId = await SeedCampaignAsync(enabled);

        Guid pairCampaignId = await SeedCampaignAsync(enabled);

        _ = await SeedEmbeddedEntryAsync(
            enabled,
            content: new string('x', 300),
            vector: [1f, 0f, 0f],
            campaignId: longCampaignId,
            status: "active");

        // The 200th UTF-16 unit is the high half of a pair: the preview stops before it rather than ending
        // on half a character.
        _ = await SeedEmbeddedEntryAsync(
            enabled,
            content: new string('a', 199) + "\U0001F600" + new string('b', 50),
            vector: [1f, 0f, 0f],
            campaignId: pairCampaignId,
            status: "active");

        SemanticSearchResult longResult = await ReadResultAsync(
            await PostDivineAsync(client, new SemanticSearchRequest("entry", CampaignId: longCampaignId)));

        Assert.Equal(new string('x', 200), Assert.Single(longResult.Results).EntryContentPreview);

        SemanticSearchResult pairResult = await ReadResultAsync(
            await PostDivineAsync(client, new SemanticSearchRequest("entry", CampaignId: pairCampaignId)));

        Assert.Equal(new string('a', 199), Assert.Single(pairResult.Results).EntryContentPreview);
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

    private static ArcanumWebApplicationFactory CreateEnabledFactory(
        IWeaveService weaveService,
        FixtureOrdinaryConnectionFactory? connections = null,
        IDivinationService? divination = null) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with
                {
                    Embeddings = true,
                    SessionSearch = true,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                    },
                },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<IWeaveService>();

                services.AddSingleton(weaveService);

                if (divination is not null)
                {
                    services.RemoveAll<IDivinationService>();

                    services.AddSingleton(divination);
                }

                if (connections is not null)
                {
                    // Appended rather than substituted: the last registration is what
                    // GetRequiredService returns, so the production descriptor stays composed.
                    services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(connections);
                }
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

    /// <summary>
    /// Records every window the endpoint asks for and answers each one full: the ids <c>leading</c>
    /// returns rank first, and the rest of the window is filled with ids no Entry has.
    /// </summary>
    private sealed class RecordingDivinationService(Func<int, string[]> leading) : IDivinationService
    {
        private readonly List<int> _windows = [];

        public IReadOnlyList<int> Windows
        {
            get
            {
                lock (_windows)
                {
                    return [.. _windows];
                }
            }
        }

        public Task<Result<DivinationResult[]>> SearchAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken)
        {
            lock (_windows)
            {
                _windows.Add(maxResults);
            }

            string[] first = leading(maxResults);

            DivinationResult[] hits = new DivinationResult[maxResults];

            for (int i = 0; i < maxResults; i++)
            {
                string id = i < first.Length ? first[i] : Guid.NewGuid().ToString().ToUpperInvariant();

                hits[i] = new DivinationResult(id, 0.99f - (i * 0.0001f), new Dictionary<string, string>());
            }

            return Task.FromResult(Result<DivinationResult[]>.Success(hits));
        }

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
            throw new NotSupportedException("Not used by the session divination endpoint.");

        public Task<Result<DivinationResult[]>> SearchCampaignScopedAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            DivinationCampaignScope scope,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the session divination endpoint.");
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
