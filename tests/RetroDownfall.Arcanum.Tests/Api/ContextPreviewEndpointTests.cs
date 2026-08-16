using System.Net;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]

public sealed class ContextPreviewEndpointTests(ArcanumWebApplicationFactory factory)
{

    [SkippableFact]

    public async Task PostContextPreview_returns_read_only_preview_without_main_inference()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        factory.FakeIntelligence.NextContextPreview = ContextPreviewTestData.Create();

        int previousInferenceCalls = factory.FakeIntelligence.ExecutePromptCallCount;

        int previousPreviewCalls = factory.FakeIntelligence.PreviewContextCallCount;

        HttpClient client = factory.CreateAuthenticatedClient();

        AttachedFileDto attachedFile = new(

            "notes.txt",

            "preview-only attachment");

        ScryingFocusDto scryingFocus = new(

            Convert.ToBase64String([1, 2, 3]),

            "image/png");

        ContextPreviewRequest request = new(

            Prompt: string.Empty,

            // No Session ID: a supplied one must now identify an existing Session. Inspection is an
            // operator surface and resolves its Campaign canonically like any other (§10.12); the
            // unknown-Session refusal has its own test below.
            SessionId: null,

            ShowContent: false,

            NoRetrieval: true,

            OverrideSpellName: "test-spell",

            AttachedFiles: [attachedFile],

            ScryingFoci: [scryingFocus],

            DisableAllTools: true,

            UnattendedMode: true,

            AdditionalSystemPrompt: "Use research synthesis policy.",

            MaxOutputTokens: 1_200,

            Temperature: 0.2f,

            TopP: 0.8f,

            Stop: ["END"],

            Seed: 42,

            ResponseFormat: "text",

            PresencePenalty: 0.1f,

            FrequencyPenalty: -0.1f);

        string payload = JsonSerializer.Serialize(

            request,

            ArcanumJsonContext.Default.ContextPreviewRequest);

        HttpResponseMessage response = await client.PostAsync(

            "/api/intelligence/context/inspect",

            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(previousInferenceCalls, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal(previousPreviewCalls + 1, factory.FakeIntelligence.PreviewContextCallCount);

        string json = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("secret system prompt", json, StringComparison.Ordinal);

        ApiResponse<ContextPreviewResult>? envelope = JsonSerializer.Deserialize(

            json,

            ArcanumJsonContext.Default.ApiResponseContextPreviewResult);

        Assert.NotNull(envelope?.Data);

        Assert.Equal("test-provider", envelope.Data.Provider);

        Assert.Null(envelope.Data.Content);

        Assert.True(factory.FakeIntelligence.LastContextPreviewRequest?.NoRetrieval);

        ContextPreviewRequest forwarded = Assert.IsType<ContextPreviewRequest>(

            factory.FakeIntelligence.LastContextPreviewRequest);

        Assert.Equal("test-spell", forwarded.OverrideSpellName);

        Assert.Equal(attachedFile, Assert.Single(forwarded.AttachedFiles!));

        Assert.Equal(scryingFocus, Assert.Single(forwarded.ScryingFoci!));

        Assert.True(forwarded.DisableAllTools);

        Assert.True(forwarded.UnattendedMode);

        Assert.Equal("Use research synthesis policy.", forwarded.AdditionalSystemPrompt);

        Assert.Equal(1_200, forwarded.MaxOutputTokens);

        Assert.Equal(0.2f, forwarded.Temperature);

        Assert.Equal(0.8f, forwarded.TopP);

        Assert.Equal(["END"], forwarded.Stop);

        Assert.Equal(42, forwarded.Seed);

        Assert.Equal("text", forwarded.ResponseFormat);

        Assert.Equal(0.1f, forwarded.PresencePenalty);

        Assert.Equal(-0.1f, forwarded.FrequencyPenalty);

    }

    [SkippableFact]

    public async Task PostContextPreview_reuses_production_prompt_bounds()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int previousCalls = factory.FakeIntelligence.PreviewContextCallCount;

        int maxPromptChars = ArcanumSettingClamps.MaxPingPromptChars(

            ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars);

        HttpClient client = factory.CreateAuthenticatedClient();

        ContextPreviewRequest request = new(Prompt: new string('x', maxPromptChars + 1));

        string payload = JsonSerializer.Serialize(

            request,

            ArcanumJsonContext.Default.ContextPreviewRequest);

        HttpResponseMessage response = await client.PostAsync(

            "/api/intelligence/context/inspect",

            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(previousCalls, factory.FakeIntelligence.PreviewContextCallCount);

    }

    [SkippableFact]

    public async Task PostContextPreview_reuses_production_campaign_resolution()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int previousCalls = factory.FakeIntelligence.PreviewContextCallCount;

        HttpClient client = factory.CreateAuthenticatedClient();

        ContextPreviewRequest request = new(CampaignId: Guid.NewGuid());

        string payload = JsonSerializer.Serialize(

            request,

            ArcanumJsonContext.Default.ContextPreviewRequest);

        HttpResponseMessage response = await client.PostAsync(

            "/api/intelligence/context/inspect",

            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(previousCalls, factory.FakeIntelligence.PreviewContextCallCount);

    }

    [SkippableFact]

    public async Task PostContextPreview_unknown_session_is_refused_rather_than_previewed()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int previousPreviewCalls = factory.FakeIntelligence.PreviewContextCallCount;

        HttpClient client = factory.CreateAuthenticatedClient();

        // A supplied Session ID must identify an existing Session. The old resolver ignored an
        // unknown one and previewed anyway, which reported context for a conversation that did not
        // exist and could not have produced it (§10.12).
        ContextPreviewRequest request = new(

            Prompt: string.Empty,

            SessionId: Guid.NewGuid());

        HttpResponseMessage response = await client.PostAsync(

            "/api/intelligence/context/inspect",

            new StringContent(

                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ContextPreviewRequest),

                Encoding.UTF8,

                "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(previousPreviewCalls, factory.FakeIntelligence.PreviewContextCallCount);

    }

}
