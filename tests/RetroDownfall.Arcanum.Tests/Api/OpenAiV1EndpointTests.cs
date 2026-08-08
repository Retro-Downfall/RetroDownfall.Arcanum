using System.Net;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence;
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
    public async Task PostChatCompletions_BufferedFailure_UsesExactSanitizedOpenAiEnvelope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string canary = "CANARY_BUFFERED_PROVIDER_RESPONSE_BODY";
        await using ArcanumWebApplicationFactory factory = new();
        factory.FakeIntelligence.NextFailure = new Error(ErrorCodes.Hub.Error, canary);
        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "hello" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.OpenAiErrorResponse);
        Assert.NotNull(body);
        Assert.Equal("Inference failed. See server logs for details.", body.Error.Message);
        Assert.Equal("api_error", body.Error.Type);
        Assert.Equal("inference_failed", body.Error.Code);
        Assert.Null(body.Error.Param);
        Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PostChatCompletions_StreamingException_UsesExactSanitizedSseAndLogsSafeMetadata()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string canary = "CANARY_STREAM_PROVIDER_CREDENTIAL_RESPONSE_BODY";
        RecordingLoggerProvider recording = new();
        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<ILoggerFactory>();
                services.AddSingleton<ILoggerFactory>(
                    new LoggerFactory([recording]));
            },
        };
        factory.FakeIntelligence.NextStreamException = new InvalidOperationException(canary);
        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = """
            {
              "model": "mistral:latest",
              "stream": true,
              "messages": [
                { "role": "user", "content": "hello" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string sse = await response.Content.ReadAsStringAsync();
        OpenAiChatChunk errorChunk = Assert.Single(
            ParseSseChunks(sse),
            static chunk => chunk.Error is not null);
        Assert.Equal("Inference failed. See server logs for details.", errorChunk.Error?.Message);
        Assert.Equal("api_error", errorChunk.Error?.Type);
        Assert.Equal("inference_failed", errorChunk.Error?.Code);
        Assert.Null(errorChunk.Error?.Param);
        Assert.Equal("error", Assert.Single(errorChunk.Choices).FinishReason);
        Assert.Contains("data: [DONE]", sse, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, sse, StringComparison.Ordinal);

        Assert.DoesNotContain(
            recording.Entries,
            entry => entry.Message.Contains(canary, StringComparison.Ordinal)
                || entry.Exception?.ToString().Contains(canary, StringComparison.Ordinal) == true);
        LogEntry failureLog = Assert.Single(
            recording.Entries,
            static entry => entry.Message.Contains(
                "streaming OpenAI chat completion",
                StringComparison.Ordinal));
        Assert.Null(failureLog.Exception);
        Assert.Contains(
            nameof(InvalidOperationException),
            failureLog.Message,
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PostChatCompletions_ReasoningFields_MapToNormalizedRequest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        

        await using ArcanumWebApplicationFactory factory = new()
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
                        Models =
                        [
                            new ModelEntry(
                                "reasoner",
                                Reasoning: new ModelReasoningSettings
                                {
                                    WireDialect = ReasoningWireDialect.OpenRouter,
                                }),
                        ],
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

        

        await using ArcanumWebApplicationFactory factory = new()
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
                        Models =
                        [
                            new ModelEntry(
                                "reasoner",
                                Reasoning: new ModelReasoningSettings
                                {
                                    WireDialect = ReasoningWireDialect.OpenRouter,
                                }),
                        ],
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

        
        await using ArcanumWebApplicationFactory factory = new()
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
                        Models =
                        [
                            new ModelEntry(
                                "reasoner",
                                Reasoning: new ModelReasoningSettings
                                {
                                    WireDialect = ReasoningWireDialect.OpenRouter,
                                    MaxBudgetTokens = 64,
                                }),
                        ],
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

        Assert.Null(model.WireDialect);

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
                                Reasoning = new ModelReasoningSettings
                                {
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

        Assert.Equal(ReasoningWireDialect.AnthropicThinking, model.WireDialect);
        Assert.Equal(32_768, model.MaxBudgetTokens);
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

    private static List<OpenAiChatChunk> ParseSseChunks(string sse) =>
        sse.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith("data: {", StringComparison.Ordinal))
            .Select(static line => JsonSerializer.Deserialize(
                line["data: ".Length..],
                ArcanumJsonContext.Default.OpenAiChatChunk))
            .OfType<OpenAiChatChunk>()
            .ToList();

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(
                    logLevel,
                    formatter(state, exception),
                    exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);

}
