using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>The physical route frozen with an SDK client before credential resolution yields.</summary>
internal sealed class ProviderWorkClientIdentity
{
    private readonly string _provider;

    private readonly string _endpoint;

    private readonly string? _runtime;

    internal ProviderResourceKey? Resource { get; }

    internal bool Ollama { get; }

    internal ProviderWorkDispatchPolicy DispatchPolicy { get; }

    internal Uri Endpoint => new(_endpoint);

    internal string? LocalRuntimeId => _runtime;

    internal ProviderWorkClientIdentity(ProviderSettings provider, ArcanumSettings? settings, IOptionsMonitor<ArcanumSettings>? currentSettings)
    {
        DispatchPolicy = new(this, currentSettings);

        _provider = provider.Name;

        _endpoint = new Uri(provider.Endpoint, UriKind.Absolute).AbsoluteUri;

        _runtime = provider.LocalRuntimeId;

        Resource = ProviderResourceKey.FromProvider(provider, settings);

        Ollama = Resource is not null && settings!.LocalRuntimes.Single(runtime => string.Equals(runtime.Id, _runtime,
            StringComparison.OrdinalIgnoreCase)).Adapter == LocalRuntimeAdapterCatalog.Ollama;
    }

    internal bool IsCurrent(ArcanumSettings? settings)
    {
        if (Resource is null)
        {
            return settings is null || !ProviderResolver.TryResolveProviderByName(settings, _provider, out ProviderSettings? unbound)
                || unbound is null || string.IsNullOrEmpty(unbound.LocalRuntimeId);
        }

        if (settings is null || !ProviderResolver.TryResolveProviderByName(settings, _provider, out ProviderSettings? provider)
            || provider is null || provider.Type != AiProviderKind.OpenAICompatible
            || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || !string.Equals(_endpoint, endpoint.AbsoluteUri, StringComparison.Ordinal)
            || !string.Equals(_runtime, provider.LocalRuntimeId, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            return Resource == ProviderResourceKey.FromProvider(provider, settings);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal ValueTask ReserveForegroundAsync(CancellationToken cancellationToken) =>
        Resource is { } resource && ProviderWorkAmbient.Owner is ProviderWorkCoordinator.ForegroundTurn { IsActive: true } owner
            ? owner.ReserveAsync(resource, cancellationToken) : ValueTask.CompletedTask;

    internal IChatClient Bind(IChatClient client) => new BoundChatClient(client, this);

    internal IEmbeddingGenerator<string, Embedding<float>> Bind(IEmbeddingGenerator<string, Embedding<float>> generator) => new BoundEmbeddingGenerator(generator, this);

    internal static ProviderWorkClientIdentity? Lookup(IChatClient client) =>
        client is BoundChatClient bound ? bound.Identity : null;

    internal static ProviderWorkClientIdentity? Lookup(IEmbeddingGenerator<string, Embedding<float>> generator) =>
        generator is BoundEmbeddingGenerator bound ? bound.Identity : null;

    internal bool TryConsumeNoSend(Exception exception) => exception is NoSendException refusal
        && ReferenceEquals(this, refusal.Identity) && refusal.TryConsume();

    // The private type cannot be constructed by another client or a later accounting/disposal frame.
    private sealed class NoSendException(ProviderWorkClientIdentity identity) : Exception("The configured local runtime route changed before transport.")
    {
        private int _consumed;

        internal ProviderWorkClientIdentity Identity { get; } = identity;

        internal bool TryConsume() => Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
    }

    private sealed class BoundChatClient(IChatClient inner, ProviderWorkClientIdentity identity) : DelegatingChatClient(inner)
    {
        internal ProviderWorkClientIdentity Identity => identity;

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(ProviderWorkClientIdentity) ? identity : base.GetService(serviceType, serviceKey);

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            using ProviderWorkDispatchPolicy.Scope scope = identity.DispatchPolicy.Enter();

            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (scope.IsNoSend(exception))
            {
                throw new NoSendException(identity);
            }
            catch (NoSendException)
            {
                throw new InvalidOperationException("An earlier provider refusal cannot classify this call.");
            }
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ProviderWorkDispatchPolicy.Scope call = identity.DispatchPolicy.CreateCall();

            await using IAsyncEnumerator<ChatResponseUpdate> stream = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);

            while (await MoveNextAsync(stream, call).ConfigureAwait(false)) yield return stream.Current;
        }

        private async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<ChatResponseUpdate> stream, ProviderWorkDispatchPolicy.Scope call)
        {
            // Re-enter only around the producer MoveNext, never around a yielded consumer continuation.
            using IDisposable scope = identity.DispatchPolicy.Enter(call);

            try
            {
                return await stream.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (call.IsNoSend(exception))
            {
                throw new NoSendException(identity);
            }
            catch (NoSendException)
            {
                throw new InvalidOperationException("An earlier provider refusal cannot classify this call.");
            }
        }
    }

    private sealed class BoundEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, ProviderWorkClientIdentity identity)
        : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
    {
        internal ProviderWorkClientIdentity Identity => identity;

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(ProviderWorkClientIdentity) ? identity : base.GetService(serviceType, serviceKey);

        public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            using ProviderWorkDispatchPolicy.Scope scope = identity.DispatchPolicy.Enter();

            try
            {
                return await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (scope.IsNoSend(exception))
            {
                throw new NoSendException(identity);
            }
            catch (NoSendException)
            {
                throw new InvalidOperationException("An earlier provider refusal cannot classify this call.");
            }
        }
    }
}
