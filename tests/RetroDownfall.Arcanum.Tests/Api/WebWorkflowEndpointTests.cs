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

using RetroDownfall.Arcanum.Core.Workspaces;

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

    /// <summary>
    /// System.Text.Json writes an explicit JSON <c>null</c> over a property initializer, so a
    /// non-nullable request property can still arrive null. Dereferencing it turns a routine client
    /// mistake into a 500 <c>Hub.Unhandled</c> instead of an envelope the caller can act on.
    /// </summary>
    [SkippableTheory]
    [InlineData("""{"query":"current facts","resultCount":3,"includeDomains":null}""")]
    [InlineData("""{"query":"current facts","resultCount":3,"excludeDomains":null}""")]
    public async Task Search_explicit_null_domain_filters_do_not_fault_the_host(string body)
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Factory(
            new StubWebProvider(),
            new StubIntelligence());

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/web/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

    }

    [SkippableFact]

    public async Task Browse_explicit_null_render_mode_does_not_fault_the_host()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Factory(
            new StubWebProvider(),
            new StubIntelligence());

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/web/browse",
            new StringContent(
                """{"url":"https://example.test/app","renderMode":null}""",
                Encoding.UTF8,
                "application/json"));

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

    }

    /// <summary>
    /// Attachment is an optional side effect. Discovering the target is unusable only after the
    /// (non-retried, billable) provider call throws away an answer the operator already paid for,
    /// which is why research preflights the same conditions before searching.
    /// </summary>
    [SkippableFact]

    public async Task Search_rejects_an_unknown_attachment_target_before_provider_work()
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

                AttachToSessionId = Guid.NewGuid(),

            },
            ArcanumJsonContext.Default.WebSearchWorkflowRequest);

        Assert.Contains(
            ErrorCodes.Session.NotFound,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(0, provider.SearchCalls);

    }

    [SkippableFact]

    public async Task Browse_rejects_an_unknown_attachment_target_before_provider_work()
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

                Url = "https://example.test/page",

                AttachToSessionId = Guid.NewGuid(),

            },
            ArcanumJsonContext.Default.WebBrowseWorkflowRequest);

        Assert.Contains(
            ErrorCodes.Session.NotFound,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(0, provider.ReadCalls);

    }

    /// <summary>
    /// Every other streaming writer in the host disables caching and proxy buffering. Without them
    /// an intermediary can hold the NDJSON frames back and the progress stream stops being a stream.
    /// </summary>
    [SkippableFact]

    public async Task Research_stream_disables_caching_and_proxy_buffering()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Factory(
            new StubWebProvider(),
            new StubIntelligence());

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "stream headers",

                    SourceTarget = 1,

                    TokenBudget = 512,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("no-cache", Assert.Single(response.Headers.CacheControl!.ToString().Split(", ")));

        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));

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

    public async Task Research_is_progress_driven_and_session_continuable()
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

        HttpResponseMessage createdSessionResponse = await client.PostAsJsonAsync(

            "/api/sessions",

            new CreateSessionRequest(null, "Research continuation"),

            ArcanumJsonContext.Default.CreateSessionRequest);

        ApiResponse<SessionDetailDto>? createdSession = JsonSerializer.Deserialize(

            await createdSessionResponse.Content.ReadAsStringAsync(),

            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Guid sessionId = Assert.IsType<Guid>(createdSession?.Data?.Id);

        Guid campaignId = Guid.Parse(

            "22222222-2222-2222-2222-222222222222");

        AttachedFileDto attachedFile = new(

            "research-notes.txt",

            "trusted operator context");

        ScryingFocusDto scryingFocus = new(

            Convert.ToBase64String([1, 2, 3]),

            "image/png");

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "What changed?",

                    SourceTarget = 2,

                    TokenBudget = 1_200,

                    ContinueSessionId = sessionId,

                    WorkingDirectory = "/workspace/project",

                    CampaignId = campaignId,

                    AttachedFiles = [attachedFile],

                    ScryingFoci = [scryingFocus],

                    Temperature = 0.2f,

                    TopP = 0.8f,

                    Stop = ["END"],

                    Seed = 42,

                    ResponseFormat = "text",

                    PresencePenalty = 0.1f,

                    FrequencyPenalty = -0.1f,

                    UnattendedMode = true,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string ndjson = await response.Content.ReadAsStringAsync();

        Assert.Contains("Searching research pass 1", ndjson, StringComparison.Ordinal);

        Assert.Contains("Searching research pass 2", ndjson, StringComparison.Ordinal);

        Assert.Contains("No new sources were discovered", ndjson, StringComparison.Ordinal);

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

        Assert.Equal("/workspace/project", intelligence.Request.WorkingDirectory);

        Assert.Equal(campaignId, intelligence.Request.CampaignId);

        Assert.Equal(attachedFile, Assert.Single(intelligence.Request.AttachedFiles!));

        Assert.Equal(scryingFocus, Assert.Single(intelligence.Request.ScryingFoci!));

        Assert.Equal(0.2f, intelligence.Request.Temperature);

        Assert.Equal(0.8f, intelligence.Request.TopP);

        Assert.Equal(["END"], intelligence.Request.Stop);

        Assert.Equal(42, intelligence.Request.Seed);

        Assert.Equal("text", intelligence.Request.ResponseFormat);

        Assert.Equal(0.1f, intelligence.Request.PresencePenalty);

        Assert.Equal(-0.1f, intelligence.Request.FrequencyPenalty);

        Assert.True(intelligence.Request.UnattendedMode);

        Assert.Contains(
            "[1]",
            intelligence.Request.Prompt,
            StringComparison.Ordinal);

    }

    [SkippableFact]

    public async Task Research_continues_beyond_former_hop_limit_until_sources_are_exhausted()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new()
        {

            ChangingCitationRounds = 8,

        };

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            new StubIntelligence());

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "Keep gathering changing evidence",

                    TokenBudget = 1_200,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string ndjson = await response.Content.ReadAsStringAsync();

        Assert.Equal(9, provider.SearchCalls);

        Assert.Equal(8, provider.ReadCalls);

        Assert.Contains(
            "No new sources were discovered",
            ndjson,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "maximum hops",
            ndjson,
            StringComparison.OrdinalIgnoreCase);

    }

    [SkippableFact]

    public async Task Research_rejects_invalid_synthesis_payload_before_provider_work()

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

        string oversized = new(
            'x',
            (int)ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes + 1);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "Reject before search",

                    SourceTarget = 2,

                    TokenBudget = 1_200,

                    AttachedFiles =
                    [

                        new AttachedFileDto(
                            "oversized.txt",
                            oversized),

                    ],

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string ndjson = await response.Content.ReadAsStringAsync();

        Assert.Contains(
            ErrorCodes.Validation.AttachedFiles,
            ndjson,
            StringComparison.Ordinal);

        Assert.Equal(0, provider.SearchCalls);

        Assert.Equal(0, provider.ReadCalls);

        Assert.Null(intelligence.Request);

    }

    /// <summary>
    /// Follow-up passes append a fixed suffix to the question, so a question just under the
    /// provider's 4,000-character query limit passes pass 1 — which is billed — and then fails pass
    /// 2 with the run aborted and nothing returned. The bound has to be enforced up front.
    /// </summary>
    [SkippableFact]

    public async Task Research_rejects_a_question_that_no_follow_up_pass_could_carry()
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

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = new string('q', 3_950),

                    SourceTarget = 2,

                    TokenBudget = 1_200,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(
            ErrorCodes.WebResearch.RequestRejected,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(0, provider.SearchCalls);

    }

    [SkippableFact]

    public async Task Research_rejects_unknown_synthesis_model_before_provider_work()
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

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "Reject before search",

                    SourceTarget = 2,

                    TokenBudget = 1_200,

                    Model = "provider/unknown-model",

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string ndjson = await response.Content.ReadAsStringAsync();

        Assert.Contains(ErrorCodes.Hub.Model, ndjson, StringComparison.Ordinal);

        Assert.Equal(0, provider.SearchCalls);

        Assert.Equal(0, provider.ReadCalls);

        Assert.Null(intelligence.Request);

    }

    [SkippableFact]

    public async Task Research_rejects_disabled_result_attachment_before_provider_work()

    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        StubIntelligence intelligence = new();

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            intelligence,
            attachmentsEnabled: false);

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage createdSessionResponse = await client.PostAsJsonAsync(

            "/api/sessions",

            new CreateSessionRequest(null, "Disabled attachment target"),

            ArcanumJsonContext.Default.CreateSessionRequest);

        ApiResponse<SessionDetailDto>? createdSession = JsonSerializer.Deserialize(

            await createdSessionResponse.Content.ReadAsStringAsync(),

            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Guid sessionId = Assert.IsType<Guid>(createdSession?.Data?.Id);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "Reject disabled result attachment before search",

                    SourceTarget = 2,

                    TokenBudget = 1_200,

                    AttachToSessionId = sessionId,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string ndjson = await response.Content.ReadAsStringAsync();

        Assert.Contains(
            ErrorCodes.WebResearch.RequestRejected,
            ndjson,
            StringComparison.Ordinal);

        Assert.Contains("disabled", ndjson, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, provider.SearchCalls);

        Assert.Equal(0, provider.ReadCalls);

        Assert.Null(intelligence.Request);

    }

    /// <summary>
    /// The NDJSON research stream must always end on a frame. Once the first frame is flushed the
    /// response has started, so <c>ArcanumExceptionHandler</c> can no longer produce an envelope and
    /// an escaping exception aborts the chunked body — the caller sees a stream that simply stops,
    /// after the billable search and synthesis passes have already been paid for.
    /// </summary>
    [SkippableFact]

    public async Task Research_orchestration_failure_ends_the_stream_with_a_sanitized_error_frame()

    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new() { ThrowOnSearchCall = 2 };

        StubIntelligence intelligence = new();

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            intelligence);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "What changed?",

                    SourceTarget = 2,

                    TokenBudget = 1_200,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string ndjson = await response.Content.ReadAsStringAsync();

        string[] lines = ndjson.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        WebResearchStreamFrame? terminal = JsonSerializer.Deserialize(
            lines[^1],
            ArcanumJsonContext.Default.WebResearchStreamFrame);

        Assert.Equal(WebResearchStreamFrameType.Error, terminal?.Type);

        // Sanitized: the operator gets a stable public message, never the exception's own text.
        Assert.DoesNotContain(
            "citation index exploded",
            ndjson,
            StringComparison.OrdinalIgnoreCase);

    }

    [SkippableFact]

    public async Task Research_resolves_campaign_only_context_before_search_and_synthesis()

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

        string campaignPath = Path.Combine(
            factory.TempHome,
            $"research-campaign-{Guid.NewGuid():N}");

        Directory.CreateDirectory(campaignPath);

        RegisterCampaignRequest registration = new(
            "Research Campaign",
            campaignPath,
            WorkspaceType.Campaign,
            null);

        HttpResponseMessage campaignResponse = await client.PostAsync(
            "/api/campaigns",
            new StringContent(
                JsonSerializer.Serialize(
                    registration,
                    ArcanumJsonContext.Default.RegisterCampaignRequest),
                Encoding.UTF8,
                "application/json"));

        ApiResponse<CampaignDto>? campaign = JsonSerializer.Deserialize(
            await campaignResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        Assert.NotNull(campaign?.Data);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "Use Campaign context",

                    SourceTarget = 1,

                    TokenBudget = 1_200,

                    CampaignId = campaign.Data.Id,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        _ = await response.Content.ReadAsStringAsync();

        Assert.Equal(1, provider.SearchCalls);

        Assert.NotNull(intelligence.Request);

        Assert.Equal(campaign.Data.Id, intelligence.Request.CampaignId);

        Assert.Equal(campaignPath, intelligence.Request.WorkingDirectory);

    }

    private static ArcanumWebApplicationFactory Factory(
        StubWebProvider provider,
        StubIntelligence intelligence,
        bool attachmentsEnabled = true) =>
        new()
        {

            SettingsOverride = settings => settings with
            {

                DefaultModel = "vision-model",

                Providers =
                [

                    new ProviderSettings
                    {

                        Name = "test",

                        Type = AiProviderKind.OpenAICompatible,

                        Endpoint = "https://example.test/v1",

                        Models =
                        [

                            new ModelEntry(
                                "vision-model",
                                SupportsVision: true),

                        ],

                    },

                ],

                Features = settings.Features with
                {

                    Attachments = attachmentsEnabled,

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

        public int ChangingCitationRounds { get; init; }

        /// <summary>1-based search call that throws instead of returning, or 0 to never throw.</summary>
        public int ThrowOnSearchCall { get; init; }

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

            if (ThrowOnSearchCall == SearchCalls)
            {

                throw new InvalidOperationException("citation index exploded");

            }

            int citationNumber = ChangingCitationRounds > 0
                ? Math.Min(SearchCalls, ChangingCitationRounds)
                : 1;

            return Task.FromResult(
                Result<WebSearchResult>.Success(
                    new WebSearchResult(
                        "Search summary [1].",
                        [
                            new WebCitation(
                                1,
                                $"https://example.test/source-{citationNumber}",
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
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
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
            ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {

            await Task.CompletedTask;

            yield break;

        }

    }

}
