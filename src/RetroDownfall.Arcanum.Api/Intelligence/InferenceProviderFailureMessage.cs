using System.ClientModel;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Public-facing inference failure copy. Prefer HTTP status detail when the provider answered;
/// reserve "unreachable" for genuine transport/connectivity failures (Status &lt;= 0 / no response).
/// </summary>
internal static class InferenceProviderFailureMessage
{
    public static string Build(string providerName, int? httpStatus = null)
    {
        if (httpStatus is > 0)
        {
            string remedy = httpStatus.Value switch
            {
                401 => "Check the configured credential and its authentication requirements.",
                403 => "Check the credential's access permissions for this model.",
                404 => "Check the configured model and provider endpoint.",
                400 or 413 or 422 => "Check the request shape, size, and model capabilities.",
                408 => "The provider timed out. Retry later.",
                429 => "The provider reported a rate limit. Retry later or check the provider's limits.",
                >= 500 and <= 599 => "The provider reported a server error. Retry later or check its service status.",
                _ => "See server logs for diagnostic details.",
            };

            return $"Provider '{providerName}' returned HTTP {httpStatus.Value}. {remedy}";
        }

        // W3.5: do NOT embed provider.Endpoint — this message surfaces to clients via the
        // native /api inference envelopes and the raw endpoint URL can leak internal hostnames/paths.
        return $"Provider '{providerName}' is unreachable. Verify the service is running and Arcanum:Providers is configured correctly.";
    }

    public static string Build(string providerName, Exception? ex) =>
        Build(providerName, TryGetHttpStatus(ex));

    public static int? TryGetHttpStatus(Exception? ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is ClientResultException { Status: > 0 } clientResult)
            {
                return clientResult.Status;
            }
        }

        return null;
    }
}
