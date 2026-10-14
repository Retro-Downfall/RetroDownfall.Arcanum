using System.ClientModel;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Types a failed Long Rest SDK call for the executor from the exception chain alone. It reads exception types and, for the SDK's
/// own error exception, the three-digit HTTP status the SDK prints at the head of its message. It keeps nothing else of that
/// message: the provider's response body that may follow it is never copied, logged, stored or returned, and no property of the
/// provider's answer is read.
/// </summary>
internal static class LongRestProviderFailure
{
    private const int MaximumDepth = 8;

    // The two heads System.ClientModel gives an error response: "HTTP 500 (code: reason)" when the body carries an error object,
    // and "Service request failed.\nStatus: 429 (reason)" when it does not.
    private static readonly string[] StatusMarkers = ["HTTP ", "Status: "];

    /// <summary>
    /// 5xx, 429 and 408 are transient (<see cref="ErrorCodes.LongRest.ProviderUnavailable"/>); any other status that is not a
    /// success is a rejection retrying cannot fix (<see cref="ErrorCodes.LongRest.ProviderRejected"/>). With no status, a failure
    /// of the connection attempt itself, before any request byte could leave, is
    /// <see cref="ErrorCodes.LongRest.ProviderUnreachable"/>: beneath an <see cref="HttpRequestException"/>, a socket failure
    /// (connecting, resolving) or a TLS handshake failure (<see cref="AuthenticationException"/>) reached without passing through an
    /// <see cref="IOException"/>, which is how the runtime reports a failure on a connection that was already established.
    /// Anything else is a transient provider failure: the call ended without a result, so the sweep may be readmitted later. A
    /// dropped established connection is deliberately never unreachable, because it may have been served.
    /// </summary>
    internal static Error Classify(Exception exception)
    {
        int status = 0;

        bool http = false;

        bool established = false;

        bool unreachable = false;

        Exception? current = exception;

        for (int depth = 0; current is not null && depth < MaximumDepth; depth++)
        {
            if (current is ClientResultException)
            {
                if (status == 0)
                {
                    status = ParseStatus(current.Message);
                }
            }
            else if (current is HttpRequestException)
            {
                http = true;
            }
            else if (current is IOException)
            {
                established = true;
            }
            else if (http && !established && current is SocketException or AuthenticationException)
            {
                unreachable = true;
            }

            current = current.InnerException;
        }

        if (status != 0)
        {
            return status is 408 or 429 or >= 500
                ? new Error(ErrorCodes.LongRest.ProviderUnavailable, "The Long Rest provider could not complete the call right now.")
                : new Error(ErrorCodes.LongRest.ProviderRejected, "The Long Rest provider rejected the call.");
        }

        return unreachable
            ? new Error(ErrorCodes.LongRest.ProviderUnreachable, "The Long Rest provider could not be reached.")
            : new Error(ErrorCodes.LongRest.ProviderUnavailable, "The Long Rest provider could not complete the call right now.");
    }

    // Zero when the message carries no status head. Only a three-digit value in the HTTP range after a marker is accepted.
    private static int ParseStatus(string message)
    {
        foreach (string marker in StatusMarkers)
        {
            int at = message.IndexOf(marker, StringComparison.Ordinal);

            if (at < 0) continue;

            int start = at + marker.Length;

            if (message.Length >= start + 3
                && int.TryParse(message.AsSpan(start, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int status)
                && status is >= 100 and <= 599)
            {
                return status;
            }
        }

        return 0;
    }
}
