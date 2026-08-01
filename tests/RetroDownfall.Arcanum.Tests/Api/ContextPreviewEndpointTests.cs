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

        ContextPreviewRequest request = new(

            Prompt: string.Empty,

            SessionId: Guid.NewGuid(),

            ShowContent: false,

            NoRetrieval: true);

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

}
