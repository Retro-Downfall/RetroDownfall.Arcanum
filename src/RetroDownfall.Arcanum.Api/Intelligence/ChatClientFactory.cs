using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public interface IChatClientFactory
{

    Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken);

    /// <summary>
    /// Builds a lease for an explicit (provider, model) pair, bypassing <see cref="ProviderResolver"/>
    /// selection. Used by the resilience fallback loop in <c>WizardIntelligenceProvider</c> to target a
    /// specific fallback candidate after the first candidate fails connectivity. Dispatches to the same
    /// per-kind lease construction as <see cref="ResolveClientAsync(string?, CancellationToken)"/>.
    /// </summary>
    Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken);

}

/// <summary>
/// Per-request <see cref="IChatClient"/> built from <see cref="ArcanumSettings.Providers"/>. Reads <see cref="IOptionsMonitor{ArcanumSettings}.CurrentValue"/> only inside <see cref="ResolveClientAsync"/> for hot-reload safety.
/// </summary>
public sealed class ChatClientFactory(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ILlamaServerManager llamaServerManager,
    ConfigurationSecretProtector secretProtector,
    ILogger<ChatClientFactory> logger) : IChatClientFactory
{

    private const string OpenAiCompatibleHttpClientName = "OpenAiCompatibleProvider";

    private const string KeylessOpenAiPlaceholder = "no-key";

    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private const int MaxCachedEndpointClients = 32;

    private readonly ConcurrentDictionary<string, EndpointHttpClientEntry> _endpointHttpClients = new(StringComparer.Ordinal);

    private readonly object _endpointLock = new();

    private sealed class EndpointHttpClientEntry
    {

        public required HttpClient Client { get; init; }

        public int RefCount;

    }

    public async Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
    {

        // Hot-reload: read settings only here — never cache ArcanumSettings on the singleton factory.
        ArcanumSettings arc = optionsMonitor.CurrentValue;

        if (!ProviderResolver.TryResolveProviderForModel(arc, targetModel, out ProviderSettings? provider, out string resolvedModel)
            || provider is null)
        {
            throw new InvalidOperationException(
                "No AI model could be resolved. Configure Arcanum:Providers (with non-empty Models) and Arcanum:DefaultModel, or pass a model override that matches a configured model.");

        }

        return await ResolveClientAsync(provider, resolvedModel, cancellationToken).ConfigureAwait(false);

    }

    public async Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken) =>
        provider.Type switch
        {
            AiProviderKind.Ollama => CreateOllamaLease(provider, resolvedModel),
            AiProviderKind.OpenAICompatible => CreateOpenAiCompatibleLease(provider, resolvedModel),
            AiProviderKind.LlamaCppServer => await CreateLlamaCppLeaseAsync(provider, resolvedModel, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported provider type '{provider.Type}' for provider '{provider.Name}'."),
        };

    private ChatClientLease CreateOllamaLease(ProviderSettings provider, string resolvedModel)
    {

        (HttpClient http, string cacheKey) = AcquireEndpointHttpClient(provider.Endpoint);

        // OllamaSharp 5.4.25 owns an internal source-generated context
        // (OllamaSharp.Models.JsonSourceGenerationContext, not public) that it consults when this
        // argument is null. AOT publish is clean against that path; do not switch to a custom
        // context here without an upstream change that exposes the type.
        var ollama = new OllamaApiClient(http, resolvedModel, jsonSerializerContext: null);

        return new ChatClientLease(
            ollama,
            ollama,
            provider,
            resolvedModel,
            isOllama: true,
            ownedHttpClient: null,
            endpointCacheKey: cacheKey,
            endpointCacheOwner: this);

    }

    private ChatClientLease CreateOpenAiCompatibleLease(ProviderSettings provider, string resolvedModel)
    {

        string key = string.IsNullOrEmpty(provider.ApiKey)
            ? KeylessOpenAiPlaceholder
            : secretProtector.ResolveApiKey(provider.ApiKey) ?? KeylessOpenAiPlaceholder;

        var credential = new ApiKeyCredential(key);

        HttpClient http = httpClientFactory.CreateClient(OpenAiCompatibleHttpClientName);

        var options = new OpenAIClientOptions
        {

            Endpoint = new Uri(provider.Endpoint),

            Transport = new HttpClientPipelineTransport(http),

        };

        var chatClient = new ChatClient(resolvedModel, credential, options);

        IChatClient meAi = chatClient.AsIChatClient();

        return new ChatClientLease(meAi, ollamaApi: null, provider, resolvedModel, isOllama: false, ownedHttpClient: null);

    }

    private async Task<ChatClientLease> CreateLlamaCppLeaseAsync(
        ProviderSettings provider,
        string resolvedModel,
        CancellationToken cancellationToken)
    {

        string cacheKey = LlamaCacheKey.NormalizeModelKey(resolvedModel);

        string? sourceUrl = TryResolveModelMapUrl(provider, resolvedModel);

        Core.Primitives.Result<LlamaServerInfo> ensure = await llamaServerManager.EnsureServerAsync(
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

            (HttpClient http, string endpointCacheKey) = AcquireEndpointHttpClient(endpoint);

            var credential = new ApiKeyCredential(KeylessOpenAiPlaceholder);

            var options = new OpenAIClientOptions
            {

                Endpoint = new Uri(endpoint),

                Transport = new HttpClientPipelineTransport(http),

            };

            var chatClient = new ChatClient(resolvedModel, credential, options);

            IChatClient meAi = chatClient.AsIChatClient();

            return new ChatClientLease(
                meAi,
                ollamaApi: null,
                provider,
                resolvedModel,
                isOllama: false,
                ownedHttpClient: null,
                concurrencySlot: slot,
                endpointCacheKey: endpointCacheKey,
                endpointCacheOwner: this);

        }
        catch
        {

            slot?.Dispose();

            throw;

        }
    }

    private (HttpClient Client, string CacheKey) AcquireEndpointHttpClient(string endpoint)
    {

        string key = NormalizeEndpointKey(endpoint);

        lock (_endpointLock)
        {

            EndpointHttpClientEntry entry = _endpointHttpClients.GetOrAdd(
                key,
                static cacheKey => new EndpointHttpClientEntry
                {
                    Client = CreateEndpointHttpClient(cacheKey),
                    RefCount = 0,
                });

            Interlocked.Increment(ref entry.RefCount);

            EvictExcessEndpointClients();

            return (entry.Client, key);

        }

    }

    internal void ReleaseEndpointHttpClient(string cacheKey)
    {

        lock (_endpointLock)
        {

            if (!_endpointHttpClients.TryGetValue(cacheKey, out EndpointHttpClientEntry? entry))
            {

                return;

            }

            if (Interlocked.Decrement(ref entry.RefCount) == 0
                && _endpointHttpClients.Count > MaxCachedEndpointClients
                && _endpointHttpClients.TryRemove(cacheKey, out EndpointHttpClientEntry? removed)
                && Volatile.Read(ref removed.RefCount) == 0)
            {

                removed.Client.Dispose();

            }

        }

    }

    private void EvictExcessEndpointClients()
    {

        while (_endpointHttpClients.Count > MaxCachedEndpointClients)
        {

            KeyValuePair<string, EndpointHttpClientEntry>? victim = null;

            foreach (KeyValuePair<string, EndpointHttpClientEntry> pair in _endpointHttpClients)
            {

                if (Volatile.Read(ref pair.Value.RefCount) == 0)
                {

                    victim = pair;

                    break;

                }

            }

            if (victim is null)
            {

                break;

            }

            if (_endpointHttpClients.TryRemove(victim.Value.Key, out EndpointHttpClientEntry? removed)
                && Volatile.Read(ref removed.RefCount) == 0)
            {

                removed.Client.Dispose();

            }
            else if (removed is not null)
            {

                _endpointHttpClients.TryAdd(victim.Value.Key, removed);

                break;

            }

        }

        // W3.3 Fix 3: soft-cap operator signal. If the cache is still over
        // MaxCachedEndpointClients here, every remaining entry is leased (RefCount > 0)
        // and cannot be evicted without disrupting in-flight inference. Do NOT block or
        // refuse new endpoint keys — surface a warning naming the remaining over-cap
        // count (same shape as the W2.5b LRU-over-cap warning). A drain/wait is a
        // larger change and is intentionally out of scope for this fix.
        int overCapCount = _endpointHttpClients.Count - MaxCachedEndpointClients;

        if (overCapCount > 0)
        {

            logger.LogWarning(
                "Endpoint HttpClient cache is over cap by {OverCapCount} entries; all eviction candidates are currently leased and were not removed.",
                overCapCount);

        }

    }

    private static HttpClient CreateEndpointHttpClient(string endpointKey)
    {

        var handler = OutboundUrlGuard.CreateProviderEgressHandler();

        handler.PooledConnectionLifetime = PooledConnectionLifetime;

        return new HttpClient(handler, disposeHandler: true)
        {

            BaseAddress = new Uri(endpointKey),

            Timeout = Timeout.InfiniteTimeSpan,

        };

    }

    private static string NormalizeEndpointKey(string endpoint)
    {

        var uri = new Uri(endpoint, UriKind.Absolute);

        return uri.AbsoluteUri.TrimEnd('/');

    }

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
/// Owns a resolved <see cref="IChatClient"/> and related disposables for one inference turn.
/// </summary>
public sealed class ChatClientLease : IDisposable
{

    private readonly OllamaApiClient? _ollama;

    // Non-null only for per-lease HttpClient instances; cached endpoint clients are never stored here.
    private readonly HttpClient? _ownedHttpClient;

    private readonly IDisposable? _concurrencySlot;

    private readonly string? _endpointCacheKey;

    private readonly ChatClientFactory? _endpointCacheOwner;

    private bool _disposed;

    public ChatClientLease(
        IChatClient chatClient,
        IOllamaApiClient? ollamaApi,
        ProviderSettings provider,
        string resolvedModel,
        bool isOllama,
        HttpClient? ownedHttpClient,
        IDisposable? concurrencySlot = null,
        string? endpointCacheKey = null,
        ChatClientFactory? endpointCacheOwner = null)
    {

        ChatClient = chatClient;

        OllamaApi = ollamaApi;

        Provider = provider;

        ResolvedModel = resolvedModel;

        IsOllama = isOllama;

        _ollama = ollamaApi as OllamaApiClient;

        _ownedHttpClient = ownedHttpClient;

        _concurrencySlot = concurrencySlot;

        _endpointCacheKey = endpointCacheKey;

        _endpointCacheOwner = endpointCacheOwner;

    }

    public IChatClient ChatClient { get; }

    public IOllamaApiClient? OllamaApi { get; }

    public ProviderSettings Provider { get; }

    public string ResolvedModel { get; }

    public bool IsOllama { get; }

    public void Dispose()
    {

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (IsOllama)
        {
            _ollama?.Dispose();

            // Cached endpoint HttpClients are process-lifetime singletons; only dispose per-lease owned clients.
            _ownedHttpClient?.Dispose();
        }
        else
        {
            (ChatClient as IDisposable)?.Dispose();

            // Cached endpoint HttpClients are process-lifetime singletons; only dispose per-lease owned clients.
            _ownedHttpClient?.Dispose();
        }

        _concurrencySlot?.Dispose();

        if (_endpointCacheKey is not null && _endpointCacheOwner is not null)
        {

            _endpointCacheOwner.ReleaseEndpointHttpClient(_endpointCacheKey);

        }

    }

}
