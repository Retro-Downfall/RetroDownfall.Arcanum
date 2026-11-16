using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    private const string OutputLimitPrivateReasoning = "private-output-limit-reasoning-canary";

    private const string OutputLimitProtectedReasoning = "private-output-limit-protected-canary";

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, " \t\r\n")]
    [InlineData(true, " \t\r\n")]
    public async Task OutputLimitNotice_final_empty_or_whitespace_length_survives_native_projection_without_replacing_answer(
        bool streaming,
        string answer)
    {
        ScriptingChatClient chat = new();

        EnqueueOutputLimitResponse(chat, streaming, answer, ChatFinishReason.Length, includePrivateReasoning: true);

        FakeGrimoireRepository grimoire = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, SettingsWithReasoning(), grimoire: grimoire);

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(wizard, streaming, OutputLimitRequest());

        Assert.True(observed.Success, observed.WireJson);

        Assert.Equal(answer, observed.Text);

        Assert.Equal(answer, grimoire.LastFinalizedContent);

        Assert.Equal("length", observed.FinishReason);

        AssertOutputLimitUsage(observed.Usage);

        Assert.Empty(observed.ToolCalls);

        InferenceNotice notice = Assert.Single(observed.Notices);

        Assert.Equal(3, (int)notice.Code);

        Assert.Equal("OutputLimitWithoutAnswer", Enum.GetName(notice.Code));

        Assert.Contains("output limit", notice.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("answer", notice.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("reasoning", notice.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(OutputLimitPrivateReasoning, observed.WireJson, StringComparison.Ordinal);

        Assert.DoesNotContain(OutputLimitProtectedReasoning, observed.WireJson, StringComparison.Ordinal);

        Assert.DoesNotContain(observed.Events, static frame => frame.Type == IntelligenceEventType.Reasoning);

        using JsonDocument wire = JsonDocument.Parse(observed.TerminalJson);

        JsonElement serializedNotice = Assert.Single(wire.RootElement.GetProperty("notices").EnumerateArray());

        Assert.Equal(3, serializedNotice.GetProperty("code").GetInt32());

        Assert.Equal(notice.Message, serializedNotice.GetProperty("message").GetString());

        if (streaming)
        {
            IntelligenceEvent status = Assert.Single(observed.Events,
                frame => frame.Type == IntelligenceEventType.Status && frame.Notices.Any(item => (int)item.Code == 3));

            Assert.Equal(notice.Message, status.Message);

            Assert.Equal(answer, string.Concat(observed.Events.Where(static frame => frame.Type == IntelligenceEventType.Token).Select(static frame => frame.Data)));
        }

        Assert.Equal(1, streaming ? chat.StreamingCallCount : chat.BufferedCallCount);
    }

    [Theory]
    [InlineData(false, "visible partial answer", "length")]
    [InlineData(true, "visible partial answer", "length")]
    [InlineData(false, "", "stop")]
    [InlineData(true, "", "stop")]
    public async Task OutputLimitNotice_nonempty_length_and_empty_stop_are_not_misclassified(
        bool streaming,
        string answer,
        string finishReason)
    {
        ScriptingChatClient chat = new();

        EnqueueOutputLimitResponse(chat, streaming, answer, new ChatFinishReason(finishReason));

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(CreateWizard(chat), streaming, OutputLimitRequest());

        Assert.True(observed.Success, observed.WireJson);

        Assert.Equal(answer, observed.Text);

        Assert.Equal(finishReason, observed.FinishReason);

        AssertOutputLimitUsage(observed.Usage);

        Assert.DoesNotContain(observed.Notices, static notice => (int)notice.Code == 3);

        Assert.DoesNotContain(observed.Events, static frame => frame.Notices.Any(notice => (int)notice.Code == 3));

        Assert.Equal(1, streaming ? chat.StreamingCallCount : chat.BufferedCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputLimitNotice_provider_failure_is_not_a_successful_output_limit_notice(bool streaming)
    {
        ScriptingChatClient chat = new();

        HttpRequestException failure = new("private-output-limit-provider-failure");

        if (streaming)
        {
            chat.EnqueueImmediateStreamFailure(failure);
        }
        else
        {
            chat.EnqueueException(failure);
        }

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(CreateWizard(chat), streaming, OutputLimitRequest());

        Assert.False(observed.Success, observed.WireJson);

        Assert.Empty(observed.Notices);

        Assert.DoesNotContain(observed.Events, static frame => frame.Notices.Any(notice => (int)notice.Code == 3));

        Assert.DoesNotContain("private-output-limit-provider-failure", observed.WireJson, StringComparison.Ordinal);

        Assert.Equal(1, streaming ? chat.StreamingCallCount : chat.BufferedCallCount);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "final answer")]
    [InlineData(true, "final answer")]
    public async Task OutputLimitNotice_applies_only_to_the_final_answer_and_preserves_executed_tool_calls(
        bool streaming,
        string finalAnswer)
    {
        ScriptingChatClient chat = new();

        FunctionCallContent call = new("output-limit-tool-call", "get_local_system_time", new Dictionary<string, object?>());

        if (streaming)
        {
            chat.EnqueueStreamUpdates(new ChatResponseUpdate(ChatRole.Assistant, [call]) { FinishReason = ChatFinishReason.Length });
        }
        else
        {
            chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, [call])) { FinishReason = ChatFinishReason.Length });
        }

        EnqueueOutputLimitResponse(chat, streaming, finalAnswer, finalAnswer.Length == 0 ? ChatFinishReason.Length : ChatFinishReason.Stop);

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(CreateWizard(chat), streaming,
            OutputLimitRequest() with { DisableAllTools = false, DisableMcpTools = true });

        Assert.True(observed.Success, observed.WireJson);

        Assert.Equal(finalAnswer, observed.Text);

        Assert.Equal("get_local_system_time", Assert.Single(observed.ToolCalls).Name);

        Assert.Equal(finalAnswer.Length == 0 ? 1 : 0, observed.Notices.Count(static notice => (int)notice.Code == 3));

        Assert.Equal(finalAnswer.Length == 0 && streaming ? 1 : 0,
            observed.Events.Count(static frame => frame.Type == IntelligenceEventType.Status && frame.Notices.Any(notice => (int)notice.Code == 3)));

        AssertOutputLimitUsage(observed.Usage);

        Assert.Equal(2, streaming ? chat.StreamingCallCount : chat.BufferedCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputLimitNotice_actual_sdk_length_metadata_reaches_native_notice_without_an_extra_send(bool streaming)
    {
        ArcanumSettings configuration = DefaultSettings();

        using OutputLimitHttpFactory http = new(streaming, cleanEof: false, answer: "");

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), new OutputLimitCredentialResolver());

        WizardIntelligenceProvider wizard = CreateWizard(new(), configuration, factory: factory);

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(wizard, streaming, OutputLimitRequest());

        Assert.True(observed.Success, observed.WireJson);

        Assert.Equal(string.Empty, observed.Text);

        Assert.Equal("length", observed.FinishReason);

        Assert.Equal(3, (int)Assert.Single(observed.Notices).Code);

        AssertOutputLimitUsage(observed.Usage);

        Assert.Equal(1, http.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputLimitNotice_actual_sdk_preserves_synthetic_visible_content_at_length(bool streaming)
    {
        ArcanumSettings settings = DefaultSettings();

        using OutputLimitHttpFactory http = new(streaming, cleanEof: false, answer: "visible synthetic answer");

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(settings), new OutputLimitCredentialResolver());

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(CreateWizard(new(), settings, factory: factory), streaming, OutputLimitRequest());

        Assert.True(observed.Success, observed.WireJson);

        Assert.Equal("visible synthetic answer", observed.Text);

        Assert.Equal("length", observed.FinishReason);

        Assert.Equal(4, (int)Assert.Single(observed.Notices).Code);

        AssertOutputLimitUsage(observed.Usage);

        Assert.Equal(1, http.Sends);
    }

    [Fact]
    public async Task OutputLimitNotice_actual_sdk_reasoning_extension_only_is_not_a_visible_answer()
    {
        const string privateCanary = "synthetic-private-reasoning-canary";

        ArcanumSettings settings = DefaultSettings();

        string body = SyntheticSse("{\"role\":\"assistant\",\"reasoning_content\":\"" + privateCanary + "\"}", "length");

        using SyntheticOutputHttpFactory http = new(true, body);

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(settings), new OutputLimitCredentialResolver());

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(CreateWizard(new(), settings, factory: factory), true,
            OutputLimitRequest() with { Reasoning = null });

        Assert.True(observed.Success, observed.WireJson);

        Assert.Equal(string.Empty, observed.Text);

        Assert.Equal("length", observed.FinishReason);

        Assert.Equal(3, (int)Assert.Single(observed.Notices).Code);

        Assert.DoesNotContain(privateCanary, observed.WireJson, StringComparison.Ordinal);

        Assert.DoesNotContain(observed.Events, static frame => frame.Type == IntelligenceEventType.Token || frame.Type == IntelligenceEventType.Reasoning);

        Assert.Equal(1, http.Sends);
    }

    [Fact]
    public async Task OutputLimitNotice_actual_sdk_executes_a_complete_fragmented_tool_call_once()
    {
        ArcanumSettings settings = DefaultSettings();

        string first = SyntheticToolSse("{}", "tool_calls");

        string second = SyntheticSse("{\"role\":\"assistant\",\"content\":\"tool complete\"}", "stop");

        using SyntheticOutputHttpFactory http = new(true, first, second);

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(settings), new OutputLimitCredentialResolver());

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(CreateWizard(new(), settings, factory: factory), true,
            OutputLimitRequest() with { DisableAllTools = false, DisableMcpTools = true });

        Assert.True(observed.Success, observed.WireJson);

        Assert.Equal("tool complete", observed.Text);

        Assert.Equal("get_local_system_time", Assert.Single(observed.ToolCalls).Name);

        Assert.Equal(2, http.Sends);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task OutputLimitNotice_actual_sdk_does_not_execute_truncated_tool_arguments(bool streaming, bool forward, bool mixed)
    {
        ArcanumSettings settings = DefaultSettings();

        string arguments = "{\"synthetic-malformed-canary\":";

        string body = mixed ? SyntheticMixedToolResponse(streaming, arguments)
            : streaming ? SyntheticToolSse(arguments, "length") : SyntheticToolBuffered(arguments, "length");

        using SyntheticOutputHttpFactory http = new(streaming, body);

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(settings), new OutputLimitCredentialResolver());

        FakeGrimoireRepository grimoire = new();

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(CreateWizard(new(), settings, grimoire: grimoire, factory: factory), streaming,
            OutputLimitRequest() with
            {
                DisableAllTools = false,
                DisableMcpTools = true,
                ForwardClientTools = forward,
                ClientTools = forward ? [new OpenAiToolDefinition("function", new OpenAiFunctionDefinition("get_local_system_time"))] : null,
            });

        Assert.True(observed.ToolCalls.Count == 0
            && observed.Events.All(static frame => frame.Type != IntelligenceEventType.ToolResult)
            && grimoire.AppendToolInteractionCallCount == 0 && http.Sends == 1
            && !observed.Success && observed.WireJson.Contains(ErrorCodes.Hub.ProviderToolCallInvalid, StringComparison.Ordinal),
            $"Incomplete arguments produced {observed.ToolCalls.Count} tool-call events, "
            + $"{observed.Events.Count(static frame => frame.Type == IntelligenceEventType.ToolResult)} tool-result events, "
            + $"{observed.Events.Count(static frame => frame.Type == IntelligenceEventType.ToolError)} tool-error events, "
            + $"{observed.Events.Count(static frame => frame.Type == IntelligenceEventType.ToolResult && !frame.ToolDenied && frame.ToolOutcome is null)} non-denied results, "
            + $"{grimoire.AppendToolInteractionCallCount} persisted tool interactions, {http.Sends} provider sends, "
            + $"success={observed.Success}, and typedInvalid={observed.WireJson.Contains(ErrorCodes.Hub.ProviderToolCallInvalid, StringComparison.Ordinal)}.");

        Assert.DoesNotContain("synthetic-malformed-canary", observed.WireJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"unfinished\":", true)]
    public async Task OutputLimitNotice_sdk_reports_malformed_tool_argument_metadata(string arguments, bool malformed)
    {
        ArcanumSettings settings = DefaultSettings();

        using SyntheticOutputHttpFactory http = new(true, SyntheticToolSse(arguments, malformed ? "length" : "tool_calls"));

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(settings), new OutputLimitCredentialResolver());

        using ChatClientLease lease = await factory.ResolveClientAsync(null, CancellationToken.None);

        List<ChatResponseUpdate> updates = [];

        await foreach (ChatResponseUpdate update in lease.ChatClient.GetStreamingResponseAsync(
            [new MeAiChatMessage(ChatRole.User, "synthetic request")], new ChatOptions(), CancellationToken.None))
        {
            updates.Add(update);
        }

        FunctionCallContent call = Assert.Single(ToolExecutionPipeline.CollectActionableFunctionCalls(updates.ToChatResponse()));

        Assert.True((call.Exception is not null) == malformed,
            $"Expected malformed={malformed}; exception present={call.Exception is not null}, arguments null={call.Arguments is null}, raw type={call.RawRepresentation?.GetType().FullName ?? "null"}.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("partial visible answer")]
    public async Task OutputLimitNotice_actual_sdk_clean_eof_without_finish_metadata_is_incomplete_not_success_or_length(string answer)
    {
        ArcanumSettings configuration = DefaultSettings();

        using OutputLimitHttpFactory http = new(streaming: true, cleanEof: true, answer: answer);

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), new OutputLimitCredentialResolver());

        WizardIntelligenceProvider wizard = CreateWizard(new(), configuration, factory: factory);

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(wizard, streaming: true, OutputLimitRequest());

        Assert.Equal(1, http.Sends);

        Assert.DoesNotContain(observed.Notices, static notice => (int)notice.Code == 3);

        Assert.DoesNotContain(observed.Events, static frame => frame.Notices.Any(notice => (int)notice.Code == 3));

        Assert.False(observed.Success,
            "The real SDK exhausted a non-seekable upstream SSE body without finish metadata or [DONE], but the native turn reported success. Observed: " + observed.TerminalJson);

        Assert.DoesNotContain(observed.Events, static frame => frame.Type == IntelligenceEventType.Result);

        Assert.Equal("Hub.IncompleteProviderResponse", Assert.Single(observed.Events, static frame => frame.Type == IntelligenceEventType.Error).Data);
    }

    private PingRequest OutputLimitRequest() => BaseRequest() with
    {
        Prompt = "Give a visible answer without exposing reasoning.",
        SkipSpellRouting = true,
        DisableAllTools = true,
        MaxOutputTokens = 8,
        Reasoning = new ReasoningRequestOptions(Output: ReasoningOutputMode.None),
    };

    private static void EnqueueOutputLimitResponse(
        ScriptingChatClient chat,
        bool streaming,
        string answer,
        ChatFinishReason finishReason,
        bool includePrivateReasoning = false)
    {
        List<AIContent> content = [];

        if (includePrivateReasoning)
        {
            content.Add(new TextReasoningContent(OutputLimitPrivateReasoning) { ProtectedData = OutputLimitProtectedReasoning });
        }

        content.Add(new TextContent(answer));

        UsageDetails usage = new()
        {
            InputTokenCount = 10,
            OutputTokenCount = 8,
            TotalTokenCount = 19,
            CachedInputTokenCount = 3,
            ReasoningTokenCount = 7,
        };

        if (streaming)
        {
            chat.EnqueueStreamUpdates(
                new ChatResponseUpdate(ChatRole.Assistant, content),
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]) { FinishReason = finishReason });
        }
        else
        {
            chat.EnqueueResponse(new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, content))
            {
                FinishReason = finishReason,
                Usage = usage,
            });
        }
    }

    private static void AssertOutputLimitUsage(ChatCompletionUsage? usage)
    {
        Assert.NotNull(usage);

        Assert.Equal(10, usage.PromptTokens);

        Assert.Equal(8, usage.CompletionTokens);

        Assert.Equal(19, usage.TotalTokens);

        Assert.Equal(3, usage.CachedTokens);

        Assert.Equal(7, usage.ReasoningTokens);
    }

    private static async Task<OutputLimitObservation> ObserveOutputLimitTurnAsync(
        WizardIntelligenceProvider wizard,
        bool streaming,
        PingRequest request)
    {
        // Deliberately retain the actual Wizard -> TurnEngine -> native projection seam.
        TurnExecutionCoordinator coordinator = new(new TurnEngine(wizard));

        if (!streaming)
        {
            Result<PromptTurnResult> result = await coordinator.ExecuteBufferedAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            if (result.IsFailure)
            {
                string failure = result.Error.Code + ":" + result.Error.Message;

                return new(false, string.Empty, null, null, [], [], [], failure, failure);
            }

            string json = JsonSerializer.Serialize(PromptResponseDto.From(result.Value), ArcanumJsonContext.Default.PromptResponseDto);

            return new(true, result.Value.Text, result.Value.FinishReason, result.Value.Usage, result.Value.Notices,
                [], result.Value.ToolCalls ?? [], json, json);
        }

        List<IntelligenceEvent> frames = [];

        await foreach (IntelligenceEvent frame in coordinator.ExecuteIntelligenceStreamAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None))
        {
            frames.Add(frame);
        }

        IntelligenceEvent? terminal = frames.SingleOrDefault(static frame => frame.Type == IntelligenceEventType.Result);

        string wire = string.Join('\n', frames.Select(static frame => JsonSerializer.Serialize(frame, ArcanumJsonContext.Default.IntelligenceEvent)));

        string terminalJson = terminal is null ? wire : JsonSerializer.Serialize(terminal, ArcanumJsonContext.Default.IntelligenceEvent);

        PromptToolCall[] calls = frames.Where(static frame => frame.Type == IntelligenceEventType.ToolCall && frame.ToolCall is not null)
            .Select(static frame => new PromptToolCall(frame.ToolCall!.CallId, frame.ToolCall.Name, frame.ToolCall.ArgumentsJson)).ToArray();

        return new(terminal is not null, terminal?.Message ?? string.Empty, terminal?.FinishReason, terminal?.Usage,
            terminal?.Notices ?? [], frames, calls, terminalJson, wire);
    }

    private sealed record OutputLimitObservation(
        bool Success,
        string Text,
        string? FinishReason,
        ChatCompletionUsage? Usage,
        IReadOnlyList<InferenceNotice> Notices,
        IReadOnlyList<IntelligenceEvent> Events,
        IReadOnlyList<PromptToolCall> ToolCalls,
        string TerminalJson,
        string WireJson);

    private static string SyntheticChunk(string delta, string? finishReason = null) =>
        "data: {\"id\":\"synthetic-wire\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"wizard-test-model\",\"choices\":[{\"index\":0,\"delta\":"
        + delta + ",\"finish_reason\":" + JsonSerializer.Serialize(finishReason, AdHocJson.Options) + "}]}\n\n";

    private static string SyntheticSse(string delta, string finishReason) =>
        SyntheticChunk(delta) + SyntheticChunk("{}", finishReason)
        + "data: {\"id\":\"synthetic-wire\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"wizard-test-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":8,\"total_tokens\":18}}\n\n"
        + "data: [DONE]\n\n";

    private static string SyntheticToolSse(string arguments, string finishReason)
    {
        int split = Math.Max(1, arguments.Length / 2);

        string first = "{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"synthetic-call\",\"type\":\"function\",\"function\":{\"name\":\"get_local_system_time\",\"arguments\":"
            + JsonSerializer.Serialize(arguments[..split], AdHocJson.Options) + "}}]}";

        string second = "{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":"
            + JsonSerializer.Serialize(arguments[split..], AdHocJson.Options) + "}}]}";

        return SyntheticChunk(first) + SyntheticSse(second, finishReason);
    }

    private static string SyntheticToolBuffered(string arguments, string finishReason) =>
        "{\"id\":\"synthetic-wire\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"wizard-test-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"synthetic-call\",\"type\":\"function\",\"function\":{\"name\":\"get_local_system_time\",\"arguments\":"
        + JsonSerializer.Serialize(arguments, AdHocJson.Options) + "}}]},\"finish_reason\":" + JsonSerializer.Serialize(finishReason, AdHocJson.Options) + "}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":8,\"total_tokens\":18}}";

    private static string SyntheticMixedToolResponse(bool streaming, string arguments)
    {
        string valid = "{\"id\":\"valid-first\",\"type\":\"function\",\"function\":{\"name\":\"get_local_system_time\",\"arguments\":\"{}\"}}";

        string invalid = "{\"id\":\"invalid-second\",\"type\":\"function\",\"function\":{\"name\":\"get_local_system_time\",\"arguments\":"
            + JsonSerializer.Serialize(arguments, AdHocJson.Options) + "}}";

        if (streaming)
        {
            valid = valid.Insert(1, "\"index\":0,");

            invalid = invalid.Insert(1, "\"index\":1,");

            return SyntheticSse("{\"role\":\"assistant\",\"tool_calls\":[" + valid + "," + invalid + "]}", "length");
        }

        return "{\"id\":\"synthetic-wire\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"wizard-test-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"tool_calls\":["
            + valid + "," + invalid + "]},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":8,\"total_tokens\":18}}";
    }

    private sealed class SyntheticOutputHttpFactory(bool streaming, params string[] responses) : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> _clients = [];

        private readonly string[] _responses = responses;

        private readonly bool _streaming = streaming;

        public int Sends { get; private set; }

        public HttpClient CreateClient(string name)
        {
            HttpClient client = new(new Handler(this));

            _clients.Add(client);

            return client;
        }

        public void Dispose()
        {
            foreach (HttpClient client in _clients) client.Dispose();
        }

        private sealed class Handler(SyntheticOutputHttpFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                int index = owner.Sends++;

                string body = index < owner._responses.Length ? owner._responses[index] : SyntheticSse("{}", "stop");

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = ProviderSdkTestTransport.Content(body, owner._streaming ? "text/event-stream" : "application/json"),
                });
            }
        }
    }

    private sealed class OutputLimitCredentialResolver : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("synthetic-output-limit-key");
    }

    private sealed class OutputLimitHttpFactory(bool streaming, bool cleanEof, string answer) : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> _clients = [];

        private readonly bool _streaming = streaming;

        private readonly bool _cleanEof = cleanEof;

        private readonly string _answer = answer;

        public int Sends { get; private set; }

        public HttpClient CreateClient(string name)
        {
            HttpClient client = new(new Handler(this));

            _clients.Add(client);

            return client;
        }

        public void Dispose()
        {
            foreach (HttpClient client in _clients)
            {
                client.Dispose();
            }
        }

        private sealed class Handler(OutputLimitHttpFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Sends++;

                string encodedAnswer = JsonSerializer.Serialize(owner._answer, AdHocJson.Options);

                const string usage = "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":8,\"total_tokens\":19,\"prompt_tokens_details\":{\"cached_tokens\":3},\"completion_tokens_details\":{\"reasoning_tokens\":7}}";

                string body;

                if (owner._streaming)
                {
                    body = "data: {\"id\":\"output-limit\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"wizard-test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":"
                        + encodedAnswer + "},\"finish_reason\":null}]}\n\n";

                    if (!owner._cleanEof)
                    {
                        body += "data: {\"id\":\"output-limit\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"wizard-test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]}\n\n"
                            + "data: {\"id\":\"output-limit\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"wizard-test-model\",\"choices\":[]," + usage + "}\n\n"
                            + "data: [DONE]\n\n";
                    }
                }
                else
                {
                    body = "{\"id\":\"output-limit\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"wizard-test-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
                        + encodedAnswer + "},\"finish_reason\":\"length\"}]," + usage + "}";
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = ProviderSdkTestTransport.Content(body, owner._streaming ? "text/event-stream" : "application/json"),
                });
            }
        }
    }
}
