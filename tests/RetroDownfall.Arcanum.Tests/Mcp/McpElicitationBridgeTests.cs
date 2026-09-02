using System.Text.Json;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// Drives the bridge through the SDK handler delegate it hands every connection, which is the entry
/// point the SDK's receive loop uses.
/// </summary>
public sealed class McpElicitationBridgeTests
{

    [Fact]
    public async Task Handler_declines_when_no_attended_turn_is_calling()
    {

        (Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler, _, _) = Create();

        ElicitResult result = await handler(new ElicitRequestParams { Message = "Question?" }, CancellationToken.None);

        AssertDeclined(result, McpElicitationSink.NoAttendedTurnReason);

    }

    [Fact]
    public async Task Handler_declines_when_two_turns_are_calling()
    {

        (Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler, McpElicitationSink sink, _) = Create();

        using IDisposable first = sink.Enter(new AnsweringEmitter(new HumanPromptRegistry(), "one"));

        using IDisposable second = sink.Enter(new AnsweringEmitter(new HumanPromptRegistry(), "two"));

        ElicitResult result = await handler(new ElicitRequestParams { Message = "Question?" }, CancellationToken.None);

        AssertDeclined(result, McpElicitationSink.AmbiguousTurnReason);

    }

    [Fact]
    public async Task Handler_declines_url_mode()
    {

        (Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler, McpElicitationSink sink, HumanPromptRegistry registry) = Create();

        using IDisposable scope = sink.Enter(new AnsweringEmitter(registry, "42"));

        ElicitResult result = await handler(
            new ElicitRequestParams { Message = "Open this.", Mode = "url", Url = "https://example.test/consent" },
            CancellationToken.None);

        AssertDeclined(result, "Elicitation mode 'url' is not supported by the text-only operator UI.");

    }

    [Fact]
    public async Task Handler_declines_a_form_request_that_carries_a_url()
    {

        (Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler, McpElicitationSink sink, HumanPromptRegistry registry) = Create();

        using IDisposable scope = sink.Enter(new AnsweringEmitter(registry, "42"));

        ElicitResult result = await handler(
            new ElicitRequestParams { Message = "Open this.", Url = "https://example.test/consent" },
            CancellationToken.None);

        AssertDeclined(result, "URL-mode elicitation is not supported by the text-only operator UI.");

    }

    [Fact]
    public async Task Handler_declines_multi_field_schemas()
    {

        (Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler, McpElicitationSink sink, HumanPromptRegistry registry) = Create();

        using IDisposable scope = sink.Enter(new AnsweringEmitter(registry, "42"));

        ElicitRequestParams request = new()
        {
            Message = "Two things?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["first"] = new ElicitRequestParams.StringSchema(),
                    ["second"] = new ElicitRequestParams.StringSchema(),
                },
            },
        };

        ElicitResult result = await handler(request, CancellationToken.None);

        AssertDeclined(result, "Structured multi-field elicitation schemas are not supported by the text-only operator UI.");

    }

    [Fact]
    public async Task Handler_declines_non_string_fields()
    {

        (Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler, McpElicitationSink sink, HumanPromptRegistry registry) = Create();

        using IDisposable scope = sink.Enter(new AnsweringEmitter(registry, "42"));

        ElicitRequestParams request = new()
        {
            Message = "How many?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["count"] = new ElicitRequestParams.NumberSchema(),
                },
            },
        };

        ElicitResult result = await handler(request, CancellationToken.None);

        AssertDeclined(result, "Only free-text (string) elicitation fields are supported by the text-only operator UI.");

    }

    [Fact]
    public async Task Handler_accepts_under_the_value_key_when_no_schema_is_given()
    {

        (Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler, McpElicitationSink sink, HumanPromptRegistry registry) = Create();

        AnsweringEmitter emitter = new(registry, "42");

        using IDisposable scope = sink.Enter(emitter);

        ElicitResult result = await handler(new ElicitRequestParams { Message = "What is the answer?" }, CancellationToken.None);

        Assert.Equal("accept", result.Action);

        Assert.Equal("42", Assert.Contains("value", result.Content!).GetString());

        Assert.Equal(["ask_human:ToolCall", "ask_human:ToolResult"], emitter.Frames);

        Assert.Equal("What is the answer?", emitter.Question);

        Assert.Equal(0, registry.WaiterCountForTesting);

    }

    [Fact]
    public async Task Handler_accepts_under_the_field_key_for_a_single_string_field()
    {

        (Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler, McpElicitationSink sink, HumanPromptRegistry registry) = Create();

        using IDisposable scope = sink.Enter(new AnsweringEmitter(registry, "blue"));

        ElicitRequestParams request = new()
        {
            Message = "Favourite colour?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["colour"] = new ElicitRequestParams.StringSchema(),
                },
            },
        };

        ElicitResult result = await handler(request, CancellationToken.None);

        Assert.Equal("accept", result.Action);

        Assert.Equal("blue", Assert.Contains("colour", result.Content!).GetString());

    }

    private static (Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> Handler, McpElicitationSink Sink, HumanPromptRegistry Registry) Create()
    {

        HumanPromptRegistry registry = new();

        McpElicitationSink sink = new();

        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler =
            new McpElicitationBridge(registry).CreateClientHandlers(sink).ElicitationHandler!;

        return (handler, sink, registry);

    }

    private static void AssertDeclined(ElicitResult result, string expectedReason)
    {

        Assert.Equal("decline", result.Action);

        Assert.Equal(expectedReason, Assert.Contains("reason", result.Content!).GetString());

    }

    /// <summary>
    /// Stands in for the turn's live channel: records the frames the bridge emits and answers the
    /// prompt through the registry the moment the ask_human call frame names it, the way an operator
    /// answering through the API would.
    /// </summary>
    private sealed class AnsweringEmitter(HumanPromptRegistry registry, string answer) : IHumanPromptLiveEmitter
    {

        public List<string> Frames { get; } = [];

        public string? Question { get; private set; }

        public ValueTask EmitAsync(IntelligenceEvent evt, CancellationToken cancellationToken)
        {

            Frames.Add($"{evt.ToolCall?.Name}:{evt.Type}");

            if (evt.Type == IntelligenceEventType.ToolCall && evt.ToolCall is not null)
            {

                JsonElement args = JsonDocument.Parse(evt.ToolCall.ArgumentsJson).RootElement;

                Question = args.GetProperty("question").GetString();

                Assert.True(registry.TrySubmitResponse(args.GetProperty("promptId").GetString()!, answer));

            }

            return ValueTask.CompletedTask;

        }

    }

}
