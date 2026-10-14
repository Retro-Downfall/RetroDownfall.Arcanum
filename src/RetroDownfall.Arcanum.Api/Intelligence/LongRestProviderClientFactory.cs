using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Owns one guarded transport for the joined Long Rest sweep scope; every send gets its own connection.</summary>
public sealed class LongRestProviderClientFactory : IDisposable
{
    private readonly IProviderApiKeyResolver _keys;

    private readonly HttpClient _httpClient;

    private int _disposed;

    public LongRestProviderClientFactory(IProviderApiKeyResolver keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        _keys = keys;

        _httpClient = CreateOwnedHttpClient();
    }

    // Alternate owned-handler test path. Production DI selects the public constructor.
    internal LongRestProviderClientFactory(IProviderApiKeyResolver keys, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(keys);

        ArgumentNullException.ThrowIfNull(handler);

        _keys = keys;

        _httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<Result<ChatClientLease>> ResolveChatAsync(LongRestProviderSelection selection, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (selection.Provider.Type != AiProviderKind.OpenAICompatible) return Unsupported();

        Result<string?> key = await ResolveCredentialAsync(selection.Provider, ct).ConfigureAwait(false);

        if (key.IsFailure) return key.Error;

        ct.ThrowIfCancellationRequested();

        OpenAIClientOptions options = Options(selection);

        OpenAI.Chat.ChatClient client = new(selection.Model, new ApiKeyCredential(key.Value ?? "no-key"), options);

        return new ChatClientLease(client.AsIChatClient(), selection.Provider, selection.Model, ownedHttpClient: null);
    }

    public async Task<Result<EmbeddingGeneratorLease>> ResolveEmbeddingAsync(LongRestProviderSelection selection, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (selection.Provider.Type != AiProviderKind.OpenAICompatible || selection.Dimensions <= 0) return Unsupported();

        Result<string?> key = await ResolveCredentialAsync(selection.Provider, ct).ConfigureAwait(false);

        if (key.IsFailure) return key.Error;

        ct.ThrowIfCancellationRequested();

        OpenAIClientOptions options = Options(selection);

        OpenAI.Embeddings.EmbeddingClient client = new(selection.Model, new ApiKeyCredential(key.Value ?? "no-key"), options);

        return new EmbeddingGeneratorLease(client.AsIEmbeddingGenerator(), ownsGenerator: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _httpClient.Dispose();
    }

    private async Task<Result<string?>> ResolveCredentialAsync(ProviderSettings provider, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            ProviderSettings credentials = JsonSerializer.Deserialize(
                JsonSerializer.SerializeToUtf8Bytes(provider, ConfigurationJsonContext.Default.ProviderSettings),
                ConfigurationJsonContext.Default.ProviderSettings)!;

            return Result<string?>.Success(await _keys.ResolveAsync(credentials, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new Error(ErrorCodes.Hub.Error, PublicInferenceErrorMessages.NativeGenericFailure);
        }
    }

    private OpenAIClientOptions Options(LongRestProviderSelection selection) => new()
    {
        Endpoint = new Uri(selection.Provider.Endpoint),
        Transport = new HttpClientPipelineTransport(_httpClient),
        RetryPolicy = new ClientRetryPolicy(0),
    };

    private static HttpClient CreateOwnedHttpClient()
    {
        SocketsHttpHandler handler = OutboundUrlGuard.CreateProviderEgressHandler();

        try
        {
            // One connection per send: a zero lifetime and idle timeout close every connection as soon as its response is joined,
            // so a pooled connection that went stale can never be silently resent on. One SDK call is one physical request.
            handler.PooledConnectionLifetime = TimeSpan.Zero;

            handler.PooledConnectionIdleTimeout = TimeSpan.Zero;

            return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch
        {
            handler.Dispose();

            throw;
        }
    }

    private static Error Unsupported() => new(ErrorCodes.LongRest.InvalidRequest, "Long Rest requires an exact OpenAI-compatible provider transport.");
}
