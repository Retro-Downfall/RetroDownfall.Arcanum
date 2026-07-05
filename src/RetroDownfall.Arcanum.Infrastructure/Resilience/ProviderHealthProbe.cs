using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Resilience;

/// <summary>
/// Connectivity probe used by <see cref="ProviderHealthProbeService"/>. OpenAI-compatible
/// providers are probed via a short-lived, non-pooled HTTP call; <see cref="AiProviderKind.LlamaCppServer"/>
/// providers are checked against the in-process <see cref="ILlamaServerManager"/> state — no HTTP call.
/// </summary>
internal sealed class ProviderHealthProbe(
    IHttpClientFactory httpFactory,
    ILlamaServerManager llamaManager,
    ConfigurationSecretProtector secretProtector,
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

        string probeUrl = $"{baseUrl}/models";

        int timeoutSeconds = ArcanumSettingClamps.HealthProbeTimeoutSeconds(
            options.CurrentValue.Resilience?.HealthProbeTimeoutSeconds ?? new ResilienceSettings().HealthProbeTimeoutSeconds);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {

            // IHttpClientFactory.CreateClient returns a fresh HttpClient instance per call (backed by
            // a pooled handler), so setting a per-provider Authorization header here is safe even
            // though this named client is shared across concurrent probes for different providers.
            HttpClient client = httpFactory.CreateClient(HttpClientName);

            if (!string.IsNullOrEmpty(provider.ApiKey))
            {

                string? resolvedApiKey = secretProtector.ResolveApiKey(provider.ApiKey);

                if (!string.IsNullOrEmpty(resolvedApiKey))
                {

                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resolvedApiKey);

                }

            }

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

            // A LlamaCppServer provider with no Models and no llamaCpp.ModelMap entries has nothing
            // to probe or ever actually serve — ConfigurationValidator rejects this shape at
            // startup, but hot-reloaded settings are not re-validated, so this can still be reached
            // at runtime. Reporting it healthy (the previous behavior) hid a real misconfiguration
            // from operators and from ProviderResolver's health-based fallback selection.
            return false;

        }

        LlamaServerInfo? info = llamaManager.TryGetRunningServer(cacheKey);

        return info is not null && info.State is LlamaServerState.Running or LlamaServerState.Starting;

    }

    private static string? ResolvePrimaryModelCacheKey(ProviderSettings provider)
    {

        IReadOnlyList<ModelEntry> models = provider.Models ?? [];

        if (models.Count > 0 && !string.IsNullOrWhiteSpace(models[0].Name))
        {
            return LlamaCacheKey.NormalizeModelKey(models[0].Name);
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
