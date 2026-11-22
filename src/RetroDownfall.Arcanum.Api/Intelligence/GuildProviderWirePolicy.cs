using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Seals the actual SDK transport and the final text-only request before physical admission.</summary>
internal sealed class GuildProviderWirePolicy(ProviderSettings provider, string model) : PipelinePolicy
{
    private static readonly ConditionalWeakTable<IChatClient, GuildProviderWirePolicy> Clients = new();

    private static readonly AsyncLocal<PreparedCall?> Active = new();

    private readonly ProviderSettings _provider = PromptCacheClientIdentity.Freeze(provider);

    private readonly string _model = model;

    internal void Bind(IChatClient client) => Clients.Add(client, this);

    internal static PreparedCall? Prepare(IChatClient client, string providerName, string modelName,
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction> functions, int? maxOutput)
    {
        if (!Clients.TryGetValue(client, out GuildProviderWirePolicy? policy)) return null;

        if (policy._provider.Name != providerName || policy._model != modelName)
            throw new InvalidOperationException("The Guild transport identity differs from the accepted model.");

        return new(policy, string.Empty, maxOutput, new(messages, functions));
    }

    internal static PreparedCall? Prepare(IChatClient client, string providerName, string modelName,
        string prompt, int? maxOutput)
    {
        if (!Clients.TryGetValue(client, out GuildProviderWirePolicy? policy)) return null;

        if (policy._provider.Name != providerName || policy._model != modelName)
            throw new InvalidOperationException("The Guild transport identity differs from the accepted model.");

        return new(policy, prompt, maxOutput);
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);

        ProcessNext(message, pipeline, currentIndex);

