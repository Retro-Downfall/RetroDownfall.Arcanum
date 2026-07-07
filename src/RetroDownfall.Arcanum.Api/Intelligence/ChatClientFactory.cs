using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    ILogger<ChatClientFactory> logger,
    ILogger<LlamaCppRequestAugmentingHandler> llamaHandlerLogger,
    InferenceTokenizerResolver tokenizerResolver) : IChatClientFactory
{

    private const string OpenAiCompatibleHttpClientName = "OpenAiCompatibleProvider";

    private const string KeylessOpenAiPlaceholder = "no-key";

    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private const int MaxCachedEndpointClients = 32;

    /// <summary>
    /// Hard backpressure ceiling: when every eviction candidate is currently leased (RefCount > 0)
    /// and the cache has grown to this many distinct endpoints, new endpoints are refused outright
    /// rather than allowed to grow without bound. Set well above <see cref="MaxCachedEndpointClients"/>
    /// so normal soft-cap operation (logged warnings, eviction of idle entries) is never affected —
    /// this only trips when the soft cap has been persistently exceeded by leased entries alone.
    /// </summary>
    private const int HardCapEndpointClients = MaxCachedEndpointClients * 4;

    private readonly ConcurrentDictionary<string, EndpointHttpClientEntry> _endpointHttpClients = new(StringComparer.Ordinal);

    private readonly object _endpointLock = new();

    private long _endpointAccessCounter;

    private sealed class EndpointHttpClientEntry
    {

        public required HttpClient Client { get; init; }

        public int RefCount;

        /// <summary>
        /// Monotonic access counter (not a wall-clock timestamp — cheaper and immune to clock
        /// adjustments) updated on every acquire, so <see cref="EvictExcessEndpointClients"/> can
        /// evict the true least-recently-used idle entry instead of an arbitrary one.
        /// </summary>
        public long LastAccessed;

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
            AiProviderKind.OpenAICompatible => CreateOpenAiCompatibleLease(provider, resolvedModel),
            AiProviderKind.LlamaCppServer => await CreateLlamaCppLeaseAsync(provider, resolvedModel, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported provider type '{provider.Type}' for provider '{provider.Name}'."),
        };

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

        return new ChatClientLease(meAi, provider, resolvedModel, ownedHttpClient: null);

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
                provider,
                resolvedModel,
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

            if (!_endpointHttpClients.ContainsKey(key)
                && _endpointHttpClients.Count >= HardCapEndpointClients)
            {

                // Real backpressure: unlike the soft-cap warning below (which only logs), refuse a
                // brand-new endpoint outright once the cache has grown this large with every existing
                // entry still leased — growing without bound would otherwise accumulate one pooled
                // HttpClient/handler (and its connections) per distinct llama-server endpoint forever.
                throw new InvalidOperationException(
                    $"Too many distinct llama-server endpoints are concurrently in use ({_endpointHttpClients.Count} >= {HardCapEndpointClients}); rejecting a new endpoint client until existing ones are released.");

            }

            EndpointHttpClientEntry entry = _endpointHttpClients.GetOrAdd(
                key,
                cacheKey => new EndpointHttpClientEntry
                {
                    Client = CreateEndpointHttpClient(cacheKey),
                    RefCount = 0,
                });

            Interlocked.Increment(ref entry.RefCount);

            entry.LastAccessed = ++_endpointAccessCounter;

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

            // True LRU: scan every idle (RefCount == 0) entry and keep the one with the smallest
            // LastAccessed counter, rather than evicting the first idle entry ConcurrentDictionary
            // happens to enumerate first (an arbitrary, implementation-defined order unrelated to
            // actual usage recency).
            foreach (KeyValuePair<string, EndpointHttpClientEntry> pair in _endpointHttpClients)
            {

                if (Volatile.Read(ref pair.Value.RefCount) != 0)
                {

                    continue;

                }

                if (victim is null || pair.Value.LastAccessed < victim.Value.Value.LastAccessed)
                {

                    victim = pair;

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

    private HttpClient CreateEndpointHttpClient(string endpointKey)
    {

        var egressHandler = OutboundUrlGuard.CreateProviderEgressHandler();

        egressHandler.PooledConnectionLifetime = PooledConnectionLifetime;

        var augmentingHandler = new LlamaCppRequestAugmentingHandler(optionsMonitor, llamaHandlerLogger, tokenizerResolver)
        {

            InnerHandler = egressHandler

        };

        return new HttpClient(augmentingHandler, disposeHandler: true)
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

    // Non-null only for per-lease HttpClient instances; cached endpoint clients are never stored here.
    private readonly HttpClient? _ownedHttpClient;

    private readonly IDisposable? _concurrencySlot;

    private readonly string? _endpointCacheKey;

    private readonly ChatClientFactory? _endpointCacheOwner;

    private bool _disposed;

    public ChatClientLease(
        IChatClient chatClient,
        ProviderSettings provider,
        string resolvedModel,
        HttpClient? ownedHttpClient,
        IDisposable? concurrencySlot = null,
        string? endpointCacheKey = null,
        ChatClientFactory? endpointCacheOwner = null)
    {

        ChatClient = chatClient;

        Provider = provider;

        ResolvedModel = resolvedModel;

        _ownedHttpClient = ownedHttpClient;

        _concurrencySlot = concurrencySlot;

        _endpointCacheKey = endpointCacheKey;

        _endpointCacheOwner = endpointCacheOwner;

    }

    public IChatClient ChatClient { get; }

    public ProviderSettings Provider { get; }

    public string ResolvedModel { get; }

    public void Dispose()
    {

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        (ChatClient as IDisposable)?.Dispose();

        // Cached endpoint HttpClients are process-lifetime singletons; only dispose per-lease owned clients.
        _ownedHttpClient?.Dispose();

        _concurrencySlot?.Dispose();

        if (_endpointCacheKey is not null && _endpointCacheOwner is not null)
        {

            _endpointCacheOwner.ReleaseEndpointHttpClient(_endpointCacheKey);

        }

    }

}
