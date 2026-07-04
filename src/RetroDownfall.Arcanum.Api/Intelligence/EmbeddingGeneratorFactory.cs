using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

// RAG Phase 1 — The Weave (shared embedding foundation). Mirrors the existing IChatClientFactory /
// ChatClientFactory (see ChatClientFactory.cs) pattern: a singleton factory that reads
// IOptionsMonitor<ArcanumSettings>.CurrentValue only inside ResolveGeneratorAsync (hot-reload safe),
// resolves the Arcanum:Embeddings:Provider by name via ProviderResolver, and builds an
// IEmbeddingGenerator<string, Embedding<float>> per AiProviderKind.
//
// Clarified scope (per explicit direction, superseding the original RAG spec's Ollama-specific
// OllamaSharp plan): Ollama is not given any bespoke embedding integration. AiProviderKind.Ollama is
// treated identically to AiProviderKind.OpenAICompatible — both go through the OpenAI-compatible
// EmbeddingClient against the provider's configured Endpoint. Operators pointing an Ollama provider at
// this factory must configure Endpoint as Ollama's OpenAI-compatible base (typically ending in /v1).
// AiProviderKind.LlamaCppServer keeps its dedicated lifecycle: EnsureServerAsync + AcquireSlotAsync
// against the locally spawned llama-server, then the same OpenAI-compatible client shape against the
// resolved dynamic endpoint.
public interface IEmbeddingGeneratorFactory
{

    Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken);

}

