using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Api.Diagnostics;

internal interface IHostDefectProbe
{
    Task<HostDefectProbeObservation> ObserveAsync(bool streaming, CancellationToken cancellationToken);
}

internal readonly record struct HostDefectProbeObservation(HostDefectDisposition Disposition, int ProviderRequests);

internal sealed class OllamaContinuationDefectProbe : IHostDefectProbe
{
    internal const string Contract = "ollama-tool-continuation-v1";

    internal const string Provider = "ollama-fixture";

    internal const string Model = "qwen-fixture";

    private const string Reasoning = "SYNTHETIC_REASONING_MARKER";

    private const string ToolId = "fixture-tool-call";

    private const string ToolName = "fixture_observation";

    private const string ToolResult = "synthetic tool result";

    public async Task<HostDefectProbeObservation> ObserveAsync(bool streaming, CancellationToken cancellationToken)
    {
        using FixtureTransport transport = new(streaming);

        ArcanumSettings settings = new()
        {
            DefaultModel = Model,
            Providers = [new() { Name = Provider, Type = AiProviderKind.OpenAICompatible, Models = [Model],
                Endpoint = "http://localhost:11434/v1", LocalRuntimeId = Provider }],
            LocalRuntimes = [new() { Id = Provider, Adapter = "ollama", ManagementEndpoint = "http://localhost:11434" }],
        };

        ChatClientFactory factory = new(transport, new FixtureOptions(settings), new FixtureCredentials());

        try
        {
            using var lease = await factory.ResolveClientAsync(null, cancellationToken).ConfigureAwait(false);

            List<ChatMessage> messages = [new(ChatRole.User, "Observe the synthetic tool result.")];

            ChatResponse first = await ReadReplyAsync(lease.ChatClient, messages, streaming, cancellationToken).ConfigureAwait(false);

            List<FunctionCallContent> calls = ToolExecutionPipeline.CollectActionableFunctionCalls(first);

            if (calls is not [FunctionCallContent { CallId: ToolId, Name: ToolName } call])
                return new(HostDefectDisposition.Inconclusive, transport.RequestCount);

            TextReasoningContent[] reasoning = first.Messages.SelectMany(message => message.Contents)
                .OfType<TextReasoningContent>().ToArray();

            ToolExecutionPipeline.AppendToolExchangeToMessages(messages, call, ToolId, ToolResult, reasoning, first.Text);

            _ = await ReadReplyAsync(lease.ChatClient, messages, streaming, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            // A failure after the serialized request was observed does not erase that observation.
        }

        HostDefectDisposition disposition = transport.RequestCount == 2 ? transport.ContinuationPresent switch
        {
            true => HostDefectDisposition.ContractSatisfied,
            false => HostDefectDisposition.ConfirmedHostDefect,
            null => HostDefectDisposition.Inconclusive,
        } : HostDefectDisposition.Inconclusive;

        return new(disposition, transport.RequestCount);
    }

    private static async Task<ChatResponse> ReadReplyAsync(IChatClient client, List<ChatMessage> messages,
        bool streaming, CancellationToken cancellationToken)
    {
        if (!streaming)
            return await client.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);

        List<ChatResponseUpdate> updates = [];

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false))
            updates.Add(update);

        return updates.ToChatResponse();
    }

    private sealed class FixtureOptions(ArcanumSettings settings) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue => settings;

        public ArcanumSettings Get(string? name) => settings;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }

    private sealed class FixtureCredentials : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FixtureTransport(bool streaming) : HttpMessageHandler, IHttpClientFactory
    {
        internal int RequestCount { get; private set; }

        internal bool? ContinuationPresent { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (RequestCount == 2)
            {
                using JsonDocument wire = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                ContinuationPresent = CheckContinuation(wire.RootElement);
            }

            byte[] body = Encoding.UTF8.GetBytes(streaming ? StreamReply() : BufferedReply());

            // Provider response bodies have forward-only streams. Model that through the SDK's streaming reader.
            StreamContent content = new(PipeReader.Create(new MemoryStream(body)).AsStream());
            content.Headers.ContentType = new(streaming ? "text/event-stream" : "application/json") { CharSet = "utf-8" };
            content.Headers.ContentLength = body.Length;

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private static bool CheckContinuation(JsonElement wire)
        {
            if (Field(wire, "model") != Model || !wire.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array) return false;

            JsonElement[] items = [.. messages.EnumerateArray()];
            int assistant = Array.FindIndex(items, static item => Field(item, "role") == "assistant");

            if (assistant < 0 || assistant == items.Length - 1) return false;

            JsonElement message = items[assistant];
            JsonElement result = items[assistant + 1];

            return Field(message, "reasoning") == Reasoning
                && Field(message, "content") == "fixture assistant"
                && message.TryGetProperty("tool_calls", out var calls)
                && calls.ValueKind == JsonValueKind.Array
                && calls.GetArrayLength() == 1
                && Field(calls[0], "id") == ToolId
                && calls[0].TryGetProperty("function", out var function)
                && Field(function, "name") == ToolName
                && ArgumentsMatch(Field(function, "arguments"))
                && Field(result, "role") == "tool"
                && Field(result, "tool_call_id") == ToolId
                && Field(result, "content") == ToolResult;
        }

        private static bool ArgumentsMatch(string? arguments)
        {
            if (arguments is null) return false;

            try
            {
                using JsonDocument document = JsonDocument.Parse(arguments);

                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("sample", out JsonElement sample)
                    && sample.ValueKind == JsonValueKind.Number && sample.TryGetInt32(out int value) && value == 42
                    && document.RootElement.EnumerateObject().Count() == 1;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string? Field(JsonElement value, string key) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
                ? field.GetString()
                : null;

        private string BufferedReply() => RequestCount == 1
            ? """{"id":"fixture-first","object":"chat.completion","created":0,"model":"qwen-fixture","choices":[{"index":0,"message":{"role":"assistant","content":"fixture assistant","reasoning":"SYNTHETIC_REASONING_MARKER","tool_calls":[{"id":"fixture-tool-call","type":"function","function":{"name":"fixture_observation","arguments":"{\"sample\":42}"}}]},"finish_reason":"tool_calls"}]}"""
            : """{"id":"fixture-final","object":"chat.completion","created":0,"model":"qwen-fixture","choices":[{"index":0,"message":{"role":"assistant","content":"fixture complete"},"finish_reason":"stop"}]}""";

        private string StreamReply()
        {
            string content = RequestCount == 1
                ? Chunk("""{"role":"assistant","content":"fixture assistant","reasoning":"SYNTHETIC_REASONING_MARKER","tool_calls":[{"index":0,"id":"fixture-tool-call","type":"function","function":{"name":"fixture_observation","arguments":"{\"sample\":42}"}}]}""", "null")
                    + Chunk("{}", "\"tool_calls\"")
                : Chunk("""{"role":"assistant","content":"fixture complete"}""", "\"stop\"");

            return content + "data: [DONE]\n\n";
        }

        private static string Chunk(string delta, string finish) =>
            $$$"""data: {"id":"fixture","object":"chat.completion.chunk","created":0,"model":"qwen-fixture","choices":[{"index":0,"delta":{{{delta}}},"finish_reason":{{{finish}}}}]}""" + "\n\n";
    }
}
