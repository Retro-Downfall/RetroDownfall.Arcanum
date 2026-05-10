using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public interface IChatClientFactory
{

    ChatClientLease ResolveClient(string? targetModel);

}

/// <summary>
/// Per-request <see cref="IChatClient"/> built from <see cref="ArcanumSettings.Providers"/>. Reads <see cref="IOptionsMonitor{ArcanumSettings}.CurrentValue"/> only inside <see cref="ResolveClient"/> for hot-reload safety.
/// </summary>
public sealed class ChatClientFactory(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor) : IChatClientFactory
{

    private const string OllamaHttpClientName = "OllamaProvider";

    private const string KeylessOpenAiPlaceholder = "no-key";

    public ChatClientLease ResolveClient(string? targetModel)
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
            _ => throw new InvalidOperationException($"Unsupported provider type '{provider.Type}' for provider '{provider.Name}'."),
        };

    }

    private ChatClientLease CreateOllamaLease(ProviderSettings provider, string resolvedModel)
    {

        HttpClient http = httpClientFactory.CreateClient(OllamaHttpClientName);

        http.BaseAddress = new Uri(provider.Endpoint);

        http.Timeout = Timeout.InfiniteTimeSpan;

        var ollama = new OllamaApiClient(http, resolvedModel, jsonSerializerContext: null);

        return new ChatClientLease(ollama, ollama, provider, resolvedModel, isOllama: true, ollamaHttp: http);

    }

    private static ChatClientLease CreateOpenAiCompatibleLease(ProviderSettings provider, string resolvedModel)
    {

        string key = string.IsNullOrEmpty(provider.ApiKey) ? KeylessOpenAiPlaceholder : provider.ApiKey;

        var credential = new ApiKeyCredential(key);

        var options = new OpenAIClientOptions
        {

            Endpoint = new Uri(provider.Endpoint),

        };

        var chatClient = new ChatClient(resolvedModel, credential, options);

        IChatClient meAi = chatClient.AsIChatClient();

        return new ChatClientLease(meAi, ollamaApi: null, provider, resolvedModel, isOllama: false, ollamaHttp: null);

    }

}

/// <summary>
/// Owns a resolved <see cref="IChatClient"/> and related disposables for one inference turn.
/// </summary>
public sealed class ChatClientLease : IDisposable
{

    private readonly OllamaApiClient? _ollama;

    private readonly HttpClient? _ollamaHttp;

    private bool _disposed;

    public ChatClientLease(
        IChatClient chatClient,
        IOllamaApiClient? ollamaApi,
        ProviderSettings provider,
        string resolvedModel,
        bool isOllama,
        HttpClient? ollamaHttp)
    {

        ChatClient = chatClient;

        OllamaApi = ollamaApi;

        Provider = provider;

        ResolvedModel = resolvedModel;

        IsOllama = isOllama;

        _ollama = ollamaApi as OllamaApiClient;

        _ollamaHttp = ollamaHttp;

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

            _ollamaHttp?.Dispose();

            return;

        }

        (ChatClient as IDisposable)?.Dispose();

    }

}
