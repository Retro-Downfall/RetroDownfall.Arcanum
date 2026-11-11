using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Frozen text/function protocol data. Function descriptions confer no invocation authority.</summary>
internal sealed class GuildProviderPayload
{
    private readonly JsonElement _messages;

    private readonly JsonElement _tools;

    internal bool HasTools => _tools.GetArrayLength() != 0;

    internal List<ChatMessage> CreateMessages()
    {
        List<ChatMessage> result = [];

        foreach (JsonElement message in _messages.EnumerateArray())
        {
            ChatRole role = new(message.GetProperty("role").GetString()!);

            List<AIContent> contents = [];

            JsonElement text = message.GetProperty("content");

            if (role == ChatRole.Tool)
                contents.Add(new FunctionResultContent(message.GetProperty("tool_call_id").GetString()!, text.GetString()!));

            else
            {
                if (text.ValueKind == JsonValueKind.String) contents.Add(new TextContent(text.GetString()));

                if (message.TryGetProperty("tool_calls", out JsonElement calls))
                    foreach (JsonElement call in calls.EnumerateArray())
                    {
                        JsonElement function = call.GetProperty("function");

                        using JsonDocument arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);

                        Dictionary<string, object?> frozen = new(StringComparer.Ordinal);

                        foreach (JsonProperty argument in arguments.RootElement.EnumerateObject()) frozen.Add(argument.Name, argument.Value.Clone());

                        contents.Add(new FunctionCallContent(call.GetProperty("id").GetString()!, function.GetProperty("name").GetString()!, frozen));
                    }
            }

            result.Add(new(role, contents));
        }

