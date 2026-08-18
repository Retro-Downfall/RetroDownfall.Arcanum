using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// Appends <see cref="ArcanumApiClient.StreamDoctorHint" /> to the transport copies
/// <see cref="ArcanumApiClient" /> raises, so every streaming command names the same next step for
/// the same failure. Anything else is returned untouched: a provider or policy message already
/// carries its own remedy, and bolting "run arcanum doctor" onto it points the operator at the wrong
/// subsystem.
/// </summary>
internal static class CliStreamTransportHint
{

    public static string Append(string? message)
    {

        if (string.IsNullOrWhiteSpace(message))
        {

            return $"{ArcanumApiClient.StreamEmptyResultMessage} {ArcanumApiClient.StreamDoctorHint}";

        }

        return IsTransportCopy(message)
            ? $"{message} {ArcanumApiClient.StreamDoctorHint}"
            : message;

    }

    /// <summary>
    /// The same, for a stream frame that also carries a typed error code. The code is the load-bearing
    /// signal — a copy edit upstream must not silently drop the hint — and the message match stays as
    /// the fallback for the frames that carry no code.
    /// </summary>
    public static string Append(string? code, string? message)
    {

        if (!string.IsNullOrWhiteSpace(message)
            && code is ErrorCodes.Connection.Timeout or ErrorCodes.Connection.Unreachable)
        {

            return $"{message} {ArcanumApiClient.StreamDoctorHint}";

        }

        return Append(message);

    }

    private static bool IsTransportCopy(string message) =>
        string.Equals(message, ArcanumApiClient.StreamTimeoutMessage, StringComparison.Ordinal)
        || string.Equals(message, ArcanumApiClient.StreamDisconnectMessage, StringComparison.Ordinal)
        || string.Equals(message, ArcanumApiClient.StreamUnreachableMessage, StringComparison.Ordinal)
        || string.Equals(message, ArcanumApiClient.StreamEmptyResultMessage, StringComparison.Ordinal);

}
