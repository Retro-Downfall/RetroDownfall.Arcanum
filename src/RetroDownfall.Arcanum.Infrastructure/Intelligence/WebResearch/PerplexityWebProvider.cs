using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

/// <summary>
/// Native, non-streaming Perplexity Sonar search provider.
/// </summary>
public sealed class PerplexityWebProvider : IWebResearchProvider
{
    private const int MaxErrorBodyBytes = 32 * 1024;

    private static readonly HashSet<string> QuotaErrorCodes = new(
        [
            "insufficient_credits",
            "insufficient_credit",
            "insufficient_quota",
            "quota_exhausted",
            "credit_balance_exhausted",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebResearchApiKeyResolver _apiKeyResolver;
    private readonly ILogger<PerplexityWebProvider>? _logger;

    public PerplexityWebProvider(
        IHttpClientFactory httpClientFactory,
        IWebResearchApiKeyResolver apiKeyResolver,
        ILogger<PerplexityWebProvider>? logger = null)
    {
        _httpClientFactory = httpClientFactory
            ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _apiKeyResolver = apiKeyResolver
            ?? throw new ArgumentNullException(nameof(apiKeyResolver));
        _logger = logger;
    }

    public string ProviderName => WebResearchProviderNames.Perplexity;

    public WebResearchCapabilities Capabilities =>
        WebResearchCapabilities.Search;

    public async Task<Result<WebSearchResult>> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        long started = WebResearchTelemetry.StartTimestamp();
        string outcome = "error";

        Result<WebSearchResult> Complete(Result<WebSearchResult> result)
        {
            outcome = result.IsSuccess
                ? "success"
                : WebResearchTelemetry.OutcomeFor(result.Error);
            return result;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(query)
                || query.Length > WebResearchConstants.MaxInputQueryChars)
            {
                return Complete(Failure(
                    ErrorCodes.WebResearch.RequestRejected,
                    "A non-empty search query within the permitted length is required."));
            }

            if (!ValidateOptions(options, out Error optionsError))
            {
                return Complete(Result<WebSearchResult>.Failure(optionsError));
            }

            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            deadline.CancelAfter(options.Timeout);

            try
            {
                string? apiKey = await _apiKeyResolver
                    .ResolveApiKeyAsync(ProviderName, deadline.Token)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return Complete(Failure(
                        ErrorCodes.WebResearch.MissingCredential,
                        "A Perplexity API key is not configured."));
                }

                PerplexityRequest payload = new()
                {
                    Model = NormalizeModel(options.Model),
                    Messages =
                    [
                        new PerplexityMessage
                        {
                            Role = "user",
                            Content = query.Trim(),
                        },
                    ],
                    MaxTokens = Math.Clamp(
                        Math.Max(1, options.MaxAnswerBytes / 4),
                        1,
                        128_000),
                    Stream = false,

                    SearchRecencyFilter = NormalizeFreshness(options.Freshness),

                    SearchDomainFilter = BuildDomainFilter(options),
                };

                byte[] requestJson = JsonSerializer.SerializeToUtf8Bytes(
                    payload,
                    WebResearchJsonContext.Default.PerplexityRequest);

                using HttpRequestMessage request = new(
                    HttpMethod.Post,
                    WebResearchConstants.PerplexityEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    apiKey.Trim());
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new ByteArrayContent(requestJson);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json")
                    {
                        CharSet = "utf-8",
                    };

                HttpClient client = _httpClientFactory.CreateClient(
                    WebResearchConstants.PerplexityHttpClientName);

                using HttpResponseMessage response = await client
                    .SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    byte[] errorBody = await ReadErrorBodyAsync(
                        response,
                        deadline.Token).ConfigureAwait(false);
                    Error error = ClassifyHttpFailure(
                        response.StatusCode,
                        errorBody);
                    return Complete(Result<WebSearchResult>.Failure(error));
                }

                if (response.Content.Headers.ContentLength
                    is long declaredLength
                    && declaredLength > options.MaxResponseBytes)
                {
                    return Complete(Failure(
                        ErrorCodes.WebResearch.ResponseTooLarge,
                        "The Perplexity response exceeded the permitted size."));
                }

                await using Stream responseStream = await response.Content
                    .ReadAsStreamAsync(deadline.Token)
                    .ConfigureAwait(false);
                WebResearchBounds.CappedBody body =
                    await WebResearchBounds.ReadCappedAsync(
                        responseStream,
                        options.MaxResponseBytes,
                        deadline.Token).ConfigureAwait(false);

                if (body.Truncated)
                {
                    return Complete(Failure(
                        ErrorCodes.WebResearch.ResponseTooLarge,
                        "The Perplexity response exceeded the permitted size."));
                }

                PerplexityResponse? parsed;

                try
                {
                    parsed = JsonSerializer.Deserialize(
                        body.Bytes,
                        WebResearchJsonContext.Default.PerplexityResponse);
                }
                catch (JsonException)
                {
                    return Complete(Failure(
                        ErrorCodes.WebResearch.InvalidResponse,
                        "Perplexity returned an invalid response."));
                }

                Result<WebSearchResult> mapped = MapResponse(
                    parsed,
                    options);
                deadline.Token.ThrowIfCancellationRequested();

                if (mapped.IsSuccess)
                {
                    WebResearchUsage usage = mapped.Value.Usage;
                    WebResearchTelemetry.RecordUsage(
                        ProviderName,
                        NormalizeModel(options.Model),
                        usage.PromptTokens,
                        usage.CompletionTokens,
                        usage.TotalTokens,
                        usage.ReasoningTokens,
                        usage.CitationTokens,
                        usage.SearchQueries,
                        usage.CostUsd);
                }

                return Complete(mapped);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            catch (OperationCanceledException)
            {
                return Complete(Failure(
                    ErrorCodes.WebResearch.Timeout,
                    "The Perplexity request timed out."));
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogWarning(
                    "Perplexity web search failed ({ExceptionType}).",
                    ex.GetType().Name);

                return Complete(Failure(
                    ErrorCodes.WebResearch.ProviderUnavailable,
                    "Perplexity is currently unavailable."));
            }
        }
        finally
        {
            WebResearchTelemetry.RecordOperation(
                ProviderName,
                WebResearchTelemetry.SearchOperation,
                outcome,
                started);
        }
    }

    public Task<Result<WebReadResult>> ReadUrlAsync(
        string url,
        WebReadOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = WebResearchTelemetry.StartTimestamp();
        Result<WebReadResult> result = Result<WebReadResult>.Failure(
            new Error(
                ErrorCodes.WebResearch.UnsupportedOperation,
                "The Perplexity provider does not support direct URL reading."));

        WebResearchTelemetry.RecordOperation(
            ProviderName,
            WebResearchTelemetry.ReadUrlOperation,
            WebResearchTelemetry.OutcomeFor(result.Error),
            started);
        return Task.FromResult(result);
    }

    private static Result<WebSearchResult> MapResponse(
        PerplexityResponse? response,
        WebSearchOptions options)
    {
        string? rawAnswer = response?.Choices?
            .FirstOrDefault()?
            .Message?
            .Content;

        if (string.IsNullOrWhiteSpace(rawAnswer))
        {
            return Failure(
                ErrorCodes.WebResearch.InvalidResponse,
                "Perplexity returned an empty response.");
        }

        string answer = WebResearchBounds.TruncateUtf8(
            rawAnswer.Trim(),
            options.MaxAnswerBytes,
            out bool answerTruncated);

        string[] citationUrls = response!.Citations is { Length: > 0 }
            ? response.Citations
            : response.SearchResults?
                .Select(static result => result.Url ?? string.Empty)
                .ToArray()
                ?? [];

        List<WebCitation> citations = new(
            Math.Min(citationUrls.Length, options.MaxCitations));

        for (int index = 0; index < citationUrls.Length; index++)
        {
            string? rawUrl = citationUrls[index];

            if (citations.Count >= options.MaxCitations
                || string.IsNullOrWhiteSpace(rawUrl))
            {
                continue;
            }

            string url = rawUrl.Trim();

            if (url.Length > options.MaxCitationUrlChars
                || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp
                    && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            PerplexitySearchResult? metadata = response.SearchResults?
                .FirstOrDefault(
                    result => string.Equals(
                        result.Url?.Trim(),
                        url,
                        StringComparison.Ordinal));

            string? title = BoundOptional(metadata?.Title, 2_048);
            string? publishedDate = BoundOptional(metadata?.Date, 256);

            citations.Add(
                new WebCitation(
                    index + 1,
                    uri.AbsoluteUri,
                    title,
                    publishedDate));
        }

        int omittedCitationCount = citationUrls.Length - citations.Count;
        PerplexityUsage? reported = response.Usage;
        long promptTokens = NonNegative(reported?.PromptTokens ?? 0);
        long completionTokens = NonNegative(
            reported?.CompletionTokens ?? 0);
        long reportedTotal = NonNegative(reported?.TotalTokens ?? 0);
        long calculatedTotal;

        try
        {
            calculatedTotal = checked(promptTokens + completionTokens);
        }
        catch (OverflowException)
        {
            calculatedTotal = long.MaxValue;
        }

        WebResearchUsage usage = new(
            promptTokens,
            completionTokens,
            Math.Max(reportedTotal, calculatedTotal),
            NonNegative(reported?.ReasoningTokens ?? 0),
            NonNegative(reported?.CitationTokens ?? 0),
            Math.Max(0, reported?.SearchQueries ?? 0),
            reported?.Cost?.TotalCost is >= 0
                ? reported.Cost.TotalCost
                : null);

        return Result<WebSearchResult>.Success(
            new WebSearchResult(
                answer,
                [.. citations],
                usage,
                answerTruncated || omittedCitationCount > 0,
                omittedCitationCount));
    }

    private static bool ValidateOptions(
        WebSearchOptions options,
        out Error error)
    {
        if (!WebResearchModels.IsSupportedPerplexityModel(options.Model))
        {
            error = new Error(
                ErrorCodes.WebResearch.RequestRejected,
                "The requested Perplexity model is not supported.");
            return false;
        }

        if (options.Timeout <= TimeSpan.Zero
            || options.MaxResponseBytes <= 0
            || options.MaxAnswerBytes <= 0
            || options.MaxCitations < 0
            || options.MaxCitationUrlChars <= 0
            || options.ResultCount is < 1 or > 20
            || !IsValidFreshness(options.Freshness)
            || options.IncludeDomains.Length > 20
            || options.ExcludeDomains.Length > 20
            || options.IncludeDomains.Any(static domain => !IsValidDomain(domain))
            || options.ExcludeDomains.Any(static domain => !IsValidDomain(domain)))
        {
            error = new Error(
                ErrorCodes.WebResearch.RequestRejected,
                "Web-search limits are invalid.");
            return false;
        }

        error = Error.None;
        return true;
    }

    private static string? NormalizeFreshness(string? freshness) =>
        string.IsNullOrWhiteSpace(freshness)
            ? null
            : freshness.Trim().ToLowerInvariant();

    private static bool IsValidFreshness(string? freshness) =>
        string.IsNullOrWhiteSpace(freshness)
        || freshness.Trim().ToLowerInvariant()
            is "day" or "week" or "month" or "year";

    private static bool IsValidDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)
            || domain.Length > 253
            || domain.Contains('/')
            || domain.Contains(':')
            || domain.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return Uri.CheckHostName(domain.Trim().TrimStart('.'))
            is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static string[]? BuildDomainFilter(WebSearchOptions options)
    {
        string[] domains = options.IncludeDomains
            .Select(static domain => domain.Trim().TrimStart('.'))
            .Concat(
                options.ExcludeDomains.Select(
                    static domain => "-" + domain.Trim().TrimStart('.')))
            .ToArray();

        return domains.Length == 0 ? null : domains;
    }

    private static async Task<byte[]> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        WebResearchBounds.CappedBody body =
            await WebResearchBounds.ReadCappedAsync(
                responseStream,
                MaxErrorBodyBytes,
                cancellationToken).ConfigureAwait(false);
        return body.Bytes;
    }

    private static Error ClassifyHttpFailure(
        HttpStatusCode statusCode,
        byte[] errorBody)
    {
        if (HasQuotaErrorCode(errorBody)
            || statusCode == HttpStatusCode.PaymentRequired)
        {
            return new Error(
                ErrorCodes.WebResearch.QuotaExhausted,
                "The Perplexity account has no available quota.");
        }

        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new Error(
                    ErrorCodes.WebResearch.AuthenticationOrCreditsFailed,
                    "Perplexity rejected the credential or the account has no available credits."),

            HttpStatusCode.TooManyRequests =>
                new Error(
                    ErrorCodes.WebResearch.RateLimited,
                    "Perplexity rate-limited the request."),

            HttpStatusCode.BadRequest
                or HttpStatusCode.UnprocessableEntity =>
                new Error(
                    ErrorCodes.WebResearch.RequestRejected,
                    "Perplexity rejected the search request."),

            >= HttpStatusCode.InternalServerError =>
                new Error(
                    ErrorCodes.WebResearch.ProviderUnavailable,
                    "Perplexity is currently unavailable."),

            _ => new Error(
                ErrorCodes.WebResearch.ProviderUnavailable,
                "Perplexity could not complete the request."),
        };
    }

    private static bool HasQuotaErrorCode(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            PerplexityErrorResponse? response = JsonSerializer.Deserialize(
                body,
                WebResearchJsonContext.Default.PerplexityErrorResponse);
            string? code = response?.Error?.Code;
            string? type = response?.Error?.Type;

            return (code is not null && QuotaErrorCodes.Contains(code))
                || (type is not null && QuotaErrorCodes.Contains(type));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeModel(string model) =>
        string.Equals(
            model.Trim(),
            WebResearchModels.SonarPro,
            StringComparison.OrdinalIgnoreCase)
            ? WebResearchModels.SonarPro
            : WebResearchModels.Sonar;

    private static string? BoundOptional(string? value, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return WebResearchBounds.TruncateUtf8(value.Trim(), maxBytes);
    }

    private static long NonNegative(long value) => Math.Max(0, value);

    private static Result<WebSearchResult> Failure(
        string code,
        string message) =>
        Result<WebSearchResult>.Failure(new Error(code, message));
}
