using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

internal static class WebToolAdapterHelpers
{
    public const string InternalErrorCode = "WebResearch.InternalError";

    public static bool TryGetRequiredString(
        AIFunctionArguments arguments,
        string name,
        int maxChars,
        out string value,
        out string? validationMessage)
    {
        value = string.Empty;
        validationMessage = null;

        foreach (KeyValuePair<string, object?> argument in arguments)
        {
            if (!string.Equals(argument.Key, name, StringComparison.Ordinal))
            {
                continue;
            }

            string? candidate = argument.Value switch
            {
                string text => text,
                JsonElement json when json.ValueKind == JsonValueKind.String =>
                    json.GetString(),
                _ => null,
            };

            if (candidate is null)
            {
                validationMessage = $"{name} must be a string.";
                return false;
            }

            candidate = candidate.Trim();

            if (candidate.Length == 0)
            {
                validationMessage = $"{name} is required.";
                return false;
            }

            if (candidate.Length > maxChars)
            {
                validationMessage =
                    $"{name} exceeds the maximum length of {maxChars} characters.";
                return false;
            }

            value = candidate;
            return true;
        }

        validationMessage = $"{name} is required.";
        return false;
    }

    public static string PublicErrorMessage(string errorCode) =>
        errorCode switch
        {
            ErrorCodes.WebResearch.MissingCredential =>
                "The web-search API key is not configured.",
            ErrorCodes.WebResearch.AuthenticationOrCreditsFailed =>
                "The web-search API key is invalid, deleted, or has no available credits.",
            ErrorCodes.WebResearch.QuotaExhausted =>
                "The web-search provider quota is exhausted.",
            ErrorCodes.WebResearch.RateLimited =>
                "The web-search provider rate limit was reached. Try again later.",
            ErrorCodes.WebResearch.RequestRejected =>
                "The web-research provider rejected the request.",
            ErrorCodes.WebResearch.ProviderUnavailable =>
                "The web-research provider is temporarily unavailable.",
            ErrorCodes.WebResearch.InvalidResponse =>
                "The web-research provider returned an invalid response.",
            ErrorCodes.WebResearch.Timeout =>
                "The web-research operation timed out.",
            ErrorCodes.WebResearch.InvalidUrl =>
                "The URL is invalid. Only absolute HTTP and HTTPS URLs are accepted.",
            ErrorCodes.WebResearch.SsrfBlocked =>
                "The URL is not permitted by the outbound network safety policy.",
            ErrorCodes.WebResearch.RedirectLimitExceeded =>
                "The page exceeded the permitted redirect limit.",
            ErrorCodes.WebResearch.BotProtected =>
                "The page denied automated access. Use web_search instead.",
            ErrorCodes.WebResearch.JavaScriptRequired =>
                "The page requires JavaScript to expose its content. Use web_search instead.",
            ErrorCodes.WebResearch.EmptyContent =>
                "The page did not contain readable content.",
            ErrorCodes.WebResearch.UnsupportedContent =>
                "The URL returned an unsupported content type.",
            ErrorCodes.WebResearch.ResponseTooLarge =>
                "The remote response exceeded the permitted size.",
            ErrorCodes.WebResearch.UnsupportedOperation =>
                "The configured provider does not support this operation.",
            _ =>
                "The web-research operation could not be completed.",
        };

    public static bool ShouldSuggestWebSearch(string errorCode) =>
        string.Equals(
            errorCode,
            ErrorCodes.WebResearch.BotProtected,
            StringComparison.Ordinal)
        || string.Equals(
            errorCode,
            ErrorCodes.WebResearch.JavaScriptRequired,
            StringComparison.Ordinal);

    public static bool IsAbsoluteHttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    public static int SaturatingCount(int retainedCount, int omittedCount)
    {
        omittedCount = Math.Max(0, omittedCount);

        return retainedCount > int.MaxValue - omittedCount
            ? int.MaxValue
            : retainedCount + omittedCount;
    }
}
