using System.ClientModel;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Public-facing inference failure copy. Prefer HTTP status detail when the provider answered;
/// reserve "unreachable" for positively classified connection failures. Missing status stays unknown.
/// </summary>
internal static class InferenceProviderFailureMessage
{
    public static string Build(string providerName, int? httpStatus = null)
    {
        if (httpStatus is >= 100 and <= 599)
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

        return PublicInferenceErrorMessages.NativeGenericFailure;
    }

    public static string Build(string providerName, Exception? ex)
    {
        if (TryGetHttpStatus(ex) is { } status) return Build(LongRestProviderFailure.Label(providerName), status);

        // Missing status alone proves nothing. This helper diagnoses connectivity only after the
        // caller selected the actual invocation origin; it never sees endpoint or exception text.
        return ex is not null && LongRestProviderFailure.Classify(ex).Code == ErrorCodes.LongRest.ProviderUnreachable
            ? $"Provider '{LongRestProviderFailure.Label(providerName)}' is unreachable. Verify the service is running and Arcanum:Providers is configured correctly."
            : PublicInferenceErrorMessages.NativeGenericFailure;
    }

    public static int? TryGetHttpStatus(Exception? ex)
    {
        Exception? current = ex;

        for (int depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
        {
            if (current is ClientResultException { Status: >= 100 and <= 599 } clientResult) return clientResult.Status;

            if (current is HttpRequestException { StatusCode: { } code } && (int)code is >= 100 and <= 599) return (int)code;
        }

        return null;
    }
}
