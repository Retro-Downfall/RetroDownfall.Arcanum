using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class OpenAiV1EndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public OpenAiV1EndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostChatCompletions_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        string payload = """
            {
              "model": "definitely-not-a-configured-model",
              "messages": [
                { "role": "user", "content": "hello" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<string>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.Equal("Auth.Unauthorized", body.Error?.Code);

    }

    [SkippableFact]
    public async Task PostChatCompletions_UnknownModel_ReturnsModelNotFound()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "definitely-not-a-configured-model",
              "messages": [
                { "role": "user", "content": "hello" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("model_not_found", body.Error.Code);

    }

    [SkippableFact]
    public async Task PostChatCompletions_UnsupportedContentPartType_Returns400InvalidValue()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        // An unknown multimodal part type was previously silently dropped by the mapper; W3.5 rejects
        // it up front with a 400 invalid_value before model resolution.
        string payload = """
            {
              "model": "any-model",
              "messages": [
                { "role": "user", "content": [ { "type": "video_url", "video_url": { "url": "x" } } ] }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_value", body!.Error.Code);

    }

    [SkippableFact]
    public async Task PostChatCompletions_ImageToNonVisionModel_ReturnsVisionNotSupported()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        // The shared fixture's default provider model ("mistral:latest") declares no
        // supportsVision — the OpenAiV1Endpoints gate must reject the image before ever calling
        // into the (fake) intelligence provider.
        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": [
                  { "type": "text", "text": "what is this?" },
                  { "type": "image_url", "image_url": { "url": "data:image/png;base64,AAAA" } }
                ] }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("vision_not_supported", body!.Error.Code);

    }

    [SkippableFact]
    public async Task PostChatCompletions_ImageToVisionCapableModel_Succeeds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "vision-model",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "vision-provider",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://example.test/v1",
                        Models = [new ModelEntry("vision-model", SupportsVision: true)],
                    },
                ],
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "vision-model",
              "messages": [
                { "role": "user", "content": [
                  { "type": "text", "text": "what is this?" },
                  { "type": "image_url", "image_url": { "url": "data:image/png;base64,AAAA" } }
                ] }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiChatResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiChatResponse);

        Assert.NotNull(body);

        Assert.Equal(factory.FakeIntelligence.NextText, body!.Choices[0].Message.Content);

    }

    [SkippableFact]
    public async Task PostChatCompletions_ImageWithScryingDisabled_ReturnsFeatureDisabled()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "vision-model",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "vision-provider",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://example.test/v1",
                        Models = [new ModelEntry("vision-model", SupportsVision: true)],
                    },
                ],
                Scrying = settings.Scrying with { Enabled = false },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "vision-model",
              "messages": [
                { "role": "user", "content": [
                  { "type": "image_url", "image_url": { "url": "data:image/png;base64,AAAA" } }
                ] }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("feature_disabled", body!.Error.Code);

    }

    [SkippableFact]
    public async Task GetModels_WithValidApiKey_ReturnsOpenAiModelList()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiModelListResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiModelListResponse);

        Assert.NotNull(body);

        Assert.Equal("list", body.ObjectKind);

        Assert.NotNull(body.Data);

    }

    [SkippableFact]
    public async Task GetModels_WithValidApiKey_IncludesCapabilityEnrichment()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiModelListResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiModelListResponse);

        Assert.NotNull(body?.Data);

        // Shared "ApiHost" factory default provider: name "test", OpenAICompatible, model
        // "mistral:latest", default ContextWindowLimit (8192), no SupportsVision declared.
        OpenAiModel model = Assert.Single(body!.Data, m => m.Id == "mistral:latest");

        Assert.Equal(8192, model.ContextWindow);

        Assert.False(model.SupportsVision);

        Assert.Equal("test", model.ProviderName);

        Assert.Equal("openai_compatible", model.ProviderType);

        Assert.True(model.SupportsTools);

        Assert.True(model.SupportsStreaming);

        Assert.Equal("test", model.OwnedBy);

    }

    [SkippableFact]
    public async Task GetModels_VisionCapableModel_ReportsSupportsVisionTrue()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "vision-model",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "vision-provider",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://example.test/v1",
                        Models = [new ModelEntry("vision-model", SupportsVision: true)],
                    },
                ],
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiModelListResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiModelListResponse);

        Assert.NotNull(body?.Data);

        OpenAiModel model = Assert.Single(body!.Data, m => m.Id == "vision-model");

        Assert.True(model.SupportsVision);

        Assert.Equal("vision-provider", model.ProviderName);

    }

    [SkippableFact]
    public async Task GetModels_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<string>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Auth.Unauthorized", body.Error?.Code);

    }

}
