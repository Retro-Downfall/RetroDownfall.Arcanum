using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public interface ICampaignRollupProviderClientFactory
{
    Task<Result<ChatClientLease?>> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken);
}

/// <summary>
/// One explicit physical maintenance attempt, without transport mutation or automatic retry. The scoped factory
/// owns one <see cref="HttpClient"/> built from an explicit handler chain (the pinned provider egress handler),
/// exactly as <see cref="LongRestProviderClientFactory"/> does: no named <see cref="IHttpClientFactory"/> client
/// or shared factory pool sits between the SDK call and the wire. The client is reused by every lease of the
/// scope, leases leave it open, and scope disposal closes its connections.
/// </summary>
public sealed class CampaignRollupProviderClientFactory : ICampaignRollupProviderClientFactory, IDisposable
{
    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    private readonly IProviderApiKeyResolver _apiKeyResolver;

    private readonly HttpClient _httpClient;

    private int _disposed;

    public CampaignRollupProviderClientFactory(IOptionsMonitor<ArcanumSettings> settings, IProviderApiKeyResolver apiKeyResolver)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ArgumentNullException.ThrowIfNull(apiKeyResolver);

        _settings = settings;

        _apiKeyResolver = apiKeyResolver;

        _httpClient = CreateOwnedHttpClient();
    }

    // Alternate owned-handler test path. Production DI selects the public constructor.
    internal CampaignRollupProviderClientFactory(IOptionsMonitor<ArcanumSettings> settings, IProviderApiKeyResolver apiKeyResolver, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ArgumentNullException.ThrowIfNull(apiKeyResolver);

        ArgumentNullException.ThrowIfNull(handler);

        _settings = settings;

        _apiKeyResolver = apiKeyResolver;

        _httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<Result<ChatClientLease?>> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        ArcanumSettings configuration = JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(_settings.CurrentValue, ConfigurationJsonContext.Default.ArcanumSettings),
            ConfigurationJsonContext.Default.ArcanumSettings)!;

        if (!ProviderResolver.TryResolveProviderForModel(configuration, targetModel, out ProviderSettings? provider, out string model)
            || provider is null)
        {
            return new Error(ErrorCodes.Hub.Error, "No configured provider model is available for Campaign summary maintenance.");
        }

        if (provider.Type is not AiProviderKind.OpenAICompatible)
        {
            return Result<ChatClientLease?>.Success(null);
        }

        provider = JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(provider, ConfigurationJsonContext.Default.ProviderSettings),
            ConfigurationJsonContext.Default.ProviderSettings)!;

        string? key = await _apiKeyResolver.ResolveAsync(provider, cancellationToken).ConfigureAwait(false);

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(provider.Endpoint),
            Transport = new HttpClientPipelineTransport(_httpClient),
            RetryPolicy = new ClientRetryPolicy(0),
        };

        OpenAI.Chat.ChatClient client = new(model, new ApiKeyCredential(key ?? "no-key"), options);

        return Result<ChatClientLease?>.Success(new(client.AsIChatClient(), provider, model, ownedHttpClient: null));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _httpClient.Dispose();
    }

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
}
