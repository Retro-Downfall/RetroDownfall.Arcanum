using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
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
    public async Task PostChatCompletions_ReasoningFields_MapToNormalizedRequest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ReasoningCapabilities capabilities = new()
        {
            ControlSupport = ReasoningControlSupport.Effort,
            SupportsSummary = true,
            AllowsClientOutput = true,
            WireDialect = ReasoningWireDialect.Standard,
        };

        using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "reasoner",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "explicitly-configured",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://example.test/v1",
                        Models = [new ModelEntry("reasoner", Reasoning: capabilities)],
                    },
                ],
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "reasoner",
              "messages": [
                { "role": "user", "content": "solve" }
              ],
              "reasoning_effort": "xhigh",
              "reasoning_output": "summary"
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new ReasoningRequestOptions(
                Effort: ReasoningEffortLevel.ExtraHigh,
                Output: ReasoningOutputMode.Summary),
            factory.FakeIntelligence.LastRequest?.Reasoning);
    }

    [SkippableFact]
    public async Task PostChatCompletions_DefinedAndUndefinedNumericReasoningEnums_ReturnInvalidJson()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ReasoningCapabilities capabilities = new()
        {
            ControlSupport = ReasoningControlSupport.Effort,
            WireDialect = ReasoningWireDialect.Standard,
        };

        using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "reasoner",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "explicitly-configured",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://example.test/v1",
                        Models = [new ModelEntry("reasoner", Reasoning: capabilities)],
                    },
                ],
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        foreach (string propertyName in new[] { "reasoning_effort", "reasoning_output" })
        {
            foreach (int numericValue in new[] { 0, 99 })
            {
                string payload = $$"""
                    {
                      "model": "reasoner",
                      "messages": [
                        { "role": "user", "content": "solve" }
                      ],
                      "{{propertyName}}": {{numericValue}}
                    }
                    """;

                HttpResponseMessage response = await client.PostAsync(
                    "/v1/chat/completions",
                    new StringContent(payload, Encoding.UTF8, "application/json"));

                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                string json = await response.Content.ReadAsStringAsync();
                OpenAiErrorResponse? body = JsonSerializer.Deserialize(
                    json,
                    ArcanumJsonContext.Default.OpenAiErrorResponse);
                Assert.Equal("invalid_json", body?.Error.Code);
            }
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostChatCompletions_SemanticReasoningValidation_UsesSharedTypedErrors(
        bool stream)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ReasoningCapabilities capabilities = new()
        {
            ControlSupport = ReasoningControlSupport.Budget,
            AllowsClientOutput = false,
            WireDialect = ReasoningWireDialect.OpenRouter,
            MaxBudgetTokens = 64,
        };
        using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "reasoner",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "reasoning-validation",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://example.test/v1",
                        Models = [new ModelEntry("reasoner", Reasoning: capabilities)],
                    },
                ],
            },
        };
        HttpClient client = factory.CreateAuthenticatedClient();
        (string Fields, string InternalCode)[] cases =
        [
            (
                "\"reasoning_effort\": \"low\", \"reasoning_budget\": 32",
                ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive),
            (
                "\"reasoning_budget\": 0",
                ErrorCodes.Validation.InvalidReasoningBudget),
            (
                "\"reasoning_budget\": 2097153",
                ErrorCodes.Validation.InvalidReasoningBudget),
            (
                "\"reasoning_budget\": 65",
                ErrorCodes.Validation.ReasoningBudgetExceedsModelLimit),
            (
                "\"reasoning_effort\": \"low\"",
                ErrorCodes.Validation.UnsupportedReasoningControl),
            (
                "\"reasoning_output\": \"summary\"",
                ErrorCodes.Validation.UnsupportedReasoningOutput),
        ];

        foreach ((string fields, string internalCode) in cases)
        {
            string payload = $$"""
                {
                  "model": "reasoner",
                  "messages": [
                    { "role": "user", "content": "solve" }
                  ],
                  "stream": {{stream.ToString().ToLowerInvariant()}},
                  {{fields}}
                }
                """;

            HttpResponseMessage response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            string json = await response.Content.ReadAsStringAsync();
            OpenAiErrorResponse? body = JsonSerializer.Deserialize(
                json,
                ArcanumJsonContext.Default.OpenAiErrorResponse);
            Assert.NotNull(body);
            Assert.Equal(
                OpenAiStreamErrorMapper.Map(new Error(internalCode, "unsafe detail")),
                body!.Error);
            Assert.Null(factory.FakeIntelligence.LastRequest);
        }
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
                Features = settings.Features with { Scrying = false },
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
    public async Task GetModels_NativeAndOpenAiSurfacesShareConfiguredProviderModelInventory()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "alpha-model",
                FastModel = "alpha-model",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "provider-a",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://a.example/v1",
                        Models =
                        [
                            new ModelEntry("alpha-model"),
                            new ModelEntry("Shared-Model", SupportsVision: true),
                        ],
                    },
                    new ProviderSettings
                    {
                        Name = "provider-b",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://b.example/v1",
                        Models =
                        [
                            new ModelEntry("beta-model"),
                            new ModelEntry("shared-model"),
                        ],
                    },
                ],
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage nativeResponse = await client.GetAsync("/api/models");
        HttpResponseMessage openAiResponse = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, nativeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, openAiResponse.StatusCode);

        ApiResponse<ModelInfoDto[]>? native = JsonSerializer.Deserialize(
            await nativeResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray);
        OpenAiModelListResponse? openAi = JsonSerializer.Deserialize(
            await openAiResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiModelListResponse);

        Assert.NotNull(native?.Data);
        Assert.NotNull(openAi?.Data);
        Assert.Equal(4, native!.Data!.Length);
        Assert.Contains(
            native.Data,
            static model => model.Model == "Shared-Model" && model.ProviderName == "provider-a");
        Assert.Contains(
            native.Data,
            static model => model.Model == "shared-model" && model.ProviderName == "provider-b");

        HashSet<string> configuredIds = native.Data
            .Select(static model => model.Model)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(configuredIds.Count, openAi!.Data.Count);
        Assert.All(
            configuredIds,
            id => Assert.Contains(
                openAi.Data,
                model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase)));

        OpenAiModel shared = Assert.Single(
            openAi.Data,
            static model => string.Equals(model.Id, "shared-model", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("provider-a", shared.ProviderName);
        Assert.True(shared.SupportsVision);

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

        Assert.Null(model.Reasoning);

    }

    [SkippableFact]
    public async Task GetModels_KnownCatalogModel_ReportsVisionReasoningAndPromptCachingMetadata()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "gpt-5",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "vision-provider",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://api.openai.com/v1",
                        Models =
                        [
                            new ModelEntry("gpt-5", SupportsVision: true)
                            {
                                Reasoning = new ReasoningCapabilities
                                {
                                    ControlSupport = ReasoningControlSupport.Budget,
                                    SupportsSummary = true,
                                    SupportsStreaming = true,
                                    ReportsReasoningTokens = true,
                                    AllowsClientOutput = true,
                                    WireDialect = ReasoningWireDialect.AnthropicThinking,
                                    MaxBudgetTokens = 32_768,
                                },
                            },
                        ],
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

        OpenAiModel model = Assert.Single(body!.Data, m => m.Id == "gpt-5");

        Assert.True(model.SupportsVision);

        Assert.Equal("vision-provider", model.ProviderName);

        Assert.NotNull(model.Reasoning);
        Assert.Equal(ReasoningControlSupport.Budget, model.Reasoning!.ControlSupport);
        Assert.True(model.Reasoning.SupportsSummary);
        Assert.True(model.Reasoning.SupportsStreaming);
        Assert.True(model.Reasoning.ReportsReasoningTokens);
        Assert.True(model.Reasoning.AllowsClientOutput);
        Assert.Equal(ReasoningWireDialect.AnthropicThinking, model.Reasoning.WireDialect);
        Assert.Equal(32_768, model.Reasoning.MaxBudgetTokens);
        Assert.Equal(PromptCachingControlMode.Explicit, model.PromptCaching?.ControlMode);
        Assert.Equal(
            PromptCachingWireDialect.OpenAiPromptCacheRetention,
            model.PromptCaching?.WireDialect);
        Assert.True(model.PromptCaching?.EmitCacheKey);

    }

    [SkippableFact]
    public async Task GetModels_DuplicateModelWithMatchingProviderProfiles_RetainsPromptCachingMetadata()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "gpt-5",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "provider-a",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://api.openai.com/v1",
                        Models = [new ModelEntry("gpt-5")],
                    },
                    new ProviderSettings
                    {
                        Name = "provider-b",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://api.openai.com/v1",
                        Models = [new ModelEntry("GPT-5")],
                    },
                ],
            },
        };
        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        OpenAiModelListResponse? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.OpenAiModelListResponse);
        OpenAiModel model = Assert.Single(
            body!.Data,
            static entry => string.Equals(entry.Id, "gpt-5", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PromptCachingControlMode.Explicit, model.PromptCaching?.ControlMode);
        Assert.Equal(
            PromptCachingWireDialect.OpenAiPromptCacheRetention,
            model.PromptCaching?.WireDialect);
        Assert.True(model.PromptCaching?.CacheKeysSupported);
        Assert.True(model.PromptCaching?.EmitCacheKey);
        Assert.True(model.PromptCaching?.ReportsCachedInputUsage);
    }

    [SkippableFact]
    public async Task GetModels_DuplicateModelWithDifferentProviderProfiles_OmitsPromptCachingMetadata()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "gpt-5",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "provider-a",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://api.openai.com/v1",
                        Models = [new ModelEntry("gpt-5")],
                    },
                    new ProviderSettings
                    {
                        Name = "provider-b",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://b.example/v1",
                        Models = [new ModelEntry("gpt-5")],
                    },
                ],
            },
        };
        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        OpenAiModelListResponse? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.OpenAiModelListResponse);
        OpenAiModel model = Assert.Single(body!.Data, static entry => entry.Id == "gpt-5");
        Assert.Null(model.PromptCaching);
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
