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

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostChatCompletions_Timeout_UsesExactOpenAiCopy(bool stream)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string nativeTimeout =
            "Inference timed out. Increase Arcanum:Intelligence:InferenceTimeoutSeconds or retry with a shorter prompt.";
        await using ArcanumWebApplicationFactory factory = new();
        factory.FakeIntelligence.NextFailure = new Error(
            ErrorCodes.Hub.Timeout,
            nativeTimeout);
        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = $$"""
            {
              "model": "mistral:latest",
              "stream": {{stream.ToString().ToLowerInvariant()}},
              "messages": [
                { "role": "user", "content": "hello" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        string serialized = await response.Content.ReadAsStringAsync();
        OpenAiErrorDetail error;
        if (stream)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            OpenAiChatChunk errorChunk = Assert.Single(
                ParseSseChunks(serialized),
                static chunk => chunk.Error is not null);
            error = errorChunk.Error!;
        }
        else
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            OpenAiErrorResponse? body = JsonSerializer.Deserialize(
                serialized,
                ArcanumJsonContext.Default.OpenAiErrorResponse);
            Assert.NotNull(body);
            error = body.Error;
        }

        Assert.Equal("Inference timed out.", error.Message);
        Assert.Equal("api_error", error.Type);
        Assert.Equal("server_error", error.Code);
        Assert.DoesNotContain(nativeTimeout, serialized, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PostChatCompletions_ProductionWizardStreamingTimeout_UsesExactSingleOpenAiError()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Intelligence = settings.Intelligence with
                {
                    InferenceTimeoutSeconds = 1,
                    EnableLexiconSystem = false,
                },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<IArcanumIntelligenceProvider>();
                services.AddScoped<IArcanumIntelligenceProvider>(
                    static sp => sp.GetRequiredService<WizardIntelligenceProvider>());
                services.RemoveAll<IChatClientFactory>();
                services.AddSingleton<IChatClientFactory, TimeoutChatClientFactory>();
            },
        };

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            Assert.IsType<WizardIntelligenceProvider>(
                scope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>());
        }

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
        Assert.Equal("Inference timed out.", errorChunk.Error?.Message);
        Assert.Equal("api_error", errorChunk.Error?.Type);
        Assert.Equal("server_error", errorChunk.Error?.Code);
        Assert.Equal("error", Assert.Single(errorChunk.Choices).FinishReason);
        Assert.Equal(
            1,
            ParseSseChunks(sse).Count(static chunk =>
                chunk.Error is not null
                || chunk.Choices.Any(static choice => choice.FinishReason == "error")));
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

        Assert.Null(model.Reasoning);

    }

    [SkippableFact]
    public async Task GetModels_CapableModel_ReportsVisionReasoningAndPromptCachingMetadata()
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
                        Models =
                        [
                            new ModelEntry("vision-model", SupportsVision: true)
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
                                PromptCaching = new PromptCachingProfile
                                {
                                    ControlMode = PromptCachingControlMode.Explicit,
                                    WireDialect = PromptCachingWireDialect.OpenAiPromptCacheRetention,
                                    CacheKeysSupported = true,
                                    EmitCacheKey = true,
                                    ReportsCachedInputUsage = true,
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

        OpenAiModel model = Assert.Single(body!.Data, m => m.Id == "vision-model");

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
    public async Task GetModels_DuplicateModelWithDifferentProviderProfiles_OmitsPromptCachingMetadata()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                DefaultModel = "shared-model",
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "provider-a",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://a.example/v1",
                        Models =
                        [
                            new ModelEntry("shared-model")
                            {
                                PromptCaching = new PromptCachingProfile
                                {
                                    ControlMode = PromptCachingControlMode.Explicit,
                                    CacheKeysSupported = true,
                                    EmitCacheKey = true,
                                },
                            },
                        ],
                    },
                    new ProviderSettings
                    {
                        Name = "provider-b",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://b.example/v1",
                        Models =
                        [
                            new ModelEntry("shared-model")
                            {
                                PromptCaching = new PromptCachingProfile
                                {
                                    ControlMode = PromptCachingControlMode.None,
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
        OpenAiModelListResponse? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.OpenAiModelListResponse);
        OpenAiModel model = Assert.Single(body!.Data, static entry => entry.Id == "shared-model");
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

    private sealed class TimeoutChatClientFactory : IChatClientFactory
    {
        private static readonly ProviderSettings Provider = new()
        {
            Name = "test",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            Models = ["mistral:latest"],
        };

        public Task<ChatClientLease> ResolveClientAsync(
            string? targetModel,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(
                new TimeoutChatClient(),
                Provider,
                "mistral:latest",
                ownedHttpClient: null));

        public Task<ChatClientLease> ResolveClientAsync(
            ProviderSettings provider,
            string resolvedModel,
            CancellationToken cancellationToken) =>
            ResolveClientAsync(resolvedModel, cancellationToken);
    }

    private sealed class TimeoutChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after infinite delay.");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

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
