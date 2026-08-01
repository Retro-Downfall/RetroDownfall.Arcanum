using System.Diagnostics.CodeAnalysis;

using System.Net;

using System.Net.Http.Json;

using System.Runtime.CompilerServices;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]

public sealed class WebWorkflowEndpointTests
{

    [SkippableFact]

    public async Task Search_propagates_bounded_filters_to_the_server_provider()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            new StubIntelligence());

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/web/search",
            new WebSearchWorkflowRequest
            {

                Query = "current facts",

                ResultCount = 3,

                Freshness = "week",

                IncludeDomains = ["example.test"],

                ExcludeDomains = ["ads.example.test"],

            },
            ArcanumJsonContext.Default.WebSearchWorkflowRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(provider.LastSearchOptions);

        Assert.Equal(3, provider.LastSearchOptions.ResultCount);

        Assert.Equal(3, provider.LastSearchOptions.MaxCitations);

        Assert.Equal("week", provider.LastSearchOptions.Freshness);

        Assert.Equal(["example.test"], provider.LastSearchOptions.IncludeDomains);

        Assert.Equal(["ads.example.test"], provider.LastSearchOptions.ExcludeDomains);

    }

    [SkippableFact]

    public async Task Browse_javascript_returns_actionable_degraded_behavior_without_fetching()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            new StubIntelligence());

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/web/browse",
            new WebBrowseWorkflowRequest
            {

                Url = "https://example.test/app",

                RenderMode = "javascript",

            },
            ArcanumJsonContext.Default.WebBrowseWorkflowRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains(
            ErrorCodes.WebResearch.JavaScriptRenderingUnavailable,
            body,
            StringComparison.Ordinal);

        Assert.Contains("--render static", body, StringComparison.Ordinal);

        Assert.Equal(0, provider.ReadCalls);

    }

    [SkippableFact]

    public async Task Search_can_attach_final_markdown_to_an_existing_session()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            new StubIntelligence());

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage createdResponse = await client.PostAsJsonAsync(
            "/api/sessions",
            new CreateSessionRequest(null, "Research target"),
            ArcanumJsonContext.Default.CreateSessionRequest);

        ApiResponse<SessionDetailDto>? created = JsonSerializer.Deserialize(
            await createdResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(created?.Data);

        HttpResponseMessage searchResponse = await client.PostAsJsonAsync(
            "/api/web/search",
            new WebSearchWorkflowRequest
            {

                Query = "attach this",

                AttachToSessionId = created.Data.Id,

            },
            ArcanumJsonContext.Default.WebSearchWorkflowRequest);

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);

        ApiResponse<WebSearchWorkflowResult>? search = JsonSerializer.Deserialize(
            await searchResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseWebSearchWorkflowResult);

        Assert.NotNull(search?.Data?.AttachmentId);

        HttpResponseMessage attachmentsResponse = await client.GetAsync(
            $"/api/sessions/{created.Data.Id:D}/attachments");

        ApiResponse<SessionAttachmentDto[]>? attached = JsonSerializer.Deserialize(
            await attachmentsResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        SessionAttachmentDto attachment = Assert.Single(attached!.Data!);

        Assert.Equal(search.Data.AttachmentId, attachment.Id);

        Assert.Equal("web-search.md", attachment.OriginalFileName);

        Assert.Equal("text/markdown", attachment.MimeType);

    }

    [SkippableFact]

    public async Task Research_is_server_owned_bounded_and_session_continuable()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        StubIntelligence intelligence = new();

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            intelligence);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = Guid.Parse(
            "11111111-1111-1111-1111-111111111111");

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "What changed?",

                    MaxSources = 2,

                    MaxHops = 2,

                    TokenBudget = 1_200,

                    ContinueSessionId = sessionId,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string ndjson = await response.Content.ReadAsStringAsync();

        Assert.Contains("Searching hop 1 of 2", ndjson, StringComparison.Ordinal);

        Assert.Contains("Searching hop 2 of 2", ndjson, StringComparison.Ordinal);

        Assert.Contains("Fetching source", ndjson, StringComparison.Ordinal);

        Assert.Contains("Rendering source", ndjson, StringComparison.Ordinal);

        Assert.Contains("Synthesizing", ndjson, StringComparison.Ordinal);

        Assert.Contains("https://example.test/source", ndjson, StringComparison.Ordinal);

        Assert.Equal(2, provider.SearchCalls);

        Assert.Equal(1, provider.ReadCalls);

        Assert.NotNull(intelligence.Request);

        Assert.True(intelligence.Request.DisableAllTools);

        Assert.Equal(1_200, intelligence.Request.MaxOutputTokens);

        Assert.Equal(sessionId, intelligence.Request.SessionId);

        Assert.Contains(
            "[1]",
            intelligence.Request.Prompt,
            StringComparison.Ordinal);

    }

    private static ArcanumWebApplicationFactory Factory(
        StubWebProvider provider,
        StubIntelligence intelligence) =>
        new()
        {

            SettingsOverride = settings => settings with
            {

                Features = settings.Features with
                {

                    WebBrowsing = true,

                },

            },

            ServiceOverrides = services =>
            {

                services.RemoveAll<IWebResearchProviderCatalog>();

                services.AddSingleton<IWebResearchProviderCatalog>(
                    new StubCatalog(provider));

                services.RemoveAll<IArcanumIntelligenceProvider>();

                services.AddScoped<IArcanumIntelligenceProvider>(
                    _ => intelligence);

            },

        };

    private sealed class StubCatalog(
        IWebResearchProvider provider) : IWebResearchProviderCatalog
    {

        public bool TryGetProvider(
            string providerName,
            [NotNullWhen(true)] out IWebResearchProvider? resolved)
        {

            resolved = provider;

            return true;

        }

    }

    private sealed class StubWebProvider : IWebResearchProvider
    {

        public int SearchCalls { get; private set; }

        public int ReadCalls { get; private set; }

        public WebSearchOptions? LastSearchOptions { get; private set; }

        public string ProviderName => WebResearchProviderNames.Perplexity;

        public WebResearchCapabilities Capabilities =>
            WebResearchCapabilities.Search
            | WebResearchCapabilities.ReadUrl;

        public Task<Result<WebSearchResult>> SearchAsync(
            string query,
            WebSearchOptions options,
            CancellationToken cancellationToken = default)
        {

            SearchCalls++;

            LastSearchOptions = options;

            return Task.FromResult(
                Result<WebSearchResult>.Success(
                    new WebSearchResult(
                        "Search summary [1].",
                        [
                            new WebCitation(
                                1,
                                "https://example.test/source",
                                "Source"),
                        ],
                        new WebResearchUsage(
                            TotalTokens: 10,
                            SearchQueries: 1,
                            CostUsd: 0.01m))));

        }

        public Task<Result<WebReadResult>> ReadUrlAsync(
            string url,
            WebReadOptions options,
            CancellationToken cancellationToken = default)
        {

            ReadCalls++;

            return Task.FromResult(
                Result<WebReadResult>.Success(
                    new WebReadResult(
                        "Source",
                        "Rendered evidence.",
                        url,
                        [])));

        }

    }

    private sealed class StubIntelligence : IArcanumIntelligenceProvider
    {

        public PingRequest? Request { get; private set; }

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null)
        {

            Request = request;

            return Task.FromResult(
                Result<PromptTurnResult>.Success(
                    new PromptTurnResult(
                        "Synthesized answer [1].",
                        new ChatCompletionUsage(20, 10, 30))));

        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null)
        {

            await Task.CompletedTask;

            yield break;

        }

    }

}
