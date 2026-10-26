using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
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

    [SkippableTheory]
    [InlineData("[null]", 0)]
    [InlineData("[{\"role\":\"user\",\"content\":\"hello\"},null]", 1)]
    public async Task PostChatCompletions_NullMessage_ReturnsIndexed400WithoutInference(string messages, int index)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        foreach (bool stream in new[] { false, true })
        {
            string payload = $$"""{"model":"mistral:latest","messages":{{messages}},"stream":{{stream.ToString().ToLowerInvariant()}}}""";

            using HttpResponseMessage response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            OpenAiErrorResponse? body = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.OpenAiErrorResponse);

            Assert.NotNull(body);

            Assert.Equal("invalid_request_error", body.Error.Type);

            Assert.Equal("invalid_value", body.Error.Code);

            Assert.Equal($"messages[{index}]", body.Error.Param);
        }

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal(0, factory.FakeIntelligence.StreamPromptCallCount);
    }

    [SkippableTheory]
    [InlineData(false, ErrorCodes.Hub.ContextBudgetExceeded, 400, "invalid_request_error", "context_length_exceeded", "arcanum session compact")]
    [InlineData(true, ErrorCodes.Hub.ContextBudgetExceeded, 200, "invalid_request_error", "context_length_exceeded", "arcanum session compact")]
    [InlineData(false, ErrorCodes.Budget.Exceeded, 429, "insufficient_quota", "insufficient_quota", "Arcanum:Cost:Budget:DailyLimitUsd")]
    [InlineData(true, ErrorCodes.Budget.Exceeded, 200, "insufficient_quota", "insufficient_quota", "Arcanum:Cost:Budget:DailyLimitUsd")]
    public async Task PostChatCompletions_ContextAndBudgetRefusals_AreActionableWithoutLeakingMessages(
        bool stream, string code, int status, string type, string publicCode, string remedy)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string canary = "SECRET_PROVIDER_RESPONSE_AND_PROMPT";

        await using ArcanumWebApplicationFactory factory = new();

        factory.FakeIntelligence.NextFailure = new Error(code, canary);

        using HttpClient client = factory.CreateAuthenticatedClient();

        string payload = $$"""{"model":"mistral:latest","messages":[{"role":"user","content":"hello"}],"stream":{{stream.ToString().ToLowerInvariant()}}}""";

        using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent(payload, Encoding.UTF8, "application/json"));

        string wire = await response.Content.ReadAsStringAsync();

        Assert.Equal(status, (int)response.StatusCode);

        OpenAiErrorDetail detail = stream
            ? Assert.Single(ParseSseChunks(wire), chunk => chunk.Error is not null).Error!
            : JsonSerializer.Deserialize(wire, ArcanumJsonContext.Default.OpenAiErrorResponse)!.Error;

        Assert.Equal(type, detail.Type);

        Assert.Equal(publicCode, detail.Code);

        Assert.Contains(remedy, detail.Message);

        Assert.DoesNotContain(canary, wire);

        if (stream)
        {
            Assert.Contains("data: [DONE]", wire);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostChatCompletions_TypedContextOverflow_PreservesSafeCountsAcrossTransports(bool stream)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        ContextOverflowDetails overflow = new("configured-provider", "configured-model", 9000, 1024, 10024, 8192);

        factory.FakeIntelligence.NextFailure = new Error(ErrorCodes.Hub.ContextBudgetExceeded, "SECRET_RESPONSE") { ContextOverflow = overflow };

        using HttpClient client = factory.CreateAuthenticatedClient();

        string payload = $$"""{"model":"mistral:latest","messages":[{"role":"user","content":"hello"}],"stream":{{stream.ToString().ToLowerInvariant()}}}""";

        using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent(payload, Encoding.UTF8, "application/json"));

        string wire = await response.Content.ReadAsStringAsync();

        Assert.Equal(stream ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);

        OpenAiErrorDetail detail = stream
            ? Assert.Single(ParseSseChunks(wire), chunk => chunk.Error is not null).Error!
            : JsonSerializer.Deserialize(wire, ArcanumJsonContext.Default.OpenAiErrorResponse)!.Error;

        Assert.Equal(overflow.ToPublicMessage(), detail.Message);

        Assert.Contains("configured-provider", detail.Message);

        Assert.Contains("configured-model", detail.Message);

        Assert.Contains("10024", detail.Message);

        Assert.Contains("8192", detail.Message);

        Assert.DoesNotContain("SECRET_RESPONSE", wire);

        Assert.DoesNotContain("contextOverflow", wire);
    }

    [Theory]
    [InlineData(ErrorCodes.Hub.Error)]
    [InlineData(ErrorCodes.Hub.InvalidAdmission)]
    [InlineData(ErrorCodes.LongRest.InvalidAdmission)]
    [InlineData(ErrorCodes.Budget.Exceeded)]
    public void NonOverflowErrors_NeverConsumeAttachedOverflowMetadata(string code)
    {
        Error error = new(code, "SECRET_RESPONSE") { ContextOverflow = new("UNRELATED_PROVIDER", "UNRELATED_MODEL", 9000, 1024, 10024, 8192) };

        string message = OpenAiStreamErrorMapper.Map(error).Message;

        Assert.DoesNotContain("SECRET_RESPONSE", message);

        Assert.DoesNotContain("UNRELATED", message);

        Assert.DoesNotContain("10024", message);

        Assert.NotEqual("context_length_exceeded", OpenAiStreamErrorMapper.Map(error).Code);
    }

    public static TheoryData<int, string, string> ContextIdentifierScalarCases
    {
        get
        {
            TheoryData<int, string, string> cases = new();

            foreach (int byteLimit in new[] { 64, 128 })
            {
                cases.Add(byteLimit, "a\U000E0001b\U000E0061c", "a_b_c");

                cases.Add(byteLimit, "a\U0001F680\U00010400b", "a\U0001F680\U00010400b");

                cases.Add(byteLimit, "bad\uD800x\uDC00", "bad\uFFFDx\uFFFD");

                cases.Add(byteLimit, new string('a', byteLimit - 4) + "\U0001F680tail", new string('a', byteLimit - 4) + "\U0001F680");

                cases.Add(byteLimit, new string('a', byteLimit - 3) + "\U0001F680tail", new string('a', byteLimit - 3));

                cases.Add(byteLimit, new string('a', byteLimit - 4) + "\U000E007Ftail", new string('a', byteLimit - 4) + "_");

                cases.Add(byteLimit, new string('a', byteLimit - 3) + "\uD800tail", new string('a', byteLimit - 3) + "\uFFFD");
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ContextIdentifierScalarCases))]
    public void ContextOverflowIdentifiers_SanitizeScalarsAndPreserveValidUnicodeWithinByteLimit(
        int byteLimit, string identifier, string expected)
    {
        ContextOverflowDetails overflow = new(
            byteLimit == 64 ? identifier : "provider",
            byteLimit == 128 ? identifier : "model",
            9000, 1024, 10024, 8192);

        Error error = new(ErrorCodes.Hub.ContextBudgetExceeded, overflow.ToPublicMessage()) { ContextOverflow = overflow };

        OpenAiErrorDetail mapped = OpenAiStreamErrorMapper.Map(error);

        Assert.Equal(error.Message, mapped.Message);

        string json = JsonSerializer.Serialize(mapped, ArcanumJsonContext.Default.OpenAiErrorDetail);

        using JsonDocument document = JsonDocument.Parse(json);

        string decodedMessage = document.RootElement.GetProperty("message").GetString()!;

        string field = byteLimit == 64 ? "provider" : "model";

        string prefix = field + " '";

        int start = decodedMessage.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;

        string actual = decodedMessage[start..decodedMessage.IndexOf("'", start, StringComparison.Ordinal)];

        Assert.Equal(expected, actual);

        UTF8Encoding strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        Assert.InRange(strictUtf8.GetByteCount(actual), 0, byteLimit);

        Assert.Equal(actual, strictUtf8.GetString(strictUtf8.GetBytes(actual)));

        foreach (Rune scalar in decodedMessage.EnumerateRunes())
        {
            Assert.NotEqual(System.Globalization.UnicodeCategory.Format, Rune.GetUnicodeCategory(scalar));

            Assert.NotEqual(System.Globalization.UnicodeCategory.Control, Rune.GetUnicodeCategory(scalar));
        }
    }

    [Fact]
    public void ContextOverflowIdentifiers_AreBoundedAndControlSanitized()
    {
        ContextOverflowDetails overflow = new("provider\n\u001b\u202e'" + new string('p', 300), "model\r\u2028\0" + new string('m', 300), 9000, 1024, 10024, 8192);

        string message = OpenAiStreamErrorMapper.Map(new Error(ErrorCodes.Hub.ContextBudgetExceeded, "SECRET_PROFILE_AND_RESPONSE") { ContextOverflow = overflow }).Message;

        Assert.Contains("provider____", message);

        Assert.Contains("model___", message);

        Assert.DoesNotContain(new string('p', 65), message);

        Assert.DoesNotContain(new string('m', 129), message);

        Assert.DoesNotContain("SECRET", message);

        Assert.DoesNotContain(message, character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format);
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

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_api_key", body.Error.Code);
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
    [InlineData(
        ErrorCodes.ClientTools.ModelUnsupported,
        "The selected model does not support the requested tool choice.",
        "unsupported_parameter")]
    [InlineData(
        ErrorCodes.ClientTools.ToolChoiceUnavailable,
        "The requested tool choice cannot be satisfied with the available tools.",
        "invalid_value")]
    public async Task PostChatCompletions_StreamingLateToolChoiceFailure_UsesSanitizedRequestError(
        string internalCode,
        string expectedMessage,
        string expectedOpenAiCode)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string canary = "CANARY_STREAM_MODEL_AND_PROVIDER_DETAILS";
        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ClientTools = true },
            },
        };
        factory.FakeIntelligence.NextFailure = new Error(internalCode, canary);

        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = """
            {
              "model": "mistral:latest",
              "stream": true,
              "messages": [
                { "role": "user", "content": "Use the weather tool." }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "parameters": { "type": "object" }
                  }
                }
              ],
              "tool_choice": "required"
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

        Assert.Equal(expectedMessage, errorChunk.Error?.Message);
        Assert.Equal("invalid_request_error", errorChunk.Error?.Type);
        Assert.Equal(expectedOpenAiCode, errorChunk.Error?.Code);
        Assert.Equal("tool_choice", errorChunk.Error?.Param);
        Assert.Equal("error", Assert.Single(errorChunk.Choices).FinishReason);
        Assert.Contains("data: [DONE]", sse, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, sse, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PostChatCompletions_StreamingProviderIoException_EmitsErrorFrameRatherThanTruncating()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        // A broken pipe on the PROVIDER socket, not the client one. The client is still reading, so
        // truncating the stream silently would hand it a partial answer with no error signal.
        factory.FakeIntelligence.NextStreamException = new IOException("provider stream failed");

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
        Assert.Equal("error", Assert.Single(errorChunk.Choices).FinishReason);
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

    [SkippableTheory]
    [InlineData("private-image-canary.png", false)]
    [InlineData("private-image-canary.png", true)]
    [InlineData("data:image/png,private-image-canary", false)]
    [InlineData("data:image/png,private-image-canary", true)]
    public async Task Invalid_image_urls_are_rejected_before_buffered_or_streaming_inference(
        string imageUrl,
        bool stream)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { Scrying = true },
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

        using HttpClient client = factory.CreateAuthenticatedClient();

        string urlJson = JsonSerializer.Serialize(imageUrl, ArcanumJsonContext.Default.String);

        string streamJson = stream ? "true" : "false";

        string payload = $$"""
            {
              "model": "vision-model",
              "stream": {{streamJson}},
              "messages": [{ "role": "user", "content": [
                { "type": "text", "text": "look" },
                { "type": "image_url", "image_url": { "url": {{urlJson}} } }
              ] }]
            }
            """;

        using HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_image_data", body.Error.Code);

        Assert.Contains("messages[0].content[1].image_url.url", body.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-image-canary", body.Error.Message, StringComparison.Ordinal);

        Assert.Null(factory.FakeIntelligence.LastRequest);
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
    public async Task GetModels_DeclaredNoTools_ReportsEffectiveCapability()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Providers =
                [
                    settings.Providers[0] with
                    {
                        Models = [new ModelEntry("mistral:latest", SupportsTools: false)],
                    },
                ],
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        HttpResponseMessage nativeResponse = await client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(HttpStatusCode.OK, nativeResponse.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiModelListResponse? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.OpenAiModelListResponse);

        OpenAiModel model = Assert.Single(body!.Data, m => m.Id == "mistral:latest");

        Assert.False(model.SupportsTools);

        ApiResponse<ModelInfoDto[]>? native = JsonSerializer.Deserialize(
            await nativeResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray);

        Assert.False(Assert.Single(native!.Data!).SupportsTools);
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

    /// <summary>
    /// An OpenAI client reads <c>error.code</c> and <c>error.type</c>, so a 401 on <c>/v1</c> is spoken in
    /// that shape, exactly as the rate limiter and the maintenance refusal already do.
    /// </summary>
    [SkippableTheory]
    [InlineData("GET", "/v1/models")]
    [InlineData("POST", "/v1/chat/completions")]
    public async Task Unauthenticated_v1_request_returns_openai_invalid_api_key_envelope(string method, string path)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        using HttpRequestMessage request = new(new HttpMethod(method), path);

        if (method == "POST")
        {
            request.Content = new StringContent("""{"model":"m","messages":[]}""", Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_request_error", body.Error.Type);

        Assert.Equal("invalid_api_key", body.Error.Code);

        Assert.Null(body.Error.Param);
    }

    [SkippableFact]
    public async Task Unauthenticated_api_request_keeps_the_arcanum_envelope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.Equal("Auth.Unauthorized", body?.Error?.Code);
    }

    [SkippableFact]
    public async Task GetModels_WithoutApiKey_Returns401()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_api_key", body.Error.Code);
    }

    [SkippableTheory]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task PostChatCompletions_NonJsonContentType_Returns415NotUnhandled(string? contentType)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "hello" }
              ]
            }
            """;

        using ByteArrayContent content = new(Encoding.UTF8.GetBytes(payload));

        content.Headers.ContentType = contentType is null ? null : new MediaTypeHeaderValue(contentType);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_request_error", body.Error.Type);

        Assert.Equal("unsupported_media_type", body.Error.Code);
    }

    [Fact]
    public async Task CreateRequestBodyReadErrorResult_OverSizeLimit_KeepsKestrels413InTheOpenAiEnvelope()
    {
        // Kestrel raises BadHttpRequestException(413) once WithLargeRequestBody's 16 MiB ceiling is
        // exceeded. It is not a JsonException, so it used to escape the handler and be answered as a
        // 500 api_error/inference_failed by ArcanumExceptionHandler.
        IResult result = OpenAiV1Endpoints.CreateRequestBodyReadErrorResult(
            StatusCodes.Status413PayloadTooLarge);

        (int statusCode, string json) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, statusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_request_error", body.Error.Type);

        Assert.Equal("payload_too_large", body.Error.Code);
    }

    [Fact]
    public async Task CreateRequestBodyReadErrorResult_OtherBadRequest_KeepsKestrelsStatusCode()
    {
        IResult result = OpenAiV1Endpoints.CreateRequestBodyReadErrorResult(StatusCodes.Status400BadRequest);

        (int statusCode, string json) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_request_error", body.Error.Type);

        Assert.Equal("invalid_request", body.Error.Code);
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        DefaultHttpContext context = new()
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

        MemoryStream buffer = new();

        context.Response.Body = buffer;

        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, Encoding.UTF8.GetString(buffer.ToArray()));
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
