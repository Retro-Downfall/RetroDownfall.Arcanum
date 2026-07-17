using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.TheForge.Ux.ViewModels;

/// <summary>
/// Formats <see cref="ApiResponse{T}"/> failures for Forge UI surfaces as <c>[Code] Message</c>,
/// with curated copy for known transport / auth fault codes (so raw socket text is not shown).
/// </summary>
public static class ForgeApiError
{

    private const string DefaultFallback = "The request failed.";

    private const string TimeoutMessage = "The request to Arcanum timed out.";

    private const string UnreachableMessage = "Arcanum is unreachable. Is the API running?";

    private const string MissingApiKeyMessage = "No API key found. Connect and enter an API key.";

    /// <summary>
    /// Formats a failed (or null) API envelope. Prefer calling only on unsuccessful responses;
    /// successful envelopes return <paramref name="fallback"/>.
    /// </summary>
    public static string From<T>(ApiResponse<T>? response, string? fallback = null)
    {

        string effectiveFallback = string.IsNullOrWhiteSpace(fallback) ? DefaultFallback : fallback;

        if (response is null || response.IsSuccess)
        {

            return effectiveFallback;

        }

        return From(response.Error, effectiveFallback);

    }

    /// <summary>
    /// Formats an <see cref="Error"/> as <c>[Code] Message</c>, applying curated transport fallbacks
    /// for known connection / auth codes.
    /// </summary>
    public static string From(Error? error, string? fallback = null)
    {

        string effectiveFallback = string.IsNullOrWhiteSpace(fallback) ? DefaultFallback : fallback;

        if (error is null)
        {

            return effectiveFallback;

        }

        Error value = error.Value;

        string code = value.Code ?? string.Empty;

        string message = CurateMessage(code) ?? value.Message ?? string.Empty;

        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(message))
        {

            return effectiveFallback;

        }

        if (string.IsNullOrEmpty(code))
        {

            return string.IsNullOrEmpty(message) ? effectiveFallback : message;

        }

        if (string.IsNullOrEmpty(message))
        {

            return $"[{code}] {effectiveFallback}";

        }

        return $"[{code}] {message}";

    }

    private static string? CurateMessage(string code)
    {

        if (string.Equals(code, ErrorCodes.Connection.Timeout, StringComparison.Ordinal))
        {

            return TimeoutMessage;

        }

        if (string.Equals(code, ErrorCodes.Connection.Unreachable, StringComparison.Ordinal)
            || string.Equals(code, "Connection.Failed", StringComparison.Ordinal))
        {

            return UnreachableMessage;

        }

        if (string.Equals(code, ErrorCodes.Security.MissingApiKey, StringComparison.Ordinal))
        {

            return MissingApiKeyMessage;

        }

        return null;

    }

}
