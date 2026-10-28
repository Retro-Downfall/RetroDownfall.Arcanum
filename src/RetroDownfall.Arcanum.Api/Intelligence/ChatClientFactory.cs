using RetroDownfall.Arcanum.Core.Weave.Tapestry;
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
    IProviderApiKeyResolver? apiKeyResolver,
    IFamiliarProcessRunner? familiarProcessRunner,
    ILoggerFactory? loggerFactory,
    PromptCacheConfigurationEpoch? promptCacheConfigurationEpoch,
    ITapestryProviderValidator? tapestryValidator) : IChatClientFactory
{
    /// <summary>Existing ordinary-only construction; an active Tapestry observation still fails closed.</summary>
    public ChatClientFactory(IHttpClientFactory httpClientFactory, IOptionsMonitor<ArcanumSettings> optionsMonitor,
        IProviderApiKeyResolver? apiKeyResolver = null, IFamiliarProcessRunner? familiarProcessRunner = null,
        ILoggerFactory? loggerFactory = null, PromptCacheConfigurationEpoch? promptCacheConfigurationEpoch = null)
        : this(httpClientFactory, optionsMonitor, apiKeyResolver, familiarProcessRunner, loggerFactory, promptCacheConfigurationEpoch, null)
    {
    }

    private readonly ITapestryProviderValidator? _tapestryValidator = tapestryValidator;

    private const string OpenAiCompatibleHttpClientName = "OpenAiCompatibleProvider";

    private const string KeylessOpenAiPlaceholder = "no-key";

    private readonly IFamiliarProcessRunner _familiarProcessRunner =
        familiarProcessRunner ?? new FamiliarProcessRunner();

    private readonly IProviderApiKeyResolver _apiKeyResolver =
        apiKeyResolver ?? EnvironmentOnlyProviderApiKeyResolver.Instance;

    public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
    {
        object? observation = ActiveResolution.Value?.Enter(this);

        // Hot-reload: read settings only here — never cache ArcanumSettings on the singleton factory.
        ArcanumSettings arc = optionsMonitor.CurrentValue;

        PromptCacheConfigurationSnapshot? snapshot = arc.Features.DynamicContextInjectionV2
            ? promptCacheConfigurationEpoch?.Capture()
            : null;

        if (snapshot is not null)
        {
            arc = snapshot.ReadSettings();
        }

        if (!ProviderResolver.TryResolveProviderForModel(arc, targetModel, out ProviderSettings? provider, out string resolvedModel)
            || provider is null)
        {
            throw GuardFailure(observation, ResolutionFailureReason.NoConfiguredModel,
                "No AI model could be resolved. Configure Arcanum:Providers (with non-empty Models) and Arcanum:DefaultModel, or pass a model override that matches a configured model.");
        }

        return ResolveClientCoreAsync(provider, resolvedModel, arc.Features.DynamicContextInjectionV2, snapshot, observation, cancellationToken);
    }

    public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken)
    {
        object? observation = ActiveResolution.Value?.Enter(this);

        bool enabled = optionsMonitor.CurrentValue.Features.DynamicContextInjectionV2;

        PromptCacheConfigurationSnapshot? snapshot = enabled ? promptCacheConfigurationEpoch?.Capture() : null;

        enabled = snapshot?.ReadSettings().Features.DynamicContextInjectionV2 ?? enabled;

        return ResolveClientCoreAsync(provider, resolvedModel, enabled, snapshot, observation, cancellationToken);
    }

    private async Task<ChatClientLease> ResolveClientCoreAsync(
        ProviderSettings provider,
        string resolvedModel,
        bool enabled,
        PromptCacheConfigurationSnapshot? snapshot,
        object? observation,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(provider.OutputTokenLimitWireDialect))
        {
            throw GuardFailure(observation, ResolutionFailureReason.InvalidOutputTokenDialect, "Provider output-token limit wire dialect is not defined.");
        }

        // Freeze the declared wire contract before any asynchronous credential resolution.
        provider = PromptCacheClientIdentity.Freeze(provider);

        if (FamiliarProviders.IsFamiliar(provider))
        {
            return CreateFamiliarLease(provider, resolvedModel, observation);
        }

        if (provider.Type != AiProviderKind.OpenAICompatible)
        {
            throw GuardFailure(observation, ResolutionFailureReason.UnsupportedProviderKind,
                $"Unsupported provider type '{provider.Type}' for provider '{provider.Name}'.");
        }

        if (enabled)
        {
            ResolvedProviderCredential captured = await _apiKeyResolver
                .ResolveWithIdentityAsync(provider, cancellationToken)
                .ConfigureAwait(false);

            return CreateOpenAiCompatibleLease(provider, resolvedModel, captured.Secret, snapshot?.Stamp, captured.Stamp);
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
    private ChatClientLease CreateFamiliarLease(ProviderSettings provider, string resolvedModel, object? observation)
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

            _ => throw GuardFailure(observation, ResolutionFailureReason.UnsupportedFamiliarKind,
                $"Unsupported Familiar type '{provider.Type}' for provider '{provider.Name}'."),
        };

        return new ChatClientLease(client, provider, resolvedModel, ownedHttpClient: null);
    }

    private ChatClientLease CreateOpenAiCompatibleLease(
        ProviderSettings provider,
        string resolvedModel,
        string? resolvedApiKey,
        PromptCacheConfigurationStamp? configurationStamp = null,
        ProviderCredentialStamp? credentialStamp = null)
    {
        string key = resolvedApiKey ?? KeylessOpenAiPlaceholder;

        var credential = new ApiKeyCredential(key);

        HttpClient http = httpClientFactory.CreateClient(OpenAiCompatibleHttpClientName);

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Endpoint),

            Transport = new HttpClientPipelineTransport(http),

            NetworkTimeout = Timeout.InfiniteTimeSpan,

            RetryPolicy = new ClientRetryPolicy(0),
        };

        options.AddPolicy(new OutputTokenLimitWirePolicy(provider.OutputTokenLimitWireDialect), PipelinePosition.PerCall);

        options.AddPolicy(new TapestryProviderPolicy(_tapestryValidator, provider.Name, resolvedModel, embedding: false), PipelinePosition.BeforeTransport);

        options.AddPolicy(new TapestryRetrievalDispatchPolicy(provider.Name, resolvedModel), PipelinePosition.BeforeTransport);

        PromptCacheDispatchPolicy? dispatchPolicy = configurationStamp is null ? null : new();

        if (dispatchPolicy is not null)
        {
            options.AddPolicy(dispatchPolicy, PipelinePosition.BeforeTransport);
        }

        var chatClient = new ChatClient(resolvedModel, credential, options);

        IChatClient meAi = chatClient.AsIChatClient();

        PromptCacheClientIdentity? identity = configurationStamp is not null
            ? new(meAi, provider, resolvedModel, configurationStamp, credentialStamp, dispatchPolicy)
            : null;

        return new ChatClientLease(meAi, provider, resolvedModel, ownedHttpClient: null, identity);
    }

    internal enum ResolutionFailureReason
    {
        Unknown,

        NoConfiguredModel,

        NoConfiguredCandidate,

        InvalidOutputTokenDialect,

        UnsupportedProviderKind,

        UnsupportedFamiliarKind,
    }

    private static readonly AsyncLocal<ResolutionObservation?> ActiveResolution = new();

    internal static ResolutionObservation ObserveResolution(IChatClientFactory factory) => new(factory);

    private static InvalidOperationException GuardFailure(object? attempt, ResolutionFailureReason reason, string message)
    {
        InvalidOperationException failure = new(message);

        ActiveResolution.Value?.Record(attempt, failure, reason);

        return failure;
    }

    // Observation only: one exact factory invocation and exception, never configuration, payload or authority.
    internal sealed class ResolutionObservation : IDisposable
    {
        private readonly object _sync = new();

        private WeakReference<IChatClientFactory>? _factory;

        private readonly ResolutionObservation? _previous;

        private object? _attempt;

        private WeakReference<Exception>? _failure;

        private ResolutionFailureReason _reason;

        private bool _entered;

        private bool _finished;

        internal ResolutionObservation(IChatClientFactory factory)
        {
            _factory = new(factory);

            _previous = ActiveResolution.Value;

            ActiveResolution.Value = this;
        }

        internal object? Enter(ChatClientFactory factory)
        {
            lock (_sync)
            {
                if (_finished || _entered || !ReferenceEquals(ActiveResolution.Value, this)
                    || _factory is null || !_factory.TryGetTarget(out IChatClientFactory? expected)
                    || !ReferenceEquals(factory, expected))
                {
                    return null;
                }

                _entered = true;

                _attempt = new object();

                return _attempt;
            }
        }

        internal void Record(object? attempt, Exception failure, ResolutionFailureReason reason)
        {
            lock (_sync)
            {
                if (_finished || attempt is null || !ReferenceEquals(attempt, _attempt)
                    || !ReferenceEquals(ActiveResolution.Value, this))
                {
                    return;
                }

                _failure = new(failure);

                _reason = reason;
            }
        }

        internal ResolutionFailureReason Consume(Exception failure)
        {
            lock (_sync)
            {
                ResolutionFailureReason reason = !_finished && ReferenceEquals(ActiveResolution.Value, this)
                    && _failure is not null && _failure.TryGetTarget(out Exception? observed)
                    && ReferenceEquals(failure, observed) ? _reason : ResolutionFailureReason.Unknown;

                Clear();

                return reason;
            }
        }

        private void Clear()
        {
            _finished = true;

            _factory = null;

            _attempt = null;

            _failure = null;

            _reason = ResolutionFailureReason.Unknown;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                Clear();

                if (ReferenceEquals(ActiveResolution.Value, this))
                {
                    ActiveResolution.Value = _previous;
                }
            }
        }
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
        HttpClient? ownedHttpClient,
        PromptCacheClientIdentity? promptCacheIdentity = null)
    {
        PromptCacheIdentity = promptCacheIdentity;

        ChatClient = chatClient;

        Provider = provider;

        ResolvedModel = resolvedModel;

        _ownedHttpClient = ownedHttpClient;
    }

    public PromptCacheClientIdentity? PromptCacheIdentity { get; }

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
