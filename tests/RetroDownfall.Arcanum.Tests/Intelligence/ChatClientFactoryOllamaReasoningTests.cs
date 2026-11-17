using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ChatClientFactoryOllamaReasoningTests
{
    private const string Reasoning = "SYNTHETIC_REASONING_MARKER";

    private const string Visible = "SYNTHETIC_VISIBLE_REPLY";

    private const string ToolResult = "SYNTHETIC_TOOL_RESULT";

    [Theory]
    [InlineData(false, "reasoning")]
    [InlineData(true, "reasoning")]
    [InlineData(false, "reasoning_content")]
    [InlineData(true, "reasoning_content")]
    [InlineData(false, "both")]
    [InlineData(true, "both")]
    [InlineData(false, "conflict")]
    [InlineData(true, "conflict")]
    public async Task Explicit_Ollama_extracts_provider_reasoning_once_without_changing_visible_text_or_tool_calls(bool streaming, string dialect)
    {
        using Capture transport = new(streaming, dialect);

        using ChatClientLease lease = await CreateFactory(transport, local: true).ResolveClientAsync(null, CancellationToken.None);

        ChatResponse response = await RespondAsync(lease.ChatClient, [new(ChatRole.User, "synthetic request")], streaming);

        Assert.Equal(Reasoning, string.Concat(response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>().Select(content => content.Text)));

        Assert.Equal(Visible, response.Text);

        FunctionCallContent call = Assert.Single(ToolExecutionPipeline.CollectActionableFunctionCalls(response));

        Assert.Equal("call-synthetic", call.CallId);

        Assert.Equal("synthetic_tool", call.Name);

        using JsonDocument arguments = JsonDocument.Parse(ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(call));

        Assert.Equal("notes.txt", arguments.RootElement.GetProperty("path").GetString());

        Assert.Equal(7, arguments.RootElement.GetProperty("line").GetInt32());

        Assert.Single(transport.Bodies);
    }

    [Theory]
    [InlineData(false, "reasoning")]
    [InlineData(true, "reasoning")]
    [InlineData(false, "reasoning_content")]
    [InlineData(true, "reasoning_content")]
    public async Task Tool_pipeline_replays_provider_thinking_in_the_next_Ollama_wire_request(bool streaming, string dialect)
    {
        using Capture transport = new(streaming, dialect);

        using ChatClientLease lease = await CreateFactory(transport, local: true).ResolveClientAsync(null, CancellationToken.None);

        List<ChatMessage> messages = [new(ChatRole.User, "synthetic request")];

        ChatResponse first = await RespondAsync(lease.ChatClient, messages, streaming);

        Assert.Equal(Visible, first.Text);

        FunctionCallContent call = Assert.Single(ToolExecutionPipeline.CollectActionableFunctionCalls(first));

        ToolExecutionPipeline pipeline = ToolExecutionPipelineTests.CreatePipeline();

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline.ProcessSingleToolCallAsync(
            call,
            new PingRequest("synthetic request"),
            new ChatOptions { Tools = [new SyntheticFunction()] },
            activeSpell: null,
            sessionId: null,
            turnContext: new ToolExecutionPipeline.TurnContext(),
            suppressInvocationFailures: false,
            cancellationToken: CancellationToken.None);

        Assert.False(processed.Failed);

        Assert.False(processed.Denied);

        Assert.Equal(ToolResult, processed.ResultText);

        TextReasoningContent[] thinking = first.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>().ToArray();

        ToolExecutionPipeline.AppendToolExchangeToMessages(messages, call, processed.CallId, processed.ResultText, thinking, first.Text);

        ChatResponse second = await RespondAsync(lease.ChatClient, messages, streaming);

        Assert.Equal("SYNTHETIC_FINAL", second.Text);

        Assert.Equal(2, transport.Bodies.Count);

        using JsonDocument request = JsonDocument.Parse(transport.Bodies[1]);

        JsonElement wireMessages = request.RootElement.GetProperty("messages");

        JsonElement assistant = Assert.Single(wireMessages.EnumerateArray(), message => message.GetProperty("role").GetString() == "assistant");

        Assert.Equal(Reasoning, assistant.GetProperty("reasoning").GetString());

        Assert.Equal(Visible, assistant.GetProperty("content").GetString());

        Assert.False(assistant.TryGetProperty("reasoning_content", out _));

        JsonElement wireCall = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());

        Assert.Equal("call-synthetic", wireCall.GetProperty("id").GetString());

        Assert.Equal("synthetic_tool", wireCall.GetProperty("function").GetProperty("name").GetString());

        using JsonDocument arguments = JsonDocument.Parse(wireCall.GetProperty("function").GetProperty("arguments").GetString()!);

        Assert.Equal("notes.txt", arguments.RootElement.GetProperty("path").GetString());

        Assert.Equal(7, arguments.RootElement.GetProperty("line").GetInt32());

        JsonElement result = Assert.Single(wireMessages.EnumerateArray(), message => message.GetProperty("role").GetString() == "tool");

        Assert.Equal("call-synthetic", result.GetProperty("tool_call_id").GetString());

        Assert.Equal(ToolResult, result.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Assistant_visible_text_and_tool_exchange_survive_serialization_while_reasoning_is_scoped_to_explicit_Ollama(bool streaming, bool local)
    {
        using Capture transport = new(streaming, "reasoning_content");

        using ChatClientLease lease = await CreateFactory(transport, local).ResolveClientAsync(null, CancellationToken.None);

        List<ChatMessage> messages =
        [
            new(ChatRole.User, "synthetic request"),
            new(ChatRole.Assistant,
            [
                new TextReasoningContent(Reasoning),
                new TextContent(Visible),
                new FunctionCallContent("call-visible", "synthetic_tool", new Dictionary<string, object?> { ["path"] = "notes.txt" }),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call-visible", ToolResult)]),
        ];

        ChatMessage originalAssistant = messages[1];

        AIContent[] originalContents = originalAssistant.Contents.ToArray();

        await RespondAsync(lease.ChatClient, messages, streaming);

        Assert.Same(originalAssistant, messages[1]);

        Assert.Null(originalAssistant.RawRepresentation);

        Assert.Equal(originalContents.Length, originalAssistant.Contents.Count);

        for (int index = 0; index < originalContents.Length; index++) Assert.Same(originalContents[index], originalAssistant.Contents[index]);

        Assert.Equal(Reasoning, Assert.IsType<TextReasoningContent>(originalAssistant.Contents[0]).Text);

        Assert.Equal(Visible, Assert.IsType<TextContent>(originalAssistant.Contents[1]).Text);

        using JsonDocument request = JsonDocument.Parse(Assert.Single(transport.Bodies));

        JsonElement assistant = request.RootElement.GetProperty("messages")[1];

        Assert.Equal(Visible, assistant.GetProperty("content").GetString());

        Assert.Equal(local, assistant.TryGetProperty("reasoning", out JsonElement thinking));

        if (local) Assert.Equal(Reasoning, thinking.GetString());

        Assert.False(assistant.TryGetProperty("reasoning_content", out _));

        Assert.Equal("call-visible", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());

        Assert.Equal(ToolResult, request.RootElement.GetProperty("messages")[2].GetProperty("content").GetString());

        if (!local) Assert.DoesNotContain(Reasoning, transport.Bodies[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_provider_with_Ollama_like_name_and_endpoint_does_not_extract_Ollama_only_reasoning(bool streaming)
    {
        using Capture transport = new(streaming, "reasoning");

        using ChatClientLease lease = await CreateFactory(transport, local: false).ResolveClientAsync(null, CancellationToken.None);

        ChatResponse response = await RespondAsync(lease.ChatClient, [new(ChatRole.User, "synthetic request")], streaming);

        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>());

        Assert.Equal(Visible, response.Text);

        Assert.Single(ToolExecutionPipeline.CollectActionableFunctionCalls(response));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ollama_request_without_provider_thinking_does_not_invent_reasoning(bool streaming)
    {
        using Capture transport = new(streaming, "reasoning");

        using ChatClientLease lease = await CreateFactory(transport, local: true).ResolveClientAsync(null, CancellationToken.None);

        await RespondAsync(lease.ChatClient, [new(ChatRole.User, "synthetic request"), new(ChatRole.Assistant, Visible)], streaming);

        using JsonDocument request = JsonDocument.Parse(Assert.Single(transport.Bodies));

        Assert.False(request.RootElement.GetProperty("messages")[1].TryGetProperty("reasoning", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reasoning_replay_preserves_raw_assistant_fields_without_mutating_the_callers_SDK_message(bool streaming)
    {
        using Capture transport = new(streaming, "reasoning_content");

        using ChatClientLease lease = await CreateFactory(transport, local: true).ResolveClientAsync(null, CancellationToken.None);

        OpenAI.Chat.AssistantChatMessage raw = new(Visible);

#pragma warning disable SCME0001 // The pinned SDK exposes its JSON extension fields through Patch.
        raw.Patch.Set("$.reasoning"u8, "original raw reasoning");

        raw.Patch.Set("$.vendor_marker"u8, "preserve this field");
#pragma warning restore SCME0001

        ChatMessage assistant = new(ChatRole.Assistant, [new TextReasoningContent(Reasoning), new TextContent(Visible)])
        {
            RawRepresentation = raw,
        };

        await RespondAsync(lease.ChatClient, [new(ChatRole.User, "synthetic request"), assistant], streaming);

        using JsonDocument request = JsonDocument.Parse(Assert.Single(transport.Bodies));

        JsonElement serialized = request.RootElement.GetProperty("messages")[1];

        Assert.Equal(Reasoning, serialized.GetProperty("reasoning").GetString());

        Assert.Equal("preserve this field", serialized.GetProperty("vendor_marker").GetString());

        Assert.Equal(Visible, serialized.GetProperty("content").GetString());

        Assert.Same(raw, assistant.RawRepresentation);

#pragma warning disable SCME0001
        Assert.True(raw.Patch.TryGetValue("$.reasoning"u8, out string? original));
#pragma warning restore SCME0001

        Assert.Equal("original raw reasoning", original);
    }

    private static ChatClientFactory CreateFactory(Capture transport, bool local)
    {
        ArcanumSettings settings = new()
        {
            DefaultModel = "ollama-fixture-model",

            Providers = [new() { Name = "ollama", Models = ["ollama-fixture-model"], Endpoint = "http://localhost:11434/v1", LocalRuntimeId = local ? "fixture-runtime" : null }],

            LocalRuntimes = [new() { Id = "fixture-runtime", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434" }],
        };

        return new(transport, new TestOptionsMonitor<ArcanumSettings>(settings), new MissingCredentials());
    }

    private static async Task<ChatResponse> RespondAsync(IChatClient client, IEnumerable<ChatMessage> messages, bool streaming)
    {
        if (!streaming) return await client.GetResponseAsync(messages, cancellationToken: CancellationToken.None);

        List<ChatResponseUpdate> updates = [];

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, cancellationToken: CancellationToken.None))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    private sealed class MissingCredentials : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class SyntheticFunction : AIFunction
    {
        private static readonly JsonDocument Schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"},"line":{"type":"integer"}},"required":["path","line"]}""");

        public override string Name => "synthetic_tool";

        public override JsonElement JsonSchema => Schema.RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => ValueTask.FromResult<object?>(ToolResult);
    }

    private sealed class Capture(bool streaming, string dialect) : HttpMessageHandler, IHttpClientFactory
    {
        internal List<string> Bodies { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            string body = Bodies.Count == 1 ? FirstResponse() : FinalResponse();

            return new(HttpStatusCode.OK) { Content = ProviderSdkTestTransport.Content(body, streaming ? "text/event-stream" : "application/json") };
        }

        private string FirstResponse()
        {
            if (!streaming)
            {
                return $$$"""{"id":"synthetic","object":"chat.completion","created":0,"model":"ollama-fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":"SYNTHETIC_VISIBLE_REPLY",{{{Fields(Reasoning)}}}"tool_calls":[{"id":"call-synthetic","type":"function","function":{"name":"synthetic_tool","arguments":"{\"path\":\"notes.txt\",\"line\":7}"}}]},"finish_reason":"tool_calls"}]}""";
            }

            return Chunk($$$"""{"role":"assistant","content":"SYNTHETIC_VISIBLE_",{{{Fields("SYNTHETIC_")}}}"tool_calls":[{"index":0,"id":"call-synthetic","type":"function","function":{"name":"synthetic_tool","arguments":"{\"path\":\"notes.txt\",\"line\":7}"}}]}""")
                + Chunk($$$"""{"content":"REPLY",{{{Fields("REASONING_MARKER")}}}"tool_calls":[]}""")
                + Chunk("{}", "tool_calls")
                + "data: [DONE]\n\n";
        }

        private string Fields(string text) => dialect switch
        {
            "reasoning" => $"\"reasoning\":\"{text}\",",

            "reasoning_content" => $"\"reasoning_content\":\"{text}\",",

            "both" => $"\"reasoning\":\"{text}\",\"reasoning_content\":\"{text}\",",

            "conflict" => $"\"reasoning\":\"IGNORED_ALTERNATE_{text}\",\"reasoning_content\":\"{text}\",",

            _ => throw new InvalidOperationException("Unknown synthetic dialect."),
        };

        private string FinalResponse() => streaming
            ? Chunk("{\"role\":\"assistant\",\"content\":\"SYNTHETIC_FINAL\"}", "stop") + "data: [DONE]\n\n"
            : """{"id":"synthetic-final","object":"chat.completion","created":0,"model":"ollama-fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":"SYNTHETIC_FINAL"},"finish_reason":"stop"}]}""";

        private static string Chunk(string delta, string? finish = null) =>
            $$$"""data: {"id":"synthetic","object":"chat.completion.chunk","created":0,"model":"ollama-fixture-model","choices":[{"index":0,"delta":{{{delta}}},"finish_reason":{{{(finish is null ? "null" : $"\"{finish}\"")}}}}]}""" + "\n\n";
    }
}
