using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Resilience;

/// <summary>
/// Connectivity probe used by <see cref="ProviderHealthProbeService"/>. OpenAI-compatible
/// providers are probed via a short-lived, non-pooled HTTP call to <c>{endpoint}/models</c>.
/// </summary>
internal sealed class ProviderHealthProbe(
    IHttpClientFactory httpFactory,
    ConfigurationSecretProtector secretProtector,
    IOptionsMonitor<ArcanumSettings> options) : IProviderHealthProbe
{

    public const string HttpClientName = "ProviderHealthProbe";

    public async Task<bool> ProbeAsync(ProviderSettings provider, CancellationToken cancellationToken)
    {

        // Defensive only — config validation owns invalid-endpoint messaging. Empty endpoints must
        // not construct a relative "/models" URL or throw from the background probe.
        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {

            return false;

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

}
