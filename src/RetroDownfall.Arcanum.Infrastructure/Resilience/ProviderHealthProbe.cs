using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

namespace RetroDownfall.Arcanum.Infrastructure.Resilience;

/// <summary>
/// Connectivity probe used by <see cref="ProviderHealthProbeService"/>. Ollama and OpenAI-compatible
/// providers are probed via a short-lived, non-pooled HTTP call; <see cref="AiProviderKind.LlamaCppServer"/>
/// providers are checked against the in-process <see cref="ILlamaServerManager"/> state — no HTTP call.
/// </summary>
internal sealed class ProviderHealthProbe(
    IHttpClientFactory httpFactory,
    ILlamaServerManager llamaManager,
    IOptionsMonitor<ArcanumSettings> options) : IProviderHealthProbe
{

    public const string HttpClientName = "ProviderHealthProbe";

    public async Task<bool> ProbeAsync(ProviderSettings provider, CancellationToken cancellationToken)
    {

        if (provider.Type == AiProviderKind.LlamaCppServer)
        {
            return ProbeLlamaCppServer(provider);
        }

        string baseUrl = provider.Endpoint.Trim().TrimEnd('/');

        string probeUrl = provider.Type switch
        {
            AiProviderKind.Ollama => $"{baseUrl}/api/tags",
            AiProviderKind.OpenAICompatible => $"{baseUrl}/models",
            _ => $"{baseUrl}/models",
        };

        int timeoutSeconds = ArcanumSettingClamps.HealthProbeTimeoutSeconds(
            options.CurrentValue.Resilience?.HealthProbeTimeoutSeconds ?? new ResilienceSettings().HealthProbeTimeoutSeconds);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {

            HttpClient client = httpFactory.CreateClient(HttpClientName);

            using HttpResponseMessage response = await client.GetAsync(probeUrl, timeoutCts.Token).ConfigureAwait(false);

            return response.IsSuccessStatusCode;

        }
        catch (Exception)
        {

            return false;

        }

    }

    private bool ProbeLlamaCppServer(ProviderSettings provider)
    {

        string? cacheKey = ResolvePrimaryModelCacheKey(provider);

        if (cacheKey is null)
        {
            return true;
        }

        LlamaServerInfo? info = llamaManager.TryGetRunningServer(cacheKey);

        return info is not null && info.State is LlamaServerState.Running or LlamaServerState.Starting;

    }

    private static string? ResolvePrimaryModelCacheKey(ProviderSettings provider)
    {

        string[] models = provider.Models ?? [];

        if (models.Length > 0 && !string.IsNullOrWhiteSpace(models[0]))
        {
            return LlamaCacheKey.NormalizeModelKey(models[0]);
        }

        Dictionary<string, string>? map = provider.LlamaCpp?.ModelMap;

        if (map is { Count: > 0 })
        {

            foreach (string key in map.Keys)
            {
                return key;
            }

        }

        return null;

    }

}
