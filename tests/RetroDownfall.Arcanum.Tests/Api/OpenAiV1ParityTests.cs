using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class OpenAiV1ParityTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public OpenAiV1ParityTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostChatCompletions_Buffered_SurfacesServerToolCalls()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextStreamToolCalls = null;

        _factory.FakeIntelligence.NextText = "done";

        _factory.FakeIntelligence.NextToolCalls =
        [
            new PromptToolCall("call-1", "get_time", "{}"),
        ];

        _factory.FakeIntelligence.NextFinishReason = "stop";

        HttpClient client = _factory.CreateAuthenticatedClient();

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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiChatResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiChatResponse);

        Assert.NotNull(body);

        OpenAiToolCall[]? toolCalls = body.Choices[0].Message.ToolCalls;

        Assert.NotNull(toolCalls);

        OpenAiToolCall toolCall = Assert.Single(toolCalls);

        Assert.StartsWith("call_", toolCall.Id, StringComparison.Ordinal);

        Assert.Equal("function", toolCall.Type);

        Assert.Equal("get_time", toolCall.Function.Name);

        Assert.Equal("{}", toolCall.Function.Arguments);

        Assert.Equal("stop", body.Choices[0].FinishReason);

    }

    [SkippableFact]
    public async Task PostChatCompletions_Buffered_MapsFinishReason()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextText = "truncated";

        _factory.FakeIntelligence.NextFinishReason = "length";

        HttpClient client = _factory.CreateAuthenticatedClient();

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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiChatResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiChatResponse);

        Assert.NotNull(body);

        Assert.Equal("length", body.Choices[0].FinishReason);

    }

    [SkippableFact]
    public async Task PostChatCompletions_PreInferenceFailure_ReturnsOpenAiEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = new Error("Hub.Model", "model resolution failed");

        HttpClient client = _factory.CreateAuthenticatedClient();

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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("model_not_found", body.Error.Code);

        Assert.Equal("api_error", body.Error.Type);

        _factory.FakeIntelligence.NextFailure = null;

    }

    [SkippableFact]
    public async Task PostChatCompletions_Buffered_PassesAuditContextWithRequestType()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextToolCalls = null;

        _factory.FakeIntelligence.NextText = "done";

        HttpClient client = _factory.CreateAuthenticatedClient();

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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        InferenceAuditContext? auditContext = _factory.FakeIntelligence.LastAuditContext;

        Assert.NotNull(auditContext);

        Assert.Equal("v1-completion", auditContext.RequestType);

    }

    [SkippableFact]
    public async Task PostChatCompletions_Streaming_PassesAuditContextWithRequestType()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextToolCalls = null;

        _factory.FakeIntelligence.NextStreamToolCalls = null;

        _factory.FakeIntelligence.NextText = "streamed";

        HttpClient client = _factory.CreateAuthenticatedClient();

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

        _ = await response.Content.ReadAsStringAsync();

        InferenceAuditContext? auditContext = _factory.FakeIntelligence.LastAuditContext;

        Assert.NotNull(auditContext);

        Assert.Equal("v1-completion", auditContext.RequestType);

    }

    [SkippableFact]
    public async Task PostChatCompletions_Streaming_EmitsToolCallDeltasWithChunkedArguments()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextToolCalls = null;

        _factory.FakeIntelligence.NextText = "final answer";

        _factory.FakeIntelligence.NextFinishReason = "stop";

        string longArguments = JsonSerializer.Serialize(new
        {
            command = "ls -la",
            note = new string('x', 80),
        });

        _factory.FakeIntelligence.NextStreamToolCalls =
        [
            new IntelligenceToolCallEvent("call-abc", "execute_command", longArguments, Index: 0),
        ];

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "stream": true,
              "messages": [
                { "role": "user", "content": "run ls" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string sseBody = await response.Content.ReadAsStringAsync();

        // Server-executed tool RESULTS are never surfaced on /v1 — only the call itself.
        Assert.DoesNotContain("toolResult", sseBody, StringComparison.OrdinalIgnoreCase);

        List<OpenAiChatChunk> chunks = ParseSseChunks(sseBody);

        List<OpenAiStreamToolCall> toolCallDeltas = chunks
            .SelectMany(static c => c.Choices)
            .Select(static c => c.Delta.ToolCalls)
            .Where(static tc => tc is { Length: > 0 })
            .SelectMany(static tc => tc!)
            .ToList();

        Assert.True(toolCallDeltas.Count > 1, "Expected the arguments string to be split across multiple deltas, not sent as one JSON blob.");

        // First delta carries id/type/name plus the first argument fragment.
        OpenAiStreamToolCall first = toolCallDeltas[0];

        Assert.StartsWith("call_", first.Id, StringComparison.Ordinal);

        Assert.Equal("function", first.Type);

        Assert.Equal("execute_command", first.Function?.Name);

        // Every delta (including the first) must carry `index`, and it must be stable across the
        // whole call.
        Assert.All(toolCallDeltas, d => Assert.Equal(0, d.Index));

        // Subsequent deltas carry only `function.arguments` — no id/type/name repeated.
        Assert.All(toolCallDeltas.Skip(1), d =>
        {

            Assert.Null(d.Id);

            Assert.Null(d.Type);

            Assert.Null(d.Function?.Name);

        });

        string reassembledArguments = string.Concat(toolCallDeltas.Select(static d => d.Function?.Arguments ?? string.Empty));

        Assert.Equal(longArguments, reassembledArguments);

        _factory.FakeIntelligence.NextStreamToolCalls = null;

    }

    [SkippableFact]
    public async Task PostChatCompletions_ReplaysClientSuppliedToolCallTranscript()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextStreamToolCalls = null;

        _factory.FakeIntelligence.NextToolCalls = null;

        _factory.FakeIntelligence.NextFinishReason = null;

        _factory.FakeIntelligence.NextText = "The command completed successfully.";

        HttpClient client = _factory.CreateAuthenticatedClient();

        // Simulates a client replaying a full transcript: the assistant's prior tool_calls message
        // (exactly the shape PostChatCompletions_Buffered_SurfacesServerToolCalls proved Arcanum
        // returns) plus the matching tool result, then a new user follow-up.
        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "system", "content": "You are a helpful assistant." },
                { "role": "user", "content": "What time is it?" },
                {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [
                    {
                      "id": "call_abc123def456abc123def456",
                      "type": "function",
                      "function": { "name": "get_time", "arguments": "{}" }
                    }
                  ]
                },
                { "role": "tool", "tool_call_id": "call_abc123def456abc123def456", "content": "2026-07-06T00:00:00Z" },
                { "role": "user", "content": "Thanks!" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        PingRequest? lastRequest = _factory.FakeIntelligence.LastRequest;

        Assert.NotNull(lastRequest);

        List<CoreChatMessage>? mapped = lastRequest.StatelessMessages;

        Assert.NotNull(mapped);

        CoreChatMessage assistantMessage = Assert.Single(
            mapped,
            static m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(assistantMessage.ToolCalls);

        CoreToolCall toolCall = Assert.Single(assistantMessage.ToolCalls);

        Assert.Equal("call_abc123def456abc123def456", toolCall.Id);

        Assert.Equal("get_time", toolCall.Name);

        Assert.Equal("{}", toolCall.ArgumentsJson);

        CoreChatMessage toolMessage = Assert.Single(
            mapped,
            static m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("call_abc123def456abc123def456", toolMessage.ToolCallId);

        Assert.Equal("2026-07-06T00:00:00Z", toolMessage.Content);

    }

    /// <summary>
    /// Phase 2 performance verification (plan §"Performance verification (Phase 2)") — a bounded
    /// concurrency smoke check standing in for a full sustained load test (not practical against an
    /// in-memory <c>TestServer</c>/<see cref="FakeIntelligenceProvider"/> host): confirms the new
    /// tool-call SSE bridge does not serialize, deadlock, or leak shared state across concurrent
    /// streaming requests.
    /// </summary>
    [SkippableFact]
    public async Task PostChatCompletions_Streaming_HandlesConcurrentRequestsWithToolCalls()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextToolCalls = null;

        _factory.FakeIntelligence.NextText = "concurrent answer";

        _factory.FakeIntelligence.NextFinishReason = "stop";

        _factory.FakeIntelligence.NextStreamToolCalls =
        [
            new IntelligenceToolCallEvent("call-x", "get_local_system_time", "{}", Index: 0),
        ];

        const int concurrency = 50;

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "stream": true,
              "messages": [ { "role": "user", "content": "what time is it" } ]
            }
            """;

        async Task<bool> RunOneAsync()
        {

            using HttpResponseMessage response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            if (response.StatusCode != HttpStatusCode.OK)
            {

                return false;

            }

            string body = await response.Content.ReadAsStringAsync();

            List<OpenAiChatChunk> chunks = ParseSseChunks(body);

            bool sawToolCall = chunks.Any(static c => c.Choices.Any(static ch => ch.Delta.ToolCalls is { Length: > 0 }));

            bool sawDone = body.Contains("[DONE]", StringComparison.Ordinal);

            return sawToolCall && sawDone;

        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        Task<bool>[] tasks = [.. Enumerable.Range(0, concurrency).Select(_ => RunOneAsync())];

        bool[] results = await Task.WhenAll(tasks);

        stopwatch.Stop();

        Assert.All(results, Assert.True);

        // Sanity budget, not a hard perf SLA — just confirms concurrent SSE tool-call streaming
        // doesn't pathologically serialize or hang.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Concurrent streaming took {stopwatch.Elapsed}, expected well under 30s for {concurrency} in-memory TestServer requests.");

        _factory.FakeIntelligence.NextStreamToolCalls = null;

    }

    [SkippableFact]
    public async Task PostChatCompletions_WithIdempotencyKey_SecondRequestReplaysWithoutReExecuting()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextToolCalls = null;

        _factory.FakeIntelligence.NextStreamToolCalls = null;

        _factory.FakeIntelligence.NextFinishReason = "stop";

        _factory.FakeIntelligence.NextText = "first-answer";

        int before = _factory.FakeIntelligence.ExecutePromptCallCount;

        string key = $"test-key-{Guid.NewGuid():N}";

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "idempotent chat completion" }
              ]
            }
            """;

        async Task<HttpResponseMessage> SendAsync()
        {

            HttpRequestMessage req = new(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            req.Headers.Add("Idempotency-Key", key);

            return await client.SendAsync(req);

        }

        HttpResponseMessage firstResponse = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        string firstBody = await firstResponse.Content.ReadAsStringAsync();

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

        _factory.FakeIntelligence.NextText = "second-answer-should-never-be-seen";

        HttpResponseMessage secondResponse = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        string secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, secondBody);

        Assert.Equal(before + 1, _factory.FakeIntelligence.ExecutePromptCallCount);

    }

    private static List<OpenAiChatChunk> ParseSseChunks(string sseBody)
    {

        List<OpenAiChatChunk> chunks = [];

        foreach (string rawLine in sseBody.Split('\n'))
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

            OpenAiChatChunk? chunk = JsonSerializer.Deserialize(payload, ArcanumJsonContext.Default.OpenAiChatChunk);

            if (chunk is not null)
            {

                chunks.Add(chunk);

            }

        }

        return chunks;

    }

}
