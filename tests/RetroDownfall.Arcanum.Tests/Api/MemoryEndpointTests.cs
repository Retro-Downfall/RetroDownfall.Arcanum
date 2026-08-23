using System.Net;

using System.Net.Http.Json;

using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Tower;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]

public sealed class MemoryEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public MemoryEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]

    public async Task Status_reports_every_distinct_store_and_retention_without_requiring_features()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/memory/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemoryStatusDto>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto);

        Assert.NotNull(envelope?.Data);

        string[] names = envelope.Data.Stores.Select(static store => store.Name).ToArray();

        Assert.Contains("Session Entries", names);

        Assert.Contains("Pinned Entries", names);

        Assert.Contains("Campaign Summary", names);

        Assert.Contains("Attachments", names);

        Assert.Contains("Indexed Attachment Chunks", names);

        Assert.Contains("Lexicon", names);

        Assert.Contains("Saga", names);

        Assert.Contains("Workspace Index", names);

        Assert.All(envelope.Data.Stores, static store => Assert.False(string.IsNullOrWhiteSpace(store.Retention)));

    }

    [SkippableFact]

    public async Task Search_requires_query_but_not_an_embedding_feature_gate()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage invalid = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("   "),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        HttpResponseMessage invalidScope = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("query", (MemorySearchScope)99),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, invalidScope.StatusCode);

        HttpResponseMessage valid = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("not-present", MemorySearchScope.All),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(
            valid,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(envelope?.Data);

        Assert.Equal(MemorySearchScope.All, envelope.Data.Scope);

    }

    [SkippableFact]

    public async Task Lexicon_endpoints_list_show_search_and_delete_only_the_named_entity()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        FakeLexiconService lexicon = new();

        _ = await lexicon.UpsertAsync(
            "Operator",
            "Person",
            ["Prefers dark mode."],
            CancellationToken.None);

        _ = await lexicon.UpsertAsync(
            "Arcanum",
            "Project",
            ["Uses C#."],
            CancellationToken.None);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ILexiconService>();

                services.AddSingleton<ILexiconService>(lexicon);

            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage list = await client.GetAsync("/api/memory/lexicon");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        ApiResponse<LexiconListDto>? listed = await ReadAsync(
            list,
            ArcanumJsonContext.Default.ApiResponseLexiconListDto);

        Assert.Equal(2, listed?.Data?.Entries.Length);

        HttpResponseMessage search = await client.GetAsync("/api/memory/lexicon?q=dark");

        ApiResponse<LexiconListDto>? searched = await ReadAsync(
            search,
            ArcanumJsonContext.Default.ApiResponseLexiconListDto);

        Assert.Single(searched!.Data!.Entries);

        HttpResponseMessage unifiedSearch = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("dark", MemorySearchScope.All),
            ArcanumJsonContext.Default.MemorySearchRequest);

        ApiResponse<MemorySearchResponse>? unified = await ReadAsync(
            unifiedSearch,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        MemorySearchResultDto match = Assert.Single(unified!.Data!.Results);

        Assert.Equal(MemorySearchScope.Lexicon, match.Scope);

        Assert.Contains("Lexicon entity: Operator", match.Provenance, StringComparison.Ordinal);

        Assert.Contains("explicit", match.Retention, StringComparison.OrdinalIgnoreCase);

        HttpResponseMessage show = await client.GetAsync("/api/memory/lexicon/Operator");

        Assert.Equal(HttpStatusCode.OK, show.StatusCode);

        HttpResponseMessage deleted = await client.DeleteAsync("/api/memory/lexicon/Operator");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Result<LexiconEntryDto?> remainingOperator = await lexicon.GetByNameAsync("Operator");

        Result<LexiconEntryDto?> remainingArcanum = await lexicon.GetByNameAsync("Arcanum");

        Assert.Null(remainingOperator.Value);

        Assert.NotNull(remainingArcanum.Value);

    }

    [SkippableFact]

    public async Task Search_bounds_the_saga_page_it_requests_and_caps_the_response()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        // A store that answers with whatever it was asked for, so an unbounded request produces an
        // unbounded response — exactly what a broad query against a mature corpus does.
        SaturatingSagaMemoryStore saga = new(available: MemoryEndpoints.SearchResultLimit + 2_000);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ISagaMemoryStore>();

                services.AddSingleton<ISagaMemoryStore>(saga);

            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("e", MemorySearchScope.Saga),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(envelope?.Data);

        // Budget + 1: the probe row is what separates "this scope filled its slice exactly" from
        // "this scope had more", and it is trimmed before the response is built. The bound that
        // matters — what the caller is made to hold in memory — is still exactly the budget.
        Assert.Equal(MemoryEndpoints.SearchResultLimit + 1, saga.RequestedLimit);

        Assert.Equal(MemoryEndpoints.SearchResultLimit, envelope.Data.Results.Length);

        Assert.True(envelope.Data.HasMore);

    }

    [SkippableFact]

    public async Task Search_shares_one_budget_across_scopes_rather_than_one_per_scope()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        SaturatingSagaMemoryStore saga = new(available: MemoryEndpoints.SearchResultLimit + 2_000);

        FakeLexiconService lexicon = new();

        _ = await lexicon.UpsertAsync(
            "Operator",
            "Person",
            ["Prefers dark mode."],
            CancellationToken.None);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ISagaMemoryStore>();

                services.AddSingleton<ISagaMemoryStore>(saga);

                services.RemoveAll<ILexiconService>();

                services.AddSingleton<ILexiconService>(lexicon);

            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("dark", MemorySearchScope.All),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(envelope?.Data);

        Assert.True(
            envelope.Data.Results.Length <= MemoryEndpoints.SearchResultLimit,
            $"scope=all returned {envelope.Data.Results.Length} results, above the {MemoryEndpoints.SearchResultLimit} budget.");

    }

    /// <summary>
    /// The server-side budget alone truncates without any machine-readable signal, and gives a caller
    /// that only wants ten rows no way to say so. <c>limit</c> is the ask, <c>hasMore</c> is the answer.
    /// </summary>
    [SkippableFact]

    public async Task Search_honours_a_caller_supplied_limit_and_reports_which_scopes_had_more()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        FakeLexiconService lexicon = new();

        for (int index = 0; index < 5; index++)
        {

            _ = await lexicon.UpsertAsync(
                $"Moonlit-{index}",
                "Person",
                ["Works by moonlight."],
                CancellationToken.None);

        }

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ILexiconService>();

                services.AddSingleton<ILexiconService>(lexicon);

            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage capped = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("moonlight", MemorySearchScope.Lexicon, Limit: 2),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, capped.StatusCode);

        ApiResponse<MemorySearchResponse>? truncated = await ReadAsync(
            capped,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(truncated?.Data);

        Assert.Equal(2, truncated.Data.Results.Length);

        Assert.True(truncated.Data.HasMore);

        MemorySearchScopeStatusDto truncatedScope = Assert.Single(truncated.Data.Scopes!);

        Assert.Equal(MemorySearchScope.Lexicon, truncatedScope.Scope);

        Assert.Equal(2, truncatedScope.Count);

        Assert.True(truncatedScope.HasMore);

        HttpResponseMessage roomy = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("moonlight", MemorySearchScope.Lexicon, Limit: 10),
            ArcanumJsonContext.Default.MemorySearchRequest);

        ApiResponse<MemorySearchResponse>? complete = await ReadAsync(
            roomy,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(complete?.Data);

        Assert.Equal(5, complete.Data.Results.Length);

        Assert.False(complete.Data.HasMore);

        Assert.False(Assert.Single(complete.Data.Scopes!).HasMore);

    }

    /// <summary>
    /// Refused rather than clamped: a caller that asked for 50,000 and silently received the budget
    /// would read a full <c>hasMore: false</c> page as "that is everything".
    /// </summary>
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MemoryEndpoints.SearchResultLimit + 1)]

    public async Task Search_refuses_a_limit_outside_the_server_budget(int limit)
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("anything", MemorySearchScope.All, Limit: limit),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, envelope?.Error?.Code);

    }

    private static async Task<T?> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {

        byte[] json = await response.Content.ReadAsByteArrayAsync();

        return JsonSerializer.Deserialize(json, typeInfo);

    }

    /// <summary>
    /// Answers <c>ListAsync</c> with whatever page size it is asked for, up to a fixed corpus size,
    /// and records that size. Stands in for a mature Saga corpus so the endpoint's own bound is what
    /// limits the response rather than the amount of test data.
    /// </summary>
    private sealed class SaturatingSagaMemoryStore(int available) : ISagaMemoryStore
    {

        public int RequestedLimit { get; private set; }

        public Task<SagaMemoryDto[]> ListAsync(
            string? query,
            Guid? sessionId,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {

            RequestedLimit = limit;

            int count = Math.Min(available, limit);

            SagaMemoryDto[] memories = new SagaMemoryDto[count];

            for (int i = 0; i < count; i++)
            {

                memories[i] = new SagaMemoryDto(
                    $"saga-{i}",
                    "e",
                    DateTimeOffset.UnixEpoch,
                    null,
                    null,
                    null);

            }

            return Task.FromResult(memories);

        }

        public Task InsertAsync(
            string id,
            string content,
            DateTimeOffset createdAt,
            Guid? sessionId,
            string? tags,
            string? source,
            float[] embedding,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(available);

        public Task<int> CountBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, SagaMemoryDto>> GetByIdsAsync(
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAllAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaStats> GetStatsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetWatermarkAsync(
            Guid sessionId,
            DateTimeOffset lastExtractedEntryCreatedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

}
