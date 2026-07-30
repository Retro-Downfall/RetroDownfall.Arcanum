using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReasoningProjectionEndpointCollection
{
    public const string Name = "ReasoningProjectionEndpointIsolation";
}

[Collection(ReasoningProjectionEndpointCollection.Name)]
public sealed class ReasoningProjectionEndpointTests
{
    [SkippableFact]
    public async Task NativeBuffered_ProjectsReasoningSeparatelyFromAnswer()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        ConfigureReasoningModel(factory);
        ReasoningContentSegment reasoning = new(
            "buffered summary",
            ReasoningOutputMode.Summary);
        PropertyInfo? nextReasoning = typeof(FakeIntelligenceProvider)
            .GetProperty("NextReasoning");
        Assert.NotNull(nextReasoning);
        nextReasoning.SetValue(
            factory.FakeIntelligence,
            new List<ReasoningContentSegment> { reasoning });
        factory.FakeIntelligence.NextText = "answer only";

        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = JsonSerializer.Serialize(
            new PingRequest(Prompt: "ping"),
            ArcanumJsonContext.Default.PingRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/ping",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        ApiResponse<PromptResponseDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

        Assert.NotNull(body?.Data);
        Assert.Equal("answer only", body!.Data!.Text);
        Assert.Equal([reasoning], body.Data.Reasoning);
        Assert.DoesNotContain("buffered summary", body.Data.Text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task NativeNdjson_PreservesMixedReasoningAndAnswerOrdering()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        ConfigureReasoningModel(factory);
        IReadOnlyList<IntelligenceEvent> sourceFrames =
        [
            new IntelligenceEvent(
                IntelligenceEventType.Reasoning,
                "summary first",
                Reasoning: new ReasoningContentSegment(
                    "summary first",
                    ReasoningOutputMode.Summary)),
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "answer"),
            new IntelligenceEvent(
                IntelligenceEventType.Reasoning,
                "full last",
                Reasoning: new ReasoningContentSegment(
                    "full last",
                    ReasoningOutputMode.Full)),
            new IntelligenceEvent(
                IntelligenceEventType.Result,
                "Complete",
                "0",
                FinishReason: "stop"),
        ];
        PropertyInfo? nextStreamEvents = typeof(FakeIntelligenceProvider)
            .GetProperty("NextStreamEvents");
        Assert.NotNull(nextStreamEvents);
        nextStreamEvents.SetValue(factory.FakeIntelligence, sourceFrames);

        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = JsonSerializer.Serialize(
            new PingRequest(Prompt: "ping"),
            ArcanumJsonContext.Default.PingRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/ping-stream",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string ndjson = await response.Content.ReadAsStringAsync();
        List<IntelligenceEvent> frames = ndjson
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize(
                line,
                ArcanumJsonContext.Default.IntelligenceEvent))
            .OfType<IntelligenceEvent>()
            .ToList();

        Assert.Equal(
            [
                IntelligenceEventType.Reasoning,
                IntelligenceEventType.Token,
                IntelligenceEventType.Reasoning,
                IntelligenceEventType.Result,
            ],
            frames.Select(static frame => frame.Type));
        Assert.Equal("summary first", frames[0].Reasoning?.Text);
        Assert.Equal(ReasoningOutputMode.Summary, frames[0].Reasoning?.Output);
        Assert.Null(frames[0].Data);
        Assert.Equal("answer", frames[1].Data);
        Assert.Equal("full last", frames[2].Reasoning?.Text);
        Assert.Equal(ReasoningOutputMode.Full, frames[2].Reasoning?.Output);
        Assert.Null(frames[2].Data);
        Assert.DoesNotContain("protected", ndjson, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task OpenAiBuffered_ProjectsAdditiveReasoningFieldsLegacyClientsCanIgnore()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        ConfigureReasoningModel(factory);
        factory.FakeIntelligence.NextText = "answer only";
        factory.FakeIntelligence.NextReasoning =
        [
            new ReasoningContentSegment("summary one", ReasoningOutputMode.Summary),
            new ReasoningContentSegment(" summary two", ReasoningOutputMode.Summary),
            new ReasoningContentSegment("full reasoning", ReasoningOutputMode.Full),
        ];

        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = """
            {
              "model": "reasoner",
              "messages": [
                { "role": "user", "content": "solve" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        OpenAiChatResponse? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.OpenAiChatResponse);

        Assert.NotNull(body);
        OpenAiChatAssistantMessage message = Assert.Single(body!.Choices).Message;
        Assert.Equal("answer only", message.Content);
        Assert.Equal("summary one summary two", message.ReasoningSummary);
        Assert.Equal("full reasoning", message.ReasoningContent);
        Assert.DoesNotContain("summary", message.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("full reasoning", message.Content, StringComparison.Ordinal);

        using JsonDocument legacyClient = JsonDocument.Parse(json);
        JsonElement legacyMessage = legacyClient.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");
        Assert.Equal("answer only", legacyMessage.GetProperty("content").GetString());
    }

    [SkippableFact]
    public async Task OpenAiSse_ProjectsMixedReasoningFieldsInOrderWithoutAnswerContamination()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        ConfigureReasoningModel(factory);
        factory.FakeIntelligence.NextStreamEvents =
        [
            new IntelligenceEvent(
                IntelligenceEventType.Reasoning,
                "summary",
                Reasoning: new ReasoningContentSegment(
                    "summary",
                    ReasoningOutputMode.Summary)),
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "answer "),
            new IntelligenceEvent(
                IntelligenceEventType.Reasoning,
                "full",
                Reasoning: new ReasoningContentSegment(
                    "full",
                    ReasoningOutputMode.Full)),
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "only"),
            new IntelligenceEvent(
                IntelligenceEventType.Result,
                "Complete",
                "0",
                FinishReason: "stop"),
        ];

        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = """
            {
              "model": "reasoner",
              "stream": true,
              "messages": [
                { "role": "user", "content": "solve" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string sse = await response.Content.ReadAsStringAsync();
        List<OpenAiDelta> projected = ParseSseChunks(sse)
            .SelectMany(static chunk => chunk.Choices)
            .Select(static choice => choice.Delta)
            .Where(static delta => delta.Content is not null
                || delta.ReasoningContent is not null
                || delta.ReasoningSummary is not null)
            .ToList();

        Assert.Collection(
            projected,
            delta =>
            {
                Assert.Null(delta.Content);
                Assert.Equal("summary", delta.ReasoningSummary);
                Assert.Null(delta.ReasoningContent);
            },
            delta =>
            {
                Assert.Equal("answer ", delta.Content);
                Assert.Null(delta.ReasoningSummary);
                Assert.Null(delta.ReasoningContent);
            },
            delta =>
            {
                Assert.Null(delta.Content);
                Assert.Null(delta.ReasoningSummary);
                Assert.Equal("full", delta.ReasoningContent);
            },
            delta =>
            {
                Assert.Equal("only", delta.Content);
                Assert.Null(delta.ReasoningSummary);
                Assert.Null(delta.ReasoningContent);
            });
        Assert.Equal(
            "answer only",
            string.Concat(projected.Select(static delta => delta.Content)));
        Assert.Equal(
            2,
            projected.Count(static delta => delta.ReasoningContent is not null
                || delta.ReasoningSummary is not null));
        Assert.Contains("\"reasoning_summary\":\"summary\"", sse, StringComparison.Ordinal);
        Assert.Contains("\"reasoning_content\":\"full\"", sse, StringComparison.Ordinal);

        System.Threading.Channels.Channel<OpenAiChatChunk> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OpenAiChatChunk>();
        OpenAiSseProjection semanticProjection = new(
            channel.Writer,
            "chatcmpl-parity",
            "reasoner",
            createdUnixSeconds: 1);
        TurnEventEmitter emitter = new(Guid.NewGuid());
        List<OpenAiChatChunk> semanticChunks =
        [
            .. semanticProjection.Map(new ReasoningDelta(
                emitter.NextCorrelation(),
                new ReasoningContentSegment("summary", ReasoningOutputMode.Summary))),
            .. semanticProjection.Map(new TextDelta(emitter.NextCorrelation(), "answer ")),
            .. semanticProjection.Map(new ReasoningDelta(
                emitter.NextCorrelation(),
                new ReasoningContentSegment("full", ReasoningOutputMode.Full))),
            .. semanticProjection.Map(new TextDelta(emitter.NextCorrelation(), "only")),
        ];
        List<OpenAiDelta> semanticDeltas = semanticChunks
            .SelectMany(static chunk => chunk.Choices)
            .Select(static choice => choice.Delta)
            .ToList();

        Assert.Equal(semanticDeltas, projected);
    }

    [SkippableTheory]
    [InlineData(
        ErrorCodes.Validation.UnsupportedReasoningControl,
        "unsupported_reasoning_control")]
    [InlineData(
        ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive,
        "invalid_reasoning_options")]
    public async Task OpenAiBuffered_ReasoningValidationUsesSharedTypedErrorMapping(
        string internalCode,
        string expectedOpenAiCode)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        ConfigureReasoningModel(factory);
        Error failure = new(
            internalCode,
            "unsafe implementation detail");
        factory.FakeIntelligence.NextFailure = failure;
        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = """
            {
              "model": "reasoner",
              "messages": [
                { "role": "user", "content": "solve" }
              ]
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
        Assert.Equal(OpenAiStreamErrorMapper.Map(failure), body.Error);
        Assert.Equal("invalid_request_error", body.Error.Type);
        Assert.Equal(expectedOpenAiCode, body.Error.Code);
        Assert.Equal("reasoning", body.Error.Param);
        Assert.DoesNotContain("unsafe implementation detail", json, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData(ErrorCodes.Guardrails.Blocked, "api_error", "content_filter")]
    [InlineData(ErrorCodes.StructuredOutput.ValidationFailed, "api_error", "validation_failed")]
    [InlineData(
        ErrorCodes.Validation.UnsupportedReasoningControl,
        "invalid_request_error",
        "unsupported_reasoning_control")]
    public async Task OpenAiSse_EndpointAndSemanticProjectionUseSameTypedErrorChunk(
        string internalCode,
        string expectedOpenAiType,
        string expectedOpenAiCode)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        ConfigureReasoningModel(factory);
        Error failure = new(
            internalCode,
            "unsafe implementation detail");
        factory.FakeIntelligence.NextFailure = failure;
        HttpClient client = factory.CreateAuthenticatedClient();
        string payload = """
            {
              "model": "reasoner",
              "stream": true,
              "messages": [
                { "role": "user", "content": "solve" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string sse = await response.Content.ReadAsStringAsync();
        OpenAiChatChunk endpointChunk = Assert.Single(
            ParseSseChunks(sse),
            static chunk => chunk.Error is not null);

        System.Threading.Channels.Channel<OpenAiChatChunk> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OpenAiChatChunk>();
        OpenAiSseProjection projection = new(
            channel.Writer,
            endpointChunk.Id,
            endpointChunk.Model,
            endpointChunk.Created);
        TurnEventEmitter emitter = new(Guid.NewGuid());
        OpenAiChatChunk semanticChunk = Assert.Single(projection.Map(new RunFailed(
            emitter.NextCorrelation(),
            failure,
            TurnTerminationReason.ProviderFailure,
            Usage: null,
            Warnings: [],
            Interrupted: false,
            PartialText: null)));

        Assert.Equal(semanticChunk.Error, endpointChunk.Error);
        Assert.Equal(expectedOpenAiType, endpointChunk.Error?.Type);
        Assert.Equal(expectedOpenAiCode, endpointChunk.Error?.Code);
        Assert.Equal("error", Assert.Single(endpointChunk.Choices).FinishReason);
        Assert.DoesNotContain("unsafe implementation detail", sse, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ParseSseChunks(sse),
            static chunk => chunk.Choices.Any(
                static choice => choice.FinishReason == "stop"));
    }

    private static void ConfigureReasoningModel(ArcanumWebApplicationFactory factory)
    {
        factory.SettingsOverride = settings => settings with
        {
            DefaultModel = "reasoner",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "reasoning-test",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://example.test/v1",
                    Models =
                    [
                        new ModelEntry(
                            "reasoner",
                            WireDialect: ReasoningWireDialect.Standard),
                    ],
                },
            ],
        };
    }

    private static List<OpenAiChatChunk> ParseSseChunks(string sse)
    {
        List<OpenAiChatChunk> chunks = [];

        foreach (string rawLine in sse.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line["data: ".Length..];
            if (payload == "[DONE]")
            {
                continue;
            }

            OpenAiChatChunk? chunk = JsonSerializer.Deserialize(
                payload,
                ArcanumJsonContext.Default.OpenAiChatChunk);
            if (chunk is not null)
            {
                chunks.Add(chunk);
            }
        }

        return chunks;
    }
}
