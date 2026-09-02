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

using RetroDownfall.Arcanum.Core.Platform;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

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

        StubWebProvider provider = new();

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            new StubIntelligence());

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/web/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.NotNull(provider.LastSearchOptions);

        // Normalized where the options are built, not merely tolerated downstream: otherwise every
        // provider that reads these filters has to defend against the same null on its own.
        Assert.NotNull(provider.LastSearchOptions.IncludeDomains);

        Assert.NotNull(provider.LastSearchOptions.ExcludeDomains);

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

        string campaignPath = Path.Combine(
            factory.TempHome,
            $"research-continuation-campaign-{Guid.NewGuid():N}");

        Directory.CreateDirectory(campaignPath);

        RegisterCampaignRequest campaignRegistration = new(
            "Research Continuation Campaign",
            campaignPath,
            WorkspaceType.Campaign,
            null);

        HttpResponseMessage campaignRegistrationResponse = await client.PostAsync(
            "/api/campaigns",
            new StringContent(
                JsonSerializer.Serialize(
                    campaignRegistration,
                    ArcanumJsonContext.Default.RegisterCampaignRequest),
                Encoding.UTF8,
                "application/json"));

        ApiResponse<CampaignDto>? registeredCampaign = JsonSerializer.Deserialize(
            await campaignRegistrationResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        // The campaign egress ward denies every citation fetch for a campaign id that does not
        // resolve, so a research run that is expected to render sources must name a registered
        // campaign rather than a literal id.
        Guid campaignId = Assert.IsType<Guid>(registeredCampaign?.Data?.Id);

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

    /// <summary>
    /// Citation URLs come from the search provider, not the operator, so a campaign Sanctum that
    /// restricts egress must gate them exactly as it gates the <c>read_url</c> tool. Otherwise the
    /// endpoint is a way to fetch any host the provider names from inside a contained campaign.
    /// </summary>
    [SkippableFact]

    public async Task Research_does_not_fetch_a_citation_the_campaign_sanctum_denies()

    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        StubSanctumGuard sanctum = new()
        {

            DeniedHosts = new(StringComparer.OrdinalIgnoreCase) { "example.test" },

        };

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            new StubIntelligence(),
            sanctum: sanctum);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid campaignId = await RegisterCampaignAsync(client, factory);

        _ = await ResearchAsync(client, campaignId);

        Assert.Empty(provider.ReadUrls);

        Assert.Contains(
            sanctum.NetworkChecks,
            check => check.Url.Contains("example.test", StringComparison.OrdinalIgnoreCase)
                && check.CampaignId == campaignId.ToString());

    }

    /// <summary>
    /// A citation host the Sanctum allows is still only the first hop. Without a per-hop ward on the
    /// read options one <c>302</c> off that allowed host converts a contained campaign into arbitrary
    /// outbound egress, so the ward has to travel with every fetch.
    /// </summary>
    [SkippableFact]

    public async Task Research_carries_the_campaign_egress_ward_into_every_citation_fetch()

    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        StubSanctumGuard sanctum = new()
        {

            DeniedHosts = new(StringComparer.OrdinalIgnoreCase) { "redirected.test" },

        };

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            new StubIntelligence(),
            sanctum: sanctum);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid campaignId = await RegisterCampaignAsync(client, factory);

        _ = await ResearchAsync(client, campaignId);

        Assert.NotEmpty(provider.ReadUrls);

        Assert.NotNull(provider.LastReadOptions);

        Assert.NotNull(provider.LastReadOptions.RedirectEgressWard);

        Assert.False(
            await provider.LastReadOptions.RedirectEgressWard(
                new Uri("https://redirected.test/hop"),
                CancellationToken.None));

        Assert.True(
            await provider.LastReadOptions.RedirectEgressWard(
                new Uri("https://example.test/hop"),
                CancellationToken.None));

    }

    /// <summary>
    /// No campaign means no Sanctum to enforce, so an uncontained research run must keep fetching
    /// exactly as before rather than gaining a ward that denies everything.
    /// </summary>
    [SkippableFact]

    public async Task Research_without_a_campaign_fetches_citations_unwarded()

    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        StubSanctumGuard sanctum = new()
        {

            DeniedHosts = new(StringComparer.OrdinalIgnoreCase) { "example.test" },

        };

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            new StubIntelligence(),
            sanctum: sanctum);

        HttpClient client = factory.CreateAuthenticatedClient();

        _ = await ResearchAsync(client, campaignId: null);

        Assert.NotEmpty(provider.ReadUrls);

        Assert.Empty(sanctum.NetworkChecks);

        Assert.NotNull(provider.LastReadOptions);

        Assert.Null(provider.LastReadOptions.RedirectEgressWard);

    }

    /// <summary>
    /// Without a <c>sourceTarget</c> the discovery loop runs until a pass finds nothing new, and every
    /// fetched page was retained in full until synthesis finished — even though the synthesis prompt
    /// budget consumes only the first few. Fetching stops once the retained material covers that budget,
    /// so the phase cannot accumulate megabytes it will never read.
    /// </summary>
    [SkippableFact]

    public async Task Research_stops_fetching_once_the_synthesis_prompt_budget_is_covered()

    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new()
        {

            ChangingCitationRounds = 8,

            ReadMarkdown = new string('x', 20_000),

        };

        StubIntelligence intelligence = new();

        await using ArcanumWebApplicationFactory factory = Factory(provider, intelligence);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "What changed?",

                    TokenBudget = 1_200,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        _ = await response.Content.ReadAsStringAsync();

        // Eight unique citations were discovered; the prompt can hold nowhere near eight 20,000-char
        // pages, so most of them must never be fetched at all.
        Assert.True(
            provider.ReadCalls < 8,
            $"fetched {provider.ReadCalls} sources for a prompt budget that cannot hold them.");

        Assert.True(provider.ReadCalls >= 1, "no source was fetched at all.");

        Assert.NotNull(intelligence.Request);

        // Retained page text never exceeds what the synthesis prompt can carry.
        Assert.True(
            intelligence.Request.Prompt.Length <= 32_768,
            $"synthesis prompt was {intelligence.Request.Prompt.Length} characters.");

    }

    /// <summary>
    /// Attachment is an optional side effect on an answer the operator has already paid for. The
    /// preflight narrows the window but cannot close it — the target session can still be archived or
    /// purged between preflight and persist — and when it is, the searches, the citation fetches and
    /// the synthesis model call have all been billed. The stream must still deliver the answer.
    /// </summary>
    [SkippableFact]

    public async Task Research_still_emits_the_billed_answer_when_attachment_fails_late()

    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        StubIntelligence intelligence = new();

        await using ArcanumWebApplicationFactory factory = Factory(
            provider,
            intelligence,
            vanishSessionAfterCalls: 1);

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage createdResponse = await client.PostAsJsonAsync(
            "/api/sessions",
            new CreateSessionRequest(null, "Research target"),
            ArcanumJsonContext.Default.CreateSessionRequest);

        ApiResponse<SessionDetailDto>? session = JsonSerializer.Deserialize(
            await createdResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(session?.Data);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "What changed?",

                    SourceTarget = 1,

                    TokenBudget = 1_200,

                    AttachToSessionId = session.Data.Id,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string ndjson = await response.Content.ReadAsStringAsync();

        WebResearchStreamFrame[] frames = ndjson
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonSerializer.Deserialize(
                line,
                ArcanumJsonContext.Default.WebResearchStreamFrame)!)
            .ToArray();

        WebResearchStreamFrame terminal = frames[^1];

        Assert.Equal(WebResearchStreamFrameType.Result, terminal.Type);

        Assert.NotNull(terminal.Result);

        Assert.Equal("Synthesized answer [1].", terminal.Result.Answer);

        // The attachment did not happen, and the stream says so rather than swallowing it.
        Assert.Null(terminal.Result.AttachmentId);

        Assert.Contains(
            frames,
            static frame => frame.Type == WebResearchStreamFrameType.Progress
                && frame.Stage == "attachment_failed");

        // The progress frame is the whole story: unlike search and browse, the research result carries
        // no `attachmentError` of its own. A permanently-null field on the terminal frame would read to
        // a client as "the attachment succeeded", which is the opposite of what happened.
        string terminalLine = ndjson
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];

        using JsonDocument terminalJson = JsonDocument.Parse(terminalLine);

        Assert.False(
            terminalJson.RootElement
                .GetProperty("result")
                .TryGetProperty("attachmentError", out _),
            $"the research result frame carried an attachmentError property: {terminalLine}");

    }

    /// <summary>
    /// Search and browse have no progress channel, so the research path's <c>attachment_failed</c> frame
    /// has no counterpart here: a failed attachment discarded the whole result, including the answer the
    /// caller has already been billed for. The billed work is returned and the attachment failure is
    /// reported alongside it, rather than one information loss being traded for another.
    /// </summary>
    [SkippableFact]

    public async Task Search_still_returns_the_billed_answer_when_attachment_fails_late()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Factory(
            new StubWebProvider(),
            new StubIntelligence(),
            vanishSessionAfterCalls: 1);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/web/search",
            new WebSearchWorkflowRequest
            {

                Query = "current facts",

                ResultCount = 3,

                AttachToSessionId = sessionId,

            },
            ArcanumJsonContext.Default.WebSearchWorkflowRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<WebSearchWorkflowResult>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseWebSearchWorkflowResult);

        Assert.NotNull(envelope?.Data);

        Assert.True(envelope.IsSuccess);

        Assert.False(string.IsNullOrWhiteSpace(envelope.Data.Answer));

        Assert.Null(envelope.Data.AttachmentId);

        Assert.False(string.IsNullOrWhiteSpace(envelope.Data.AttachmentError));

    }

    [SkippableFact]

    public async Task Browse_still_returns_the_billed_page_when_attachment_fails_late()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Factory(
            new StubWebProvider(),
            new StubIntelligence(),
            vanishSessionAfterCalls: 1);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/web/browse",
            new WebBrowseWorkflowRequest
            {

                Url = "https://example.test/article",

                AttachToSessionId = sessionId,

            },
            ArcanumJsonContext.Default.WebBrowseWorkflowRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<WebBrowseWorkflowResult>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseWebBrowseWorkflowResult);

        Assert.NotNull(envelope?.Data);

        Assert.True(envelope.IsSuccess);

        Assert.False(string.IsNullOrWhiteSpace(envelope.Data.Markdown));

        Assert.Null(envelope.Data.AttachmentId);

        Assert.False(string.IsNullOrWhiteSpace(envelope.Data.AttachmentError));

    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {

        HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/sessions",
            new CreateSessionRequest(null, "Attachment target"),
            ArcanumJsonContext.Default.CreateSessionRequest);

        ApiResponse<SessionDetailDto>? session = JsonSerializer.Deserialize(
            await created.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(session?.Data);

        return session.Data.Id;

    }

    private static async Task<Guid> RegisterCampaignAsync(
        HttpClient client,
        ArcanumWebApplicationFactory factory)
    {

        string campaignPath = Path.Combine(
            factory.TempHome,
            $"research-campaign-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(campaignPath);

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

        return campaign.Data.Id;

    }

    private static async Task<string> ResearchAsync(
        HttpClient client,
        Guid? campaignId)
    {

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/web/research")
        {

            Content = JsonContent.Create(
                new WebResearchWorkflowRequest
                {

                    Question = "What changed?",

                    SourceTarget = 1,

                    TokenBudget = 1_200,

                    CampaignId = campaignId,

                },
                ArcanumJsonContext.Default.WebResearchWorkflowRequest),

        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();

    }

    private static ArcanumWebApplicationFactory Factory(
        StubWebProvider provider,
        StubIntelligence intelligence,
        bool attachmentsEnabled = true,
        StubSanctumGuard? sanctum = null,
        int vanishSessionAfterCalls = 0) =>
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

                if (sanctum is not null)
                {

                    services.RemoveAll<ISanctumGuard>();

                    services.AddScoped<ISanctumGuard>(_ => sanctum);

                }

                if (vanishSessionAfterCalls > 0)
                {

                    services.RemoveAll<ISessionRepository>();

                    services.AddScoped<SessionRepository>();

                    services.AddScoped<ISessionRepository>(
                        sp => new VanishingSessionRepository(
                            sp.GetRequiredService<SessionRepository>(),
                            vanishSessionAfterCalls));

                }

            },

        };

    /// <summary>
    /// Reproduces the archive/purge race the attachment preflight cannot close: the target session
    /// resolves for the preflight and is gone by the time the workflow tries to persist onto it.
    /// </summary>
    private sealed class VanishingSessionRepository(
        ISessionRepository inner,
        int lookupsBeforeVanishing) : ISessionRepository
    {

        private int _lookups;

        public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Interlocked.Increment(ref _lookups) > lookupsBeforeVanishing
                ? Task.FromResult<Session?>(null)
                : inner.GetByIdAsync(id, ct);

        public Task<Session> CreateAsync(Guid? campaignId, string? title, CancellationToken ct) =>
            inner.CreateAsync(campaignId, title, ct);

        public Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct) =>
            inner.QueryAsync(request, ct);

        public Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct) =>
            inner.GetAnalyticsAsync(ct);

        public Task<Result<SessionExportResult>> ExportAsync(
            Guid id,
            SessionExportFormat format,
            CancellationToken ct) =>
            inner.ExportAsync(id, format, ct);

        public Task<Result<Entry>> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct) =>
            inner.AddEntryAsync(sessionId, entry, ct);

        public Task<Result<Session>> ForkAsync(Guid sourceId, ForkSessionRequest request, CancellationToken ct) =>
            inner.ForkAsync(sourceId, request, ct);

        public Task<List<Entry>> GetEntriesAscendingAsync(
            Guid sessionId,
            int takeLast,
            CancellationToken ct = default) =>
            inner.GetEntriesAscendingAsync(sessionId, takeLast, ct);

        public Task<List<Entry>> GetEntriesAfterAsync(
            Guid sessionId,
            long afterSequence,
            int limit,
            CancellationToken ct = default) =>
            inner.GetEntriesAfterAsync(sessionId, afterSequence, limit, ct);

        public Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default) =>
            inner.GetEntryAsync(sessionId, entryId, ct);

        public Task<List<Entry>> GetEntriesAsync(
            Guid sessionId,
            int offset = 0,
            int limit = 100,
            DateTimeOffset? beforeCreatedAt = null,
            Guid? beforeId = null,
            CancellationToken ct = default) =>
            inner.GetEntriesAsync(sessionId, offset, limit, beforeCreatedAt, beforeId, ct);

        public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) =>
            inner.GetEntryCountAsync(sessionId, ct);

        public Task UpdateSessionAsync(Session session, CancellationToken ct) =>
            inner.UpdateSessionAsync(session, ct);

        public Task ArchiveAsync(Guid id, CancellationToken ct) =>
            inner.ArchiveAsync(id, ct);

    }

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

        public List<string> ReadUrls { get; } = [];

        public WebReadOptions? LastReadOptions { get; private set; }

        /// <summary>Markdown returned per read, or null for the default single-line body.</summary>
        public string? ReadMarkdown { get; init; }

        public Task<Result<WebReadResult>> ReadUrlAsync(
            string url,
            WebReadOptions options,
            CancellationToken cancellationToken = default)
        {

            ReadCalls++;

            ReadUrls.Add(url);

            LastReadOptions = options;

            return Task.FromResult(
                Result<WebReadResult>.Success(
                    new WebReadResult(
                        "Source",
                        ReadMarkdown ?? "Rendered evidence.",
                        url,
                        [])));

        }

    }

    /// <summary>
    /// Records every campaign network check and denies the hosts it is told to deny, standing in for
    /// the real guard's config lookup so the workflow's own enforcement is what the test observes.
    /// </summary>
    private sealed class StubSanctumGuard : ISanctumGuard
    {

        public List<(string CampaignId, string Url, string ToolName)> NetworkChecks { get; } = [];

        /// <summary>Hosts denied by this Sanctum; empty denies nothing.</summary>
        public HashSet<string> DeniedHosts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default)
        {

            NetworkChecks.Add((campaignId, url, toolName));

            bool denied = Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                && DeniedHosts.Contains(parsed.Host);

            return Task.FromResult(
                new SanctumResult
                {

                    Allowed = !denied,

                    DenyReason = denied
                        ? $"Host '{parsed!.Host}' is not in the Sanctum allowed domain list."
                        : null,

                });

        }

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

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
