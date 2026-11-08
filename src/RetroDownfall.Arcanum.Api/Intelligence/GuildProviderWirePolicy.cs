using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction> functions, int? maxOutput, ModelPricingEntry configured)
    {
        if (!Clients.TryGetValue(client, out GuildProviderWirePolicy? policy)) return null;

        if (policy._provider.Name != providerName || policy._model != modelName)
            throw new InvalidOperationException("The Guild transport identity differs from the accepted model.");

        return new(policy, string.Empty, maxOutput, configured, new(messages, functions));
    }

    internal static PreparedCall? Prepare(IChatClient client, string providerName, string modelName,
        string prompt, int? maxOutput, ModelPricingEntry configured)
    {
        if (!Clients.TryGetValue(client, out GuildProviderWirePolicy? policy)) return null;

        if (policy._provider.Name != providerName || policy._model != modelName)
            throw new InvalidOperationException("The Guild transport identity differs from the accepted model.");

        return new(policy, prompt, maxOutput, configured);
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);

        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);

        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
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
        // Code-owned proof, never inferred from configurable context windows or token estimates.
        // https://developers.openai.com/api/docs/models/gpt-5.4-mini
        // https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create
        private const string ProofVersion = "openai-chat-text-default-v1-2026-11-08";

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

        internal decimal? UsdBound { get; }

        internal long? TokenBound { get; }

        internal int? MaxOutputTokens => _maxOutput;

        internal ModelPricingEntry Pricing { get; }

        internal string ProofIdentity { get; }

        internal PreparedCall(GuildProviderWirePolicy policy, string prompt, int? maxOutput, ModelPricingEntry configured,
            GuildProviderPayload? structured = null)
        {
            Policy = policy;

            _structured = structured;

            _prompt = prompt;

            _limitField = policy._provider.OutputTokenLimitWireDialect == OutputTokenLimitWireDialect.MaxTokens
                ? "max_tokens" : "max_completion_tokens";

            _endpoint = new Uri(policy._provider.Endpoint.TrimEnd('/') + "/chat/completions", UriKind.Absolute);

            Uri endpoint = new(policy._provider.Endpoint, UriKind.Absolute);

            bool qualified = policy._model == "gpt-5.4-mini-2026-03-17"
                && endpoint.Scheme == "https" && endpoint.Host == "api.openai.com" && endpoint.Port == 443
                && endpoint.AbsolutePath is "/v1" or "/v1/" && endpoint.Query.Length == 0
                && endpoint.Fragment.Length == 0 && endpoint.UserInfo.Length == 0
                && _limitField == "max_completion_tokens" && maxOutput is null or > 0;

            int? physicalOutput = qualified ? Math.Min(maxOutput ?? 128000, 128000) : maxOutput;

            _maxOutput = physicalOutput;

            Pricing = qualified ? new()
            {
                InputPer1M = Math.Max(0.75m, configured.InputPer1M),
                CachedPer1M = Math.Max(0.075m, configured.CachedPer1M),
                OutputPer1M = Math.Max(4.50m, configured.OutputPer1M),
                ReasoningPer1M = Math.Max(4.50m, configured.ReasoningPer1M ?? configured.OutputPer1M),
            } : configured with { };

            if (qualified)
            {
                TokenBound = 400000L + physicalOutput!.Value;

                try
                {
                    UsdBound = checked(400000m * Math.Max(Pricing.InputPer1M, Pricing.CachedPer1M)
                        + physicalOutput.Value * Math.Max(Pricing.OutputPer1M, Pricing.ReasoningPer1M!.Value)) / 1000000m;
                }
                catch (OverflowException)
                {
                    // Token proof remains valid; this price is not representable and cannot authorize finite USD.
                    UsdBound = null;
                }
            }

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

                if (physicalOutput is { } maximum) writer.WriteNumber(_limitField, maximum);

                writer.WriteNumber("n", 1);

                writer.WriteBoolean("stream", false);

                if (qualified) writer.WriteString("service_tier", "default");

                writer.WriteEndObject();
            }

            _payload = buffer.ToArray();

            ProofIdentity = $"{(qualified ? ProofVersion : "unqualified-text-v1")}:{(structured?.HasTools == true ? "functions-v1" : "text")}:{Convert.ToHexString(SHA256.HashData(_payload))}";
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

            // These final bytes (including default service tier) were frozen and hashed before budget admission.
            message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(_payload));
        }

        internal string PricingSnapshotJson()
        {
            using MemoryStream buffer = new();

            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();

                writer.WriteNumber("inputPer1M", Pricing.InputPer1M);

                writer.WriteNumber("outputPer1M", Pricing.OutputPer1M);

                writer.WriteNumber("cachedPer1M", Pricing.CachedPer1M);

                if (Pricing.ReasoningPer1M is { } reasoning) writer.WriteNumber("reasoningPer1M", reasoning);

                writer.WriteString("guildProof", ProofIdentity);

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
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
