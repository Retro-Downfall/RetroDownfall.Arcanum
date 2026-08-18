using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using RetroDownfall.Arcanum.Api.Intelligence.Familiars;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

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
    IProviderApiKeyResolver? apiKeyResolver = null,
    IFamiliarProcessRunner? familiarProcessRunner = null,
    ILoggerFactory? loggerFactory = null) : IChatClientFactory
{

    private const string OpenAiCompatibleHttpClientName = "OpenAiCompatibleProvider";

    private const string KeylessOpenAiPlaceholder = "no-key";

    private readonly IFamiliarProcessRunner _familiarProcessRunner =
        familiarProcessRunner ?? new FamiliarProcessRunner();

    private readonly IProviderApiKeyResolver _apiKeyResolver =
        apiKeyResolver ?? EnvironmentOnlyProviderApiKeyResolver.Instance;

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

    public async Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken)
    {

        if (FamiliarProviders.IsFamiliar(provider))
        {

            return CreateFamiliarLease(provider, resolvedModel);

        }

        if (provider.Type != AiProviderKind.OpenAICompatible)
        {
            throw new InvalidOperationException(
                $"Unsupported provider type '{provider.Type}' for provider '{provider.Name}'.");

        }

        string? resolvedApiKey = await _apiKeyResolver
            .ResolveAsync(provider, cancellationToken)
            .ConfigureAwait(false);

        return CreateOpenAiCompatibleLease(provider, resolvedModel, resolvedApiKey);

    }

    /// <summary>
    /// A Familiar lease owns a spawn plan rather than an <see cref="HttpClient"/>. The lease
    /// lifetime is still one inference turn, which is exactly the process lifetime — there is no
    /// long-lived CLI to leak or wedge between turns.
    /// </summary>
    private ChatClientLease CreateFamiliarLease(ProviderSettings provider, string resolvedModel)
    {

        // Read once, here: the deny list is derived from the current provider table, and a Familiar
        // must never inherit a key configured for some other provider.
        IReadOnlyList<string> denied = FamiliarEnvironmentDenyList.Build(optionsMonitor.CurrentValue);

        IChatClient client = provider.Type switch
        {

            AiProviderKind.ClaudeCodeCli =>
                new ClaudeCodeCliChatClient(_familiarProcessRunner, provider, resolvedModel, denied),

            // The Familiar adapters take an optional logger rather than requiring one, so a test or a
            // composition without logging still constructs them; here, where the real DI container is,
            // one is always available.
            AiProviderKind.CodexCli =>
                new CodexCliChatClient(
                    _familiarProcessRunner,
                    provider,
                    resolvedModel,
                    denied,
                    loggerFactory?.CreateLogger<CodexCliChatClient>()),

            _ => throw new InvalidOperationException(
                $"Unsupported Familiar type '{provider.Type}' for provider '{provider.Name}'."),

        };

        return new ChatClientLease(client, provider, resolvedModel, ownedHttpClient: null);

    }

    private ChatClientLease CreateOpenAiCompatibleLease(
        ProviderSettings provider,
        string resolvedModel,
        string? resolvedApiKey)
    {

        string key = resolvedApiKey ?? KeylessOpenAiPlaceholder;

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