        return result;
    }

    internal IReadOnlyList<AIFunction> CreateFunctions() => _tools.EnumerateArray()
        .Select(static tool => (AIFunction)new FrozenFunction(tool.GetProperty("function"))).ToArray();

    internal bool Matches(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction> functions)
    {
        GuildProviderPayload other = new(messages, functions);

        return JsonElement.DeepEquals(_messages, other._messages) && JsonElement.DeepEquals(_tools, other._tools);
    }

    internal GuildProviderPayload(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction> functions)
    {
        if (messages.Count == 0 || messages[0].Role != ChatRole.User) throw Invalid();

        HashSet<string> names = new(StringComparer.Ordinal);

        _tools = Build(writer =>
        {
            writer.WriteStartArray();

            foreach (AIFunction function in functions)
            {
                if (function.Name is not ("delegate_task" or "create_agent_task" or "send_agent_message" or "get_agent_task" or "cancel_agent_task" or "list_agents" or "search_memory"
                    or "create_cohort" or "admit_cohort_member" or "close_cohort" or "remove_cohort_member") || !names.Add(function.Name)) throw Invalid();

                writer.WriteStartObject();

                writer.WriteString("type", "function");

                writer.WriteStartObject("function");

                writer.WriteString("name", function.Name);

                writer.WriteString("description", function.Description);

                writer.WritePropertyName("parameters");

                function.JsonSchema.WriteTo(writer);

                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });

        _messages = Build(writer =>
        {
            writer.WriteStartArray();

            foreach (ChatMessage message in messages)
            {
                if (message.AdditionalProperties is { Count: > 0 } || message.RawRepresentation is not null
                    || message.AuthorName is not null) throw Invalid();

                writer.WriteStartObject();

                writer.WriteString("role", message.Role.Value);

                if (message.Role == ChatRole.Tool)
                {
                    if (message.Contents.Count != 1 || message.Contents[0] is not FunctionResultContent result
                        || string.IsNullOrWhiteSpace(result.CallId) || result.Result is not string text) throw Invalid();

                    writer.WriteString("tool_call_id", result.CallId);

                    writer.WriteString("content", text);
                }
                else if (message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
                {
                    TextContent[] text = message.Contents.OfType<TextContent>().ToArray();

                    FunctionCallContent[] calls = message.Contents.OfType<FunctionCallContent>().ToArray();

                    if (text.Length > 1 || text.Length + calls.Length != message.Contents.Count
                        || message.Role == ChatRole.User && (text.Length != 1 || calls.Length != 0)) throw Invalid();

                    if (text.Length == 1) writer.WriteString("content", text[0].Text);

                    else writer.WriteNull("content");

                    if (calls.Length > 0)
                    {
                        writer.WriteStartArray("tool_calls");

                        foreach (FunctionCallContent call in calls)
                        {
                            if (!names.Contains(call.Name) || string.IsNullOrWhiteSpace(call.CallId)
                                || call.AdditionalProperties is { Count: > 0 } || call.InformationalOnly) throw Invalid();

                            string arguments = ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(call);

                            if (string.IsNullOrEmpty(arguments)) arguments = "{}";

                            writer.WriteStartObject();

                            writer.WriteString("id", call.CallId);

                            writer.WriteString("type", "function");

                            writer.WriteStartObject("function");

                            writer.WriteString("name", call.Name);

                            writer.WriteString("arguments", arguments);

                            writer.WriteEndObject();

                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                    }
                }
                else throw Invalid();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("messages");

        _messages.WriteTo(writer);

        if (!HasTools) return;

        writer.WritePropertyName("tools");

        _tools.WriteTo(writer);

        writer.WriteString("tool_choice", "auto");

        writer.WriteBoolean("parallel_tool_calls", false);
    }

    internal void Validate(JsonElement root)
    {
        JsonElement actual = root.GetProperty("messages");

        JsonElement normalized = Build(writer =>
        {
            writer.WriteStartArray();

            foreach (JsonElement message in actual.EnumerateArray())
            {
                HashSet<string> keys = new(StringComparer.Ordinal);

                foreach (JsonProperty property in message.EnumerateObject())
                    if (!keys.Add(property.Name) || property.Name is not ("role" or "content" or "tool_call_id" or "tool_calls")) throw Invalid();

                writer.WriteStartObject();

                writer.WriteString("role", message.GetProperty("role").GetString());

                if (message.TryGetProperty("tool_call_id", out JsonElement callId)) writer.WriteString("tool_call_id", callId.GetString());

                if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind == JsonValueKind.Null)
                    writer.WriteNull("content");

                else if (content.ValueKind == JsonValueKind.String) writer.WriteString("content", content.GetString());

                else if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 1
                    && content[0].EnumerateObject().Count() == 2 && content[0].GetProperty("type").GetString() == "text")
                    writer.WriteString("content", content[0].GetProperty("text").GetString());

                else throw Invalid();

                if (message.TryGetProperty("tool_calls", out JsonElement calls))
                {
                    writer.WriteStartArray("tool_calls");

                    foreach (JsonElement call in calls.EnumerateArray())
                    {
                        writer.WriteStartObject();

                        foreach (JsonProperty property in call.EnumerateObject())
                        {
                            writer.WritePropertyName(property.Name);

                            if (!property.NameEquals("function")) property.Value.WriteTo(writer);

                            else
                            {
                                writer.WriteStartObject();

                                foreach (JsonProperty member in property.Value.EnumerateObject())
                                {
                                    writer.WritePropertyName(member.Name);

                                    if (!member.NameEquals("arguments")) member.Value.WriteTo(writer);

                                    else
                                    {
                                        // The pinned SDK pretty-prints the arguments JSON inside its string.
                                        // Normalize representation only; the sealed final bytes remain unchanged.
                                        using JsonDocument arguments = JsonDocument.Parse(member.Value.GetString()!);

                                        writer.WriteStringValue(Build(arguments.RootElement.WriteTo).GetRawText());
                                    }
                                }

                                writer.WriteEndObject();
                            }
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });

        if (!JsonElement.DeepEquals(normalized, _messages)) throw Invalid();

        if (HasTools)
        {
            if (!root.TryGetProperty("tools", out JsonElement tools) || !JsonElement.DeepEquals(tools, _tools)
                || root.TryGetProperty("tool_choice", out JsonElement choice) && choice.GetString() != "auto"
                || !root.TryGetProperty("parallel_tool_calls", out JsonElement parallel) || parallel.ValueKind != JsonValueKind.False) throw Invalid();
        }
        else if (root.TryGetProperty("tools", out _) || root.TryGetProperty("tool_choice", out _) || root.TryGetProperty("parallel_tool_calls", out _)) throw Invalid();
    }

    private static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer)) write(writer);

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());

        return document.RootElement.Clone();
    }

    private static InvalidOperationException Invalid() => new("The Guild text/function payload differs from its qualified closed protocol.");

    private sealed class FrozenFunction(JsonElement function) : AIFunction
    {
        public override string Name => function.GetProperty("name").GetString()!;

        public override string Description => function.GetProperty("description").GetString() ?? string.Empty;

        public override JsonElement JsonSchema => function.GetProperty("parameters");

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A physical wire descriptor grants no tool execution authority.");
    }
}
