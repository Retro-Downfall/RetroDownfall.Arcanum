using System.ClientModel;
using System.Net.Sockets;
using System.Security.Authentication;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Classifies transport failures using only types and structured HTTP status; exception text is never inspected.</summary>
internal static class LongRestProviderFailure
{
    private const int MaximumDepth = 8;

    /// <summary>
    /// 5xx, 429 and 408 are transient (<see cref="ErrorCodes.LongRest.ProviderUnavailable"/>); any other status that is not a
    /// success is a rejection retrying cannot fix (<see cref="ErrorCodes.LongRest.ProviderRejected"/>). With no status, a failure
    /// of the connection attempt itself, before any request byte could leave, is
    /// <see cref="ErrorCodes.LongRest.ProviderUnreachable"/>. Two shapes prove it, both reached without passing through an
    /// <see cref="IOException"/>, which is how the runtime reports a failure on a connection that was already established: the
    /// egress guard's own <see cref="OutboundConnectException"/> (the host did not resolve, an address was refused by policy, the
    /// guard's connect bound elapsed, or every address refused the connection; the guard consumes the resolver's socket error, so
    /// only its type survives), and, beneath an <see cref="HttpRequestException"/>, a socket failure
    /// (<see cref="SocketException"/>) or a TLS handshake failure (<see cref="AuthenticationException"/>).
    /// Anything else is a transient provider failure: the call ended without a result, so the sweep may be readmitted later. A
    /// dropped established connection is deliberately never unreachable, because it may have been served.
    /// </summary>
    internal static Error Classify(Exception exception) => Classify(exception, out _);

    internal static Error Classify(Exception exception, out int? httpStatus)
    {
        int status = 0;

        bool http = false;

        bool established = false;

        bool unreachable = false;

        Exception? current = exception;

        for (int depth = 0; current is not null && depth < MaximumDepth; depth++)
        {
            if (current is ClientResultException response)
            {
                if (status == 0)
                {
                    status = response.Status is >= 100 and <= 599 ? response.Status : 0;
                }
            }
            else if (current is OutboundConnectException)
            {
                // Raised by the egress connect callback before any connection exists.
                http = true;

                if (!established)
                {
                    unreachable = true;
                }
            }
            else if (current is HttpRequestException request)
            {
                if (status == 0 && request.StatusCode is { } code && (int)code is >= 100 and <= 599) status = (int)code;

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

        httpStatus = status == 0 ? null : status;

        if (status != 0)
        {
            string remedy = status switch
            {
                401 or 403 => "Check the configured provider credential and its access permissions.",
                404 => "Check the configured model and endpoint.",
                400 or 413 or 422 => "Check the configured model capabilities and request limits.",
                429 => "Check the provider rate limit and quota before retrying the operation.",
                408 => "Check provider availability before retrying the operation.",
                >= 500 => "Check the provider service before retrying the operation.",
                _ => "Check the provider configuration before retrying the operation.",
            };

            string diagnosis = status is 408 or 429 or >= 500 ? "could not complete" : "rejected";

            return new Error(status is 408 or 429 or >= 500 ? ErrorCodes.LongRest.ProviderUnavailable : ErrorCodes.LongRest.ProviderRejected,
                $"The Long Rest provider {diagnosis} the call (HTTP {status}). {remedy}");
        }

        return unreachable
            ? new Error(ErrorCodes.LongRest.ProviderUnreachable, "The Long Rest provider could not be reached. Check provider availability and network access.")
            : new Error(ErrorCodes.LongRest.ProviderUnavailable, "The Long Rest provider call ended without a result; HTTP status is unknown. Check provider availability before retrying the operation.");
    }

    // Configuration labels, never endpoints or exception text. Bound their size and remove log-control characters.
    internal static string Label(string value) => new(value.Take(128).Select(character => char.IsControl(character) || character is '\u2028' or '\u2029' ? '?' : character).ToArray());
}
