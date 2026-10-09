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

namespace RetroDownfall.Arcanum.Api.Intelligence;

public interface ICampaignRollupProviderClientFactory
{
    Task<Result<ChatClientLease?>> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken);
}

/// <summary>One explicit physical maintenance attempt, without transport mutation or automatic retry.</summary>
public sealed class CampaignRollupProviderClientFactory(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> settings,
    IProviderApiKeyResolver apiKeyResolver) : ICampaignRollupProviderClientFactory
{
    public const string HttpClientName = "CampaignRollupProvider";

    public async Task<Result<ChatClientLease?>> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
    {
        ArcanumSettings configuration = JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(settings.CurrentValue, ConfigurationJsonContext.Default.ArcanumSettings),
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

        string? key = await apiKeyResolver.ResolveAsync(provider, cancellationToken).ConfigureAwait(false);

        HttpClient http = httpClientFactory.CreateClient(HttpClientName);

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(provider.Endpoint),
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0),
        };

        OpenAI.Chat.ChatClient client = new(model, new ApiKeyCredential(key ?? "no-key"), options);

        return Result<ChatClientLease?>.Success(new(client.AsIChatClient(), provider, model, ownedHttpClient: null));
    }
}
