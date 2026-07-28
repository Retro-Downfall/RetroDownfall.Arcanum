using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

// RAG Phase 1 — The Weave (shared embedding foundation). Mirrors the existing IChatClientFactory /
// ChatClientFactory (see ChatClientFactory.cs) pattern: a singleton factory that reads
// IOptionsMonitor<ArcanumSettings>.CurrentValue only inside ResolveGeneratorAsync (hot-reload safe),
// resolves the Arcanum:Embeddings:Provider by name via ProviderResolver, and builds an
// IEmbeddingGenerator<string, Embedding<float>> for OpenAI-compatible providers.
//
// AiProviderKind.OpenAICompatible providers go through the OpenAI-compatible EmbeddingClient against
// the provider's configured Endpoint — this covers any OpenAI-shaped embeddings API, including Ollama
// via its own OpenAI-compatible /v1 endpoint.
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
    ConfigurationSecretProtector secretProtector) : IEmbeddingGeneratorFactory
{

    private const string OpenAiCompatibleHttpClientName = "OpenAiCompatibleProvider";

    private const string KeylessOpenAiPlaceholder = "no-key";

    // OpenAICompatible generators are process-lifetime cached, keyed by provider/model/endpoint/credential
    // fingerprint — constructing the thin IEmbeddingGenerator wrapper has no per-call cost worth avoiding
    // beyond object churn.
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> _generators =
        new(StringComparer.Ordinal);

    public Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken)
    {

        _ = cancellationToken;

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

        if (provider.Type != AiProviderKind.OpenAICompatible)
        {
            throw new InvalidOperationException(
                $"Unsupported provider type '{provider.Type}' for embeddings provider '{provider.Name}'. Only {nameof(AiProviderKind.OpenAICompatible)} is supported.");

        }

        return Task.FromResult(CreateOpenAiCompatibleLease(provider, embeddings.Model));

    }

    private EmbeddingGeneratorLease CreateOpenAiCompatibleLease(ProviderSettings provider, string model)
    {

        string resolvedApiKey = ResolveApiKeyValue(provider);

        // Includes the endpoint and a credential fingerprint (not the raw secret — see
        // ComputeCredentialFingerprint) so an operator hot-reloading Arcanum:Providers[].Endpoint or
        // ApiKey for this same provider name/model actually takes effect: a cache key of just
        // "providerName::model" would keep serving a generator built against the OLD endpoint/key
        // forever, since GetOrAdd only ever calls the factory once per key.
        string cacheKey = CacheKey(provider, model, resolvedApiKey);

        IEmbeddingGenerator<string, Embedding<float>> generator = _generators.GetOrAdd(
            cacheKey,
            _ => BuildOpenAiCompatibleGenerator(provider, model, resolvedApiKey));

        return new EmbeddingGeneratorLease(generator, ownsGenerator: false);

    }

    private IEmbeddingGenerator<string, Embedding<float>> BuildOpenAiCompatibleGenerator(
        ProviderSettings provider,
        string model,
        string resolvedApiKey)
    {

        ApiKeyCredential credential = new(resolvedApiKey);

        HttpClient http = httpClientFactory.CreateClient(OpenAiCompatibleHttpClientName);

        var options = new OpenAIClientOptions
        {

            Endpoint = new Uri(provider.Endpoint),

            Transport = new HttpClientPipelineTransport(http),

        };

        var embeddingClient = new EmbeddingClient(model, credential, options);

        return embeddingClient.AsIEmbeddingGenerator();

    }

    private string ResolveApiKeyValue(ProviderSettings provider) =>
        string.IsNullOrEmpty(provider.ApiKey)
            ? KeylessOpenAiPlaceholder
            : secretProtector.ResolveApiKey(provider.ApiKey) ?? KeylessOpenAiPlaceholder;

    private static string CacheKey(ProviderSettings provider, string model, string resolvedApiKey) =>
        $"{provider.Name}::{model}::{provider.Endpoint}::{ComputeCredentialFingerprint(resolvedApiKey)}";

    /// <summary>
    /// A one-way fingerprint (never the raw secret itself) so the cache key changes whenever the
    /// resolved API key changes, without holding the plaintext credential in an additional place
    /// (a dictionary key that could end up in a debugger view, dump, or an incidental log/exception
    /// message referencing the key).
    /// </summary>
    private static string ComputeCredentialFingerprint(string resolvedApiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resolvedApiKey)));

}

/// <summary>
/// Owns a resolved <see cref="IEmbeddingGenerator{String, Embedding}"/> for one embedding call (or
/// batch of calls within one turn). OpenAICompatible generators are process-lifetime cached and
/// neither owned nor disposed by the lease (<c>ownsGenerator: false</c>).
/// </summary>
public sealed class EmbeddingGeneratorLease : IDisposable
{

    private readonly bool _ownsGenerator;

    private bool _disposed;

    internal EmbeddingGeneratorLease(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        bool ownsGenerator)
    {

        Generator = generator;

        _ownsGenerator = ownsGenerator;

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

    }

}
