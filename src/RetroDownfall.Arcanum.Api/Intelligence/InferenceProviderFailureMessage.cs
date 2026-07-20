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

            return $"Provider '{providerName}' returned HTTP {httpStatus.Value}. Check the model, API key, and request; see server logs for detail.";

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