/// <summary>
/// Builds and caches <see cref="IEmbeddingGenerator{String, Embedding}"/> instances from
/// <c>Arcanum:Embeddings</c>. Reads <see cref="IOptionsMonitor{ArcanumSettings}.CurrentValue"/> only
/// inside <see cref="ResolveGeneratorAsync"/> for hot-reload safety — never cached on the singleton
/// itself.
/// </summary>
public sealed class EmbeddingGeneratorFactory(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ILlamaServerManager llamaServerManager,
    ConfigurationSecretProtector secretProtector) : IEmbeddingGeneratorFactory
{

    private const string OpenAiCompatibleHttpClientName = "OpenAiCompatibleProvider";

    private const string KeylessOpenAiPlaceholder = "no-key";

    // Ollama and OpenAICompatible generators are process-lifetime cached, keyed by "providerName::model"
    // — the same shape ChatClientFactory's endpoint HttpClient cache uses, applied here to the thin
    // IEmbeddingGenerator wrapper itself since constructing it has no per-call cost worth avoiding
    // beyond object churn. LlamaCppServer generators are NOT cached here (see CreateLlamaCppLeaseAsync)
    // because the server's dynamic endpoint can change across restarts, mirroring ChatClientFactory's
    // LlamaCpp lease, which likewise builds a fresh client per call.
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> _generators =
        new(StringComparer.Ordinal);

    public async Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken)
    {

        ArcanumSettings arc = optionsMonitor.CurrentValue;

        EmbeddingSettings embeddings = arc.Embeddings ?? new EmbeddingSettings();

        if (!embeddings.Enabled)
        {
            throw new InvalidOperationException(
                "Embeddings are disabled (Arcanum:Embeddings:Enabled is false).");

        }

        if (string.IsNullOrWhiteSpace(embeddings.Provider) || string.IsNullOrWhiteSpace(embeddings.Model))
        {
            throw new InvalidOperationException(
                "Arcanum:Embeddings:Provider and Arcanum:Embeddings:Model must both be configured.");

        }

        if (!ProviderResolver.TryResolveProviderByName(arc, embeddings.Provider, out ProviderSettings? provider)
            || provider is null)
        {
            throw new InvalidOperationException(
                $"Arcanum:Embeddings:Provider '{embeddings.Provider}' does not match any configured provider.");

        }

        string model = embeddings.Model;

        return provider.Type == AiProviderKind.LlamaCppServer
            ? await CreateLlamaCppLeaseAsync(provider, model, cancellationToken).ConfigureAwait(false)
            : CreateOpenAiStyleLease(provider, model);

    }

    private EmbeddingGeneratorLease CreateOpenAiStyleLease(ProviderSettings provider, string model)
    {

        string cacheKey = CacheKey(provider.Name, model);

        IEmbeddingGenerator<string, Embedding<float>> generator = _generators.GetOrAdd(
            cacheKey,
            _ => BuildOpenAiStyleGenerator(provider, model));

        return new EmbeddingGeneratorLease(generator, ownsGenerator: false);

    }

    private IEmbeddingGenerator<string, Embedding<float>> BuildOpenAiStyleGenerator(ProviderSettings provider, string model)
    {

        ApiKeyCredential credential = ResolveCredential(provider);

        HttpClient http = httpClientFactory.CreateClient(OpenAiCompatibleHttpClientName);

        var options = new OpenAIClientOptions
        {

            Endpoint = new Uri(provider.Endpoint),

            Transport = new HttpClientPipelineTransport(http),

        };

        var embeddingClient = new EmbeddingClient(model, credential, options);

        return embeddingClient.AsIEmbeddingGenerator();

    }

    private async Task<EmbeddingGeneratorLease> CreateLlamaCppLeaseAsync(
        ProviderSettings provider,
        string model,
        CancellationToken cancellationToken)
    {

        string cacheKey = LlamaCacheKey.NormalizeModelKey(model);

        string? sourceUrl = TryResolveModelMapUrl(provider, model);

        Result<LlamaServerInfo> ensure = await llamaServerManager.EnsureServerAsync(
            cacheKey,
            sourceUrl,
            gpuLayersOverride: null,
            portOverride: null,
            cancellationToken).ConfigureAwait(false);

        if (ensure.IsFailure)
        {
            throw new InvalidOperationException(ensure.Error.Message);

        }

        IDisposable? slot = null;

        try
        {

            slot = await llamaServerManager.AcquireSlotAsync(cacheKey, cancellationToken).ConfigureAwait(false);

            string endpoint = ensure.Value.Endpoint;

            ApiKeyCredential credential = new(KeylessOpenAiPlaceholder);

            HttpClient http = httpClientFactory.CreateClient(OpenAiCompatibleHttpClientName);

            var options = new OpenAIClientOptions
            {

                Endpoint = new Uri(endpoint),

                Transport = new HttpClientPipelineTransport(http),

            };

            var embeddingClient = new EmbeddingClient(model, credential, options);

            IEmbeddingGenerator<string, Embedding<float>> generator = embeddingClient.AsIEmbeddingGenerator();

            return new EmbeddingGeneratorLease(generator, ownsGenerator: true, slot: slot);

        }
        catch
        {

            slot?.Dispose();

            throw;

        }

    }

    private ApiKeyCredential ResolveCredential(ProviderSettings provider)
    {

        string key = string.IsNullOrEmpty(provider.ApiKey)
            ? KeylessOpenAiPlaceholder
            : secretProtector.ResolveApiKey(provider.ApiKey) ?? KeylessOpenAiPlaceholder;

        return new ApiKeyCredential(key);

    }

    private static string CacheKey(string providerName, string model) => providerName + "::" + model;

    private static string? TryResolveModelMapUrl(ProviderSettings provider, string resolvedModel)
    {

        Dictionary<string, string>? map = provider.LlamaCpp?.ModelMap;

        if (map is null || map.Count == 0)
        {
            return null;

        }

        foreach (KeyValuePair<string, string> pair in map)
        {
            if (string.Equals(pair.Key, resolvedModel, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;

            }

        }

        return null;

    }

}

/// <summary>
/// Owns a resolved <see cref="IEmbeddingGenerator{String, Embedding}"/> for one embedding call (or
/// batch of calls within one turn). Disposal order mirrors <c>ChatClientLease</c>: dispose the
/// generator only if this lease owns it (LlamaCppServer — freshly built per lease, never cached), then
/// release the concurrency slot last. Ollama/OpenAICompatible generators are process-lifetime cached
/// and neither owned nor disposed by the lease.
/// </summary>
public sealed class EmbeddingGeneratorLease : IDisposable
{

    private readonly bool _ownsGenerator;

    private readonly IDisposable? _slot;

    private bool _disposed;

    internal EmbeddingGeneratorLease(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        bool ownsGenerator,
        IDisposable? slot = null)
    {

        Generator = generator;

        _ownsGenerator = ownsGenerator;

        _slot = slot;

    }

    public IEmbeddingGenerator<string, Embedding<float>> Generator { get; }

    public void Dispose()
    {

        if (_disposed)
        {
            return;

        }

        _disposed = true;

        if (_ownsGenerator)
        {
            Generator.Dispose();

        }

        _slot?.Dispose();

    }

}
