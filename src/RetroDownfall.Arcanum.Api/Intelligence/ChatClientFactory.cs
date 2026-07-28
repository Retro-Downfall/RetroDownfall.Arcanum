using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using RetroDownfall.Arcanum.Core.Configuration;
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
/// OpenAI-compatible HTTP traffic uses the named client <c>OpenAiCompatibleProvider</c>, which is wired with
/// <see cref="OpenAiRequestAugmentingHandler"/> (structured-output <c>strict: true</c> injection).
/// </summary>
public sealed class ChatClientFactory(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ConfigurationSecretProtector secretProtector) : IChatClientFactory
{

    private const string OpenAiCompatibleHttpClientName = "OpenAiCompatibleProvider";

    private const string KeylessOpenAiPlaceholder = "no-key";

    public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
    {

        // Hot-reload: read settings only here — never cache ArcanumSettings on the singleton factory.
        ArcanumSettings arc = optionsMonitor.CurrentValue;

        if (!ProviderResolver.TryResolveProviderForModel(arc, targetModel, out ProviderSettings? provider, out string resolvedModel)
            || provider is null)
        {
            throw new InvalidOperationException(
                "No AI model could be resolved. Configure Arcanum:Providers (with non-empty Models) and Arcanum:DefaultModel, or pass a model override that matches a configured model.");

        }

        return ResolveClientAsync(provider, resolvedModel, cancellationToken);

    }

    public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken)
    {

        _ = cancellationToken;

        if (provider.Type != AiProviderKind.OpenAICompatible)
        {
            throw new InvalidOperationException(
                $"Unsupported provider type '{provider.Type}' for provider '{provider.Name}'. Only {nameof(AiProviderKind.OpenAICompatible)} is supported.");

        }

        return Task.FromResult(CreateOpenAiCompatibleLease(provider, resolvedModel));

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

        return new ChatClientLease(meAi, provider, resolvedModel, ownedHttpClient: null);

    }

}

/// <summary>
/// Owns a resolved <see cref="IChatClient"/> and related disposables for one inference turn.
/// </summary>
public sealed class ChatClientLease : IDisposable
{

    // Non-null only for per-lease HttpClient instances; named factory clients are never stored here.
    private readonly HttpClient? _ownedHttpClient;

    private bool _disposed;

    public ChatClientLease(
        IChatClient chatClient,
        ProviderSettings provider,
        string resolvedModel,
        HttpClient? ownedHttpClient)
    {

        ChatClient = chatClient;

        Provider = provider;

        ResolvedModel = resolvedModel;

        _ownedHttpClient = ownedHttpClient;

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

        _ownedHttpClient?.Dispose();

    }

}
