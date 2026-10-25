using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

/// <summary>Owns one guarded transport for the joined memory evaluation request scope; every send gets its own connection.</summary>
public sealed class MemoryEvaluationProviderClientFactory : IDisposable
{
    private readonly IProviderApiKeyResolver _keys;

    private readonly HttpClient _httpClient;

    private int _disposed;

    public MemoryEvaluationProviderClientFactory(IProviderApiKeyResolver keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        _keys = keys;

        _httpClient = CreateOwnedHttpClient();
    }

    // Alternate owned-handler test path. Production DI selects the public constructor.
    internal MemoryEvaluationProviderClientFactory(IProviderApiKeyResolver keys, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(keys);

        ArgumentNullException.ThrowIfNull(handler);

        _keys = keys;

        _httpClient = new HttpClient(new MemoryEvaluationBoundedResponseHandler(handler), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<Result<ChatClientLease>> ResolveChatAsync(ProviderSettings provider, string model, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (provider.Type != AiProviderKind.OpenAICompatible) return Unsupported();

        OutputTokenLimitWireDialect dialect = provider.OutputTokenLimitWireDialect;

        if (!Enum.IsDefined(dialect))
        {
            throw new InvalidOperationException("Provider output-token limit wire dialect is not defined.");
        }

        Result<string?> key = await ResolveCredentialAsync(provider, ct).ConfigureAwait(false);

        if (key.IsFailure) return key.Error;

        ct.ThrowIfCancellationRequested();

        OpenAIClientOptions options = Options(provider);

        options.AddPolicy(new OutputTokenLimitWirePolicy(dialect), PipelinePosition.PerCall);

        OpenAI.Chat.ChatClient client = new(model, new ApiKeyCredential(key.Value ?? "no-key"), options);

        return new ChatClientLease(client.AsIChatClient(), provider, model, ownedHttpClient: null);
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

    private OpenAIClientOptions Options(ProviderSettings provider) => new()
    {
        Endpoint = new Uri(provider.Endpoint),
        Transport = new HttpClientPipelineTransport(_httpClient),
        RetryPolicy = new ClientRetryPolicy(RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation.MemoryEvaluationLimits.AutomaticRetries),
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

            return new HttpClient(new MemoryEvaluationBoundedResponseHandler(handler), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch
        {
            handler.Dispose();

            throw;
        }
    }

    private static Error Unsupported() => new(ErrorCodes.MemoryEvaluation.ProviderUnavailable, "memory evaluation requires an exact OpenAI-compatible provider transport.");
}
