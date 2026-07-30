using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

internal static class WebResearchTelemetry
{
    public const string SearchOperation = "search";

    public const string ReadUrlOperation = "read_url";

    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    public static void RecordOperation(
        string provider,
        string operation,
        string outcome,
        long startTimestamp)
    {
        TagList tags = new()
        {
            { "provider", provider },
            { "operation", operation },
            { "outcome", outcome },
        };

        ArcanumMetrics.WebResearchRequestsTotal.Add(1, tags);
        ArcanumMetrics.WebResearchDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
            tags);
    }

    public static void RecordUsage(
        string provider,
        string model,
        long promptTokens,
        long completionTokens,
        long totalTokens,
        long reasoningTokens,
        long citationTokens,
        int searchQueries,
        decimal? costUsd)
    {
        TagList baseTags = new()
        {
            { "provider", provider },
            { "model", model },
        };

        RecordTokenKind(baseTags, "prompt", promptTokens);
        RecordTokenKind(baseTags, "completion", completionTokens);
        RecordTokenKind(baseTags, "total", totalTokens);
        RecordTokenKind(baseTags, "reasoning", reasoningTokens);
        RecordTokenKind(baseTags, "citation", citationTokens);

        if (searchQueries > 0)
        {
            ArcanumMetrics.WebResearchSearchQueriesTotal.Add(
                searchQueries,
                baseTags);
        }

        if (costUsd is >= 0)
        {
            ArcanumMetrics.WebResearchCostUsdTotal.Add(
                decimal.ToDouble(costUsd.Value),
                baseTags);
        }
    }

    public static string OutcomeFor(Error error) =>
        error.Code switch
        {
            ErrorCodes.WebResearch.MissingCredential => "missing_credential",
            ErrorCodes.WebResearch.AuthenticationOrCreditsFailed => "authentication_or_credits",
            ErrorCodes.WebResearch.QuotaExhausted => "quota_exhausted",
            ErrorCodes.WebResearch.RateLimited => "rate_limited",
            ErrorCodes.WebResearch.RequestRejected => "request_rejected",
            ErrorCodes.WebResearch.ProviderUnavailable => "provider_unavailable",
            ErrorCodes.WebResearch.InvalidResponse => "invalid_response",
            ErrorCodes.WebResearch.Timeout => "timeout",
            ErrorCodes.WebResearch.InvalidUrl => "invalid_url",
            ErrorCodes.WebResearch.SsrfBlocked => "ssrf_blocked",
            ErrorCodes.WebResearch.RedirectLimitExceeded => "redirect_limit",
            ErrorCodes.WebResearch.BotProtected => "bot_protected",
            ErrorCodes.WebResearch.JavaScriptRequired => "javascript_required",
            ErrorCodes.WebResearch.EmptyContent => "empty_content",
            ErrorCodes.WebResearch.UnsupportedContent => "unsupported_content",
            ErrorCodes.WebResearch.ResponseTooLarge => "response_too_large",
            ErrorCodes.WebResearch.UnsupportedOperation => "unsupported_operation",
            _ => "error",
        };

    private static void RecordTokenKind(
        TagList baseTags,
        string kind,
        long value)
    {
        if (value <= 0)
        {
            return;
        }

        TagList tags = baseTags;
        tags.Add("kind", kind);
        ArcanumMetrics.WebResearchTokensTotal.Add(value, tags);
    }
}
