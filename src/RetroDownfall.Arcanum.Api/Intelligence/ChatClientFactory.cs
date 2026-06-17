using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
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

}

/// <summary>
/// Per-request <see cref="IChatClient"/> built from <see cref="ArcanumSettings.Providers"/>. Reads <see cref="IOptionsMonitor{ArcanumSettings}.CurrentValue"/> only inside <see cref="ResolveClientAsync"/> for hot-reload safety.
/// </summary>
public sealed class ChatClientFactory(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ILlamaServerManager llamaServerManager,
    ConfigurationSecretProtector secretProtector) : IChatClientFactory
{

    private const string OpenAiCompatibleHttpClientName = "OpenAiCompatibleProvider";

    private const string KeylessOpenAiPlaceholder = "no-key";

    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private const int MaxCachedEndpointClients = 32;

    private readonly ConcurrentDictionary<string, HttpClient> _endpointHttpClients = new(StringComparer.Ordinal);

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

        return provider.Type switch
        {
            AiProviderKind.Ollama => CreateOllamaLease(provider, resolvedModel),
            AiProviderKind.OpenAICompatible => CreateOpenAiCompatibleLease(provider, resolvedModel),
            AiProviderKind.LlamaCppServer => await CreateLlamaCppLeaseAsync(provider, resolvedModel, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported provider type '{provider.Type}' for provider '{provider.Name}'."),
        };

    }

    private ChatClientLease CreateOllamaLease(ProviderSettings provider, string resolvedModel)
    {

        HttpClient http = GetOrCreateEndpointHttpClient(provider.Endpoint);

        // OllamaSharp 5.4.25 owns an internal source-generated context
        // (OllamaSharp.Models.JsonSourceGenerationContext, not public) that it consults when this
        // argument is null. AOT publish is clean against that path; do not switch to a custom
        // context here without an upstream change that exposes the type.
        var ollama = new OllamaApiClient(http, resolvedModel, jsonSerializerContext: null);

        return new ChatClientLease(ollama, ollama, provider, resolvedModel, isOllama: true, ownedHttpClient: null);

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

            HttpClient http = GetOrCreateEndpointHttpClient(endpoint);

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
                concurrencySlot: slot);

        }
        catch
        {

            slot?.Dispose();

            throw;

        }
    }

    private HttpClient GetOrCreateEndpointHttpClient(string endpoint)
    {

        string key = NormalizeEndpointKey(endpoint);

        if (_endpointHttpClients.TryGetValue(key, out HttpClient? existing))
        {

            return existing;

        }

        HttpClient created = CreateEndpointHttpClient(key);

        if (_endpointHttpClients.TryAdd(key, created))
        {

            EvictExcessEndpointClients();

            return created;

        }

        created.Dispose();

        return _endpointHttpClients[key];

    }

    private void EvictExcessEndpointClients()
    {

        while (_endpointHttpClients.Count > MaxCachedEndpointClients)
        {

            string? victimKey = _endpointHttpClients.Keys.FirstOrDefault();

            if (victimKey is null)
            {

                break;

            }

            if (_endpointHttpClients.TryRemove(victimKey, out HttpClient? victim))
            {

                victim.Dispose();

            }

        }

    }

    private static HttpClient CreateEndpointHttpClient(string endpointKey)
    {

        var handler = new SocketsHttpHandler
        {

            PooledConnectionLifetime = PooledConnectionLifetime,

            AllowAutoRedirect = false,

        };

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

    private bool _disposed;

    public ChatClientLease(
        IChatClient chatClient,
        IOllamaApiClient? ollamaApi,
        ProviderSettings provider,
        string resolvedModel,
        bool isOllama,
        HttpClient? ownedHttpClient,
        IDisposable? concurrencySlot = null)
    {

        ChatClient = chatClient;

        OllamaApi = ollamaApi;

        Provider = provider;

        ResolvedModel = resolvedModel;

        IsOllama = isOllama;

        _ollama = ollamaApi as OllamaApiClient;

        _ownedHttpClient = ownedHttpClient;

        _concurrencySlot = concurrencySlot;

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

    }

}
