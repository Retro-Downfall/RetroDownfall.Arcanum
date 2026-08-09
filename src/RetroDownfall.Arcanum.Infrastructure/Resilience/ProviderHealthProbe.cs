using System.Net.Http.Headers;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Infrastructure.Resilience;

/// <summary>
/// Connectivity probe used by <see cref="ProviderHealthProbeService"/>. OpenAI-compatible
/// providers are probed via a short-lived, non-pooled HTTP call to <c>{endpoint}/models</c>; a
/// Familiar has no endpoint and is probed by resolving its binary.
/// </summary>
internal sealed class ProviderHealthProbe(
    IHttpClientFactory httpFactory,
    IProviderApiKeyResolver? apiKeyResolver = null) : IProviderHealthProbe
{

    public const string HttpClientName = "ProviderHealthProbe";

    private readonly IProviderApiKeyResolver _apiKeyResolver =
        apiKeyResolver ?? EnvironmentOnlyProviderApiKeyResolver.Instance;

    public async Task<bool> ProbeAsync(ProviderSettings provider, CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(provider);

        // A Familiar is healthy when its binary is where the operator said it would be. Deliberately
        // a filesystem check and not a spawn: this runs on a background interval, it must cost
        // nothing, and whether the CLI is *signed in* is a question for the status probe — which the
        // operator asks on demand and which returns actionable remediation rather than a boolean.
        if (FamiliarProviders.IsFamiliar(provider))
        {

            return FamiliarExecutableResolver.TryResolve(FamiliarProviders.ResolveCommand(provider), out _);

        }

        // Defensive only — config validation owns invalid-endpoint messaging. Empty endpoints must
        // not construct a relative "/models" URL or throw from the background probe.
        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {

            return false;

        }

        string baseUrl = provider.Endpoint.Trim().TrimEnd('/');

        string probeUrl = $"{baseUrl}/models";

        int timeoutSeconds = ArcanumSettingClamps.HealthProbeTimeoutSeconds(
            ArcanumRuntimeDefaults.Resilience.HealthProbeTimeoutSeconds);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {

            // IHttpClientFactory.CreateClient returns a fresh HttpClient instance per call (backed by
            // a pooled handler), so setting a per-provider Authorization header here is safe even
            // though this named client is shared across concurrent probes for different providers.
            HttpClient client = httpFactory.CreateClient(HttpClientName);

            string? resolvedApiKey = await _apiKeyResolver
                .ResolveAsync(provider, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(resolvedApiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", resolvedApiKey);
            }

            using HttpRequestMessage request = new(HttpMethod.Get, probeUrl);

            using HttpResponseMessage response = await client
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;

        }
        catch (Exception)
        {

            return false;

        }

    }

}
