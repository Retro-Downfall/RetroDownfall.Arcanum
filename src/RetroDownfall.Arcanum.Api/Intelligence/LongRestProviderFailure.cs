using System.ClientModel;
using System.Net;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Types a failed Long Rest SDK call. It reads the HTTP status and the transport error kind only and never the provider's
/// response body, message or headers, so no provider text can reach a park code, a log or an operation record.
/// </summary>
internal static class LongRestProviderFailure
{
    private const int MaximumDepth = 8;

    /// <summary>
    /// 5xx, 429 and 408 are transient (<see cref="ErrorCodes.LongRest.ProviderUnavailable"/>); any other 4xx or 3xx is a rejection
    /// retrying cannot fix (<see cref="ErrorCodes.LongRest.ProviderRejected"/>). With no HTTP status, a failure raised before any
    /// request byte (name resolution, connection, TLS) is <see cref="ErrorCodes.LongRest.ProviderUnreachable"/>; a dropped
    /// connection, timeout or anything else is <see cref="ErrorCodes.LongRest.ProviderUnavailable"/>.
    /// </summary>
    internal static Error Classify(Exception exception)
    {
        int status = 0;

        HttpRequestError? transport = null;

        Exception? current = exception;

        for (int depth = 0; current is not null && depth < MaximumDepth; depth++)
        {
            if (current is ClientResultException result && status == 0)
            {
                status = result.Status;
            }
            else if (current is HttpRequestException http)
            {
                transport ??= http.HttpRequestError;

                if (status == 0 && http.StatusCode is { } code)
                {
                    status = (int)code;
                }
            }

            current = current.InnerException;
        }

        if (status != 0)
        {
            return status is 408 or 429 or >= 500
                ? new Error(ErrorCodes.LongRest.ProviderUnavailable, "The Long Rest provider could not complete the call right now.")
                : new Error(ErrorCodes.LongRest.ProviderRejected, "The Long Rest provider rejected the call.");
        }

        return transport is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
            ? new Error(ErrorCodes.LongRest.ProviderUnreachable, "The Long Rest provider could not be reached.")
            : new Error(ErrorCodes.LongRest.ProviderUnavailable, "The Long Rest provider could not complete the call right now.");
    }
}