        Active.Value?.ObserveResponse(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);

        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);

        Active.Value?.ObserveResponse(message);
    }

    private void Apply(PipelineMessage message)
    {
        PreparedCall? call = Active.Value;

        if (call is null && !GuildMemoryAccess.IsGuildInvocation) return;

        if (call is null || !ReferenceEquals(call.Policy, this))
            throw new InvalidOperationException("The Guild HTTP request has no matching live physical admission.");

        call.Apply(message);
    }

    internal sealed class PreparedCall
    {
        private readonly byte[] _payload;

        private readonly string _prompt;

        private readonly GuildProviderPayload? _structured;

        private readonly int? _maxOutput;

        private readonly string _limitField;

        private readonly Uri _endpoint;

        private int _entered;

        private int _closed;

        private int _sent;

        internal GuildProviderWirePolicy Policy { get; }

        internal int? MaxOutputTokens => _maxOutput;

        internal bool UsageObserved { get; private set; }

        internal bool InvalidUsage { get; private set; }

        internal long? ObservedInputTokens { get; private set; }

        internal long? ObservedOutputTokens { get; private set; }

        internal long? ObservedCachedTokens { get; private set; }

        internal long? ObservedReasoningTokens { get; private set; }

        internal PreparedCall(GuildProviderWirePolicy policy, string prompt, int? maxOutput,
            GuildProviderPayload? structured = null)
        {
            Policy = policy;

            _structured = structured;

            _prompt = prompt;

            _limitField = policy._provider.OutputTokenLimitWireDialect == OutputTokenLimitWireDialect.MaxTokens
                ? "max_tokens" : "max_completion_tokens";

            _endpoint = new Uri(policy._provider.Endpoint.TrimEnd('/') + "/chat/completions", UriKind.Absolute);

            _maxOutput = maxOutput;

            using MemoryStream buffer = new();

            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();

                writer.WriteString("model", policy._model);

                if (structured is not null)
                {
                    structured.Write(writer);
                }
                else
                {
                    writer.WriteStartArray("messages");

                    writer.WriteStartObject();

                    writer.WriteString("role", "user");

                    writer.WriteString("content", prompt);

                    writer.WriteEndObject();

                    writer.WriteEndArray();
                }

                if (_maxOutput is { } maximum) writer.WriteNumber(_limitField, maximum);

                writer.WriteNumber("n", 1);

                writer.WriteBoolean("stream", false);

                writer.WriteEndObject();
            }

            _payload = buffer.ToArray();
        }

        internal IDisposable Enter()
        {
            if (Interlocked.Exchange(ref _entered, 1) != 0)
                throw new InvalidOperationException("A physical Guild payload cannot be dispatched twice.");

            PreparedCall? previous = Active.Value;

            Active.Value = this;

            return new Scope(this, previous);
        }

        internal void Apply(PipelineMessage message)
        {
            if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _entered) == 0
                || Interlocked.Exchange(ref _sent, 1) != 0 || message.Request.Method != "POST"
                || message.Request.Uri != _endpoint || message.Request.Content is null)
                throw new InvalidOperationException("The Guild transport request differs from its single admitted payload.");

            using MemoryStream buffer = new();

            message.Request.Content.WriteTo(buffer, message.CancellationToken);

            using JsonDocument document = JsonDocument.Parse(buffer.ToArray());

            JsonElement root = document.RootElement;

            HashSet<string> names = new(StringComparer.Ordinal);

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!names.Add(property.Name) || property.Name is not ("model" or "messages" or "stream" or "n" or "max_tokens" or "max_completion_tokens")
                        && !(_structured?.HasTools == true && property.Name is "tools" or "tool_choice" or "parallel_tool_calls")
                    || property.Name is "max_tokens" or "max_completion_tokens" && property.Name != _limitField)
                    throw new InvalidOperationException("The Guild SDK request contains an unqualified payload field.");
            }

            if (root.GetProperty("model").GetString() != Policy._model
                || root.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind != JsonValueKind.False
                || root.TryGetProperty("n", out JsonElement n) && n.GetInt32() != 1
                || (_maxOutput is { } max ? !root.TryGetProperty(_limitField, out JsonElement limit) || limit.GetInt32() != max
                    : root.TryGetProperty(_limitField, out _)))
                throw new InvalidOperationException("The Guild SDK request changed its model or output bound.");

            if (_structured is not null)
            {
                _structured.Validate(root);
            }
            else
            {
                JsonElement messages = root.GetProperty("messages");

                if (messages.GetArrayLength() != 1) throw new InvalidOperationException("The Guild SDK request changed its messages.");

                JsonElement user = messages[0];

                if (user.EnumerateObject().Count() != 2 || user.GetProperty("role").GetString() != "user")
                    throw new InvalidOperationException("The Guild SDK request changed its message authority.");

                JsonElement content = user.GetProperty("content");

                bool exact = content.ValueKind == JsonValueKind.String ? content.GetString() == _prompt
                    : content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 1
                        && content[0].EnumerateObject().Count() == 2 && content[0].GetProperty("type").GetString() == "text"
                        && content[0].GetProperty("text").GetString() == _prompt;

                if (!exact) throw new InvalidOperationException("The Guild SDK request changed its accepted text.");
            }

            // Send only the exact payload frozen before the effect was admitted.
            message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(_payload));
        }

        internal void ObserveResponse(PipelineMessage message)
        {
            // The SDK buffers non-streaming responses already. Do not force a second
            // buffer or retain the response: inspect only its usage facts in place.
            if (!message.BufferResponse || message.Response is not { } response || response.IsError) return;

            try
            {
                Utf8JsonReader reader = new(response.Content.ToMemory().Span);

                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { InvalidUsage = true; return; }

                bool foundUsage = false;

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) { InvalidUsage = true; return; }

                    bool usage = reader.ValueTextEquals("usage"u8);

                    if (!reader.Read()) { InvalidUsage = true; return; }

                    if (!usage) { reader.Skip(); continue; }

                    if (foundUsage) { InvalidUsage = true; return; }

                    foundUsage = true;

                    if (reader.TokenType == JsonTokenType.Null) continue;

                    if (reader.TokenType != JsonTokenType.StartObject) { InvalidUsage = true; return; }

                    bool inputSeen = false, outputSeen = false, inputDetailsSeen = false, outputDetailsSeen = false;

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName) { InvalidUsage = true; return; }

                        bool input = reader.ValueTextEquals("prompt_tokens"u8);
                        bool output = reader.ValueTextEquals("completion_tokens"u8);
                        bool inputDetails = reader.ValueTextEquals("prompt_tokens_details"u8);
                        bool outputDetails = reader.ValueTextEquals("completion_tokens_details"u8);

                        if (!reader.Read()) { InvalidUsage = true; return; }

                        if (inputDetails || outputDetails)
                        {
                            if (inputDetails && inputDetailsSeen || outputDetails && outputDetailsSeen
                                || !ReadUsageDetail(ref reader, inputDetails, out long? detail)) { InvalidUsage = true; return; }

                            if (inputDetails) { inputDetailsSeen = true; ObservedCachedTokens = detail; }
                            else { outputDetailsSeen = true; ObservedReasoningTokens = detail; }

                            continue;
                        }

                        if (!input && !output) { reader.Skip(); continue; }

                        if (input && inputSeen || output && outputSeen || reader.TokenType != JsonTokenType.Number
                            || !reader.TryGetInt64(out long value) || value < 0) { InvalidUsage = true; return; }

                        if (input) { inputSeen = true; ObservedInputTokens = value; }
                        else { outputSeen = true; ObservedOutputTokens = value; }
                    }

                    if (reader.TokenType != JsonTokenType.EndObject) { InvalidUsage = true; return; }
                }

                if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) { InvalidUsage = true; return; }

                UsageObserved = true;
            }
            catch (JsonException)
            {
                InvalidUsage = true;
            }
            catch (InvalidOperationException)
            {
                // No in-memory response is available at this pipeline stage. Its
                // usage remains unknown; the transport is never read a second time.
            }
        }

        private static bool ReadUsageDetail(ref Utf8JsonReader reader, bool input, out long? value)
        {
            value = null;

            if (reader.TokenType == JsonTokenType.Null) return true;

            if (reader.TokenType != JsonTokenType.StartObject) return false;

            bool seen = false;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                bool relevant = input ? reader.ValueTextEquals("cached_tokens"u8) : reader.ValueTextEquals("reasoning_tokens"u8);

                if (!reader.Read()) return false;

                if (!relevant) { reader.Skip(); continue; }

                if (seen) return false;

                seen = true;

                if (reader.TokenType == JsonTokenType.Null) continue;

                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long count) || count < 0) return false;

                value = count;
            }

            return reader.TokenType == JsonTokenType.EndObject;
        }

        private sealed class Scope(PreparedCall call, PreparedCall? previous) : IDisposable
        {
            public void Dispose()
            {
                Interlocked.Exchange(ref call._closed, 1);

                Active.Value = previous;
            }
        }
    }
}
