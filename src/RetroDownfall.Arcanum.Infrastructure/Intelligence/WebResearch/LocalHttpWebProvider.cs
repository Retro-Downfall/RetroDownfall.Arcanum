using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

/// <summary>
/// Direct, SSRF-guarded reader for static HTML and other bounded textual resources.
/// </summary>
public sealed class LocalHttpWebProvider : IWebResearchProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebPageContentExtractor _extractor;
    private readonly ILogger<LocalHttpWebProvider>? _logger;

    public LocalHttpWebProvider(
        IHttpClientFactory httpClientFactory,
        WebPageContentExtractor extractor,
        ILogger<LocalHttpWebProvider>? logger = null)
    {
        _httpClientFactory = httpClientFactory
            ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _extractor = extractor
            ?? throw new ArgumentNullException(nameof(extractor));
        _logger = logger;
    }

    public string ProviderName => WebResearchProviderNames.LocalHttp;

    public WebResearchCapabilities Capabilities =>
        WebResearchCapabilities.ReadUrl;

    public Task<Result<WebSearchResult>> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = WebResearchTelemetry.StartTimestamp();
        Result<WebSearchResult> result = Result<WebSearchResult>.Failure(
            new Error(
                ErrorCodes.WebResearch.UnsupportedOperation,
                "The local HTTP provider does not support synthesized search."));

        WebResearchTelemetry.RecordOperation(
            ProviderName,
            WebResearchTelemetry.SearchOperation,
            WebResearchTelemetry.OutcomeFor(result.Error),
            started);
        return Task.FromResult(result);
    }

    public async Task<Result<WebReadResult>> ReadUrlAsync(
        string url,
        WebReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        long started = WebResearchTelemetry.StartTimestamp();
        string outcome = "error";

        Result<WebReadResult> Complete(Result<WebReadResult> result)
        {
            outcome = result.IsSuccess
                ? "success"
                : WebResearchTelemetry.OutcomeFor(result.Error);
            return result;
        }

        try
        {
            if (!TryParseInputUrl(url, out Uri? target, out Error urlError))
            {
                return Complete(Result<WebReadResult>.Failure(urlError));
            }

            if (!ValidateOptions(options, out Error optionsError))
            {
                return Complete(Result<WebReadResult>.Failure(optionsError));
            }

            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            try
            {
                HttpClient client = _httpClientFactory.CreateClient(
                    WebResearchConstants.LocalHttpClientName);
                HashSet<string> visited = new(
                    StringComparer.OrdinalIgnoreCase)
                {
                    target.AbsoluteUri,
                };
                Uri current = target;
                int redirectCount = 0;

                while (true)
                {
                    Result outbound = await OutboundUrlGuard
                        .ValidateUntrustedUrlAsync(
                            current.AbsoluteUri,
                            deadline.Token)
                        .ConfigureAwait(false);

                    if (outbound.IsFailure)
                    {
                        return Complete(Failure(
                            ErrorCodes.WebResearch.SsrfBlocked,
                            "The URL was rejected by the outbound network policy."));
                    }

                    using HttpRequestMessage request = new(
                        HttpMethod.Get,
                        current);
                    request.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("text/html"));
                    request.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue(
                            "text/plain",
                            0.9));

                    using CancellationTokenSource headerIdle =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            deadline.Token);

                    headerIdle.CancelAfter(options.IdleTimeout);

                    using HttpResponseMessage response = await client
                        .SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            headerIdle.Token)
                        .ConfigureAwait(false);

                    if (OutboundUrlGuard.IsRedirectStatusCode(
                            response.StatusCode))
                    {
                        if (redirectCount >= options.MaxRedirects)
                        {
                            return Complete(Failure(
                                ErrorCodes.WebResearch.RedirectLimitExceeded,
                                "The page exceeded the permitted redirect limit."));
                        }

                        Result<string> redirect =
                            OutboundUrlGuard.ResolveRedirectLocation(
                                current,
                                response.Headers.Location?.ToString());

                        if (redirect.IsFailure
                            || redirect.Value.Length
                                > WebResearchConstants.MaxInputUrlChars
                            || !Uri.TryCreate(
                                redirect.Value,
                                UriKind.Absolute,
                                out Uri? redirected))
                        {
                            return Complete(Failure(
                                ErrorCodes.WebResearch.InvalidUrl,
                                "The server returned an invalid redirect URL."));
                        }

                        if (!visited.Add(redirected.AbsoluteUri))
                        {
                            return Complete(Failure(
                                ErrorCodes.WebResearch.RedirectLimitExceeded,
                                "The page entered a redirect cycle."));
                        }

                        current = redirected;
                        redirectCount++;
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return Complete(Failure(
                            ErrorCodes.WebResearch.BotProtected,
                            "The page refused direct access. Use web_search instead."));
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        return Complete(ClassifyHttpFailure(
                            response.StatusCode));
                    }

                    if (!IsSupportedContentType(
                            response.Content.Headers.ContentType))
                    {
                        return Complete(Failure(
                            ErrorCodes.WebResearch.UnsupportedContent,
                            "The URL returned a non-textual content type."));
                    }

                    if (response.Content.Headers.ContentLength
                        is long declaredLength
                        && declaredLength > options.MaxResponseBytes)
                    {
                        return Complete(Failure(
                            ErrorCodes.WebResearch.ResponseTooLarge,
                            "The page exceeded the permitted response size."));
                    }

                    await using Stream responseStream =
                        await response.Content
                            .ReadAsStreamAsync(deadline.Token)
                            .ConfigureAwait(false);
                    WebResearchBounds.CappedBody body =
                        await WebResearchBounds.ReadCappedAsync(
                            responseStream,
                            options.MaxResponseBytes,
                            options.IdleTimeout,
                            deadline.Token).ConfigureAwait(false);

                    if (body.Truncated)
                    {
                        return Complete(Failure(
                            ErrorCodes.WebResearch.ResponseTooLarge,
                            "The page exceeded the permitted response size."));
                    }

                    Encoding encoding = ResolveEncoding(
                        response.Content.Headers.ContentType);
                    string decoded = encoding.GetString(body.Bytes);
                    deadline.Token.ThrowIfCancellationRequested();
                    bool isHtml = IsHtml(
                        response.Content.Headers.ContentType,
                        decoded);

                    Result<WebReadResult> extracted = isHtml
                        ? ExtractHtml(decoded, current, options)
                        : ExtractText(decoded, current, options);
                    deadline.Token.ThrowIfCancellationRequested();
                    return Complete(extracted);
                }
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
                    "The page connection or response stream exceeded its idle I/O timeout; retry or cancel the read."));
            }
            catch (HttpRequestException ex)
                when (IsOutboundPolicyFailure(ex))
            {
                return Complete(Failure(
                    ErrorCodes.WebResearch.SsrfBlocked,
                    "The URL was rejected by the outbound network policy."));
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogWarning(
                    "Direct web-page retrieval failed ({ExceptionType}).",
                    ex.GetType().Name);

                return Complete(Failure(
                    ErrorCodes.WebResearch.ProviderUnavailable,
                    "The page is currently unavailable."));
            }
        }
        finally
        {
            WebResearchTelemetry.RecordOperation(
                ProviderName,
                WebResearchTelemetry.ReadUrlOperation,
                outcome,
                started);
        }
    }

    private Result<WebReadResult> ExtractHtml(
        string html,
        Uri finalUri,
        WebReadOptions options)
    {
        WebPageExtractionResult extracted = _extractor.Extract(
            html,
            finalUri,
            options.MaxContentBytes,
            options.MaxLinks,
            options.MaxLinkUrlChars);

        if (extracted.IsJavaScriptShell)
        {
            return Failure(
                ErrorCodes.WebResearch.JavaScriptRequired,
                "The page requires JavaScript to render. Use web_search instead.");
        }

        if (string.IsNullOrWhiteSpace(extracted.Markdown))
        {
            return Failure(
                ErrorCodes.WebResearch.EmptyContent,
                "The page did not contain readable text.");
        }

        WebLink[] links = extracted.Links
            .Select(static link => new WebLink(link.Text, link.Url))
            .ToArray();

        return Result<WebReadResult>.Success(
            new WebReadResult(
                string.IsNullOrWhiteSpace(extracted.Title)
                    ? null
                    : extracted.Title,
                extracted.Markdown,
                finalUri.AbsoluteUri,
                links,
                extracted.Truncated,
                extracted.OmittedLinkCount));
    }

    private static Result<WebReadResult> ExtractText(
        string text,
        Uri finalUri,
        WebReadOptions options)
    {
        string normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (normalized.Length == 0)
        {
            return Failure(
                ErrorCodes.WebResearch.EmptyContent,
                "The page did not contain readable text.");
        }

        string markdown = WebResearchBounds.TruncateUtf8(
            normalized,
            options.MaxContentBytes,
            out bool truncated);

        return Result<WebReadResult>.Success(
            new WebReadResult(
                null,
                markdown,
                finalUri.AbsoluteUri,
                [],
                truncated,
                0));
    }

    private static bool TryParseInputUrl(
        string? url,
        [NotNullWhen(true)] out Uri? parsed,
        out Error error)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(url)
            || url.Length > WebResearchConstants.MaxInputUrlChars
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? candidate)
            || (candidate.Scheme != Uri.UriSchemeHttp
                && candidate.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(candidate.Host))
        {
            error = new Error(
                ErrorCodes.WebResearch.InvalidUrl,
                "A valid absolute HTTP or HTTPS URL is required.");
            return false;
        }

        parsed = candidate;
        error = Error.None;
        return true;
    }

    private static bool ValidateOptions(
        WebReadOptions options,
        out Error error)
    {
        if (options.IdleTimeout <= TimeSpan.Zero
            || options.MaxResponseBytes <= 0
            || options.MaxContentBytes <= 0
            || options.MaxLinks < 0
            || options.MaxLinkUrlChars <= 0
            || options.MaxRedirects < 0)
        {
            error = new Error(
                ErrorCodes.WebResearch.RequestRejected,
                "URL-reading limits are invalid.");
            return false;
        }

        error = Error.None;
        return true;
    }

    private static bool IsSupportedContentType(
        MediaTypeHeaderValue? contentType)
    {
        string? mediaType = contentType?.MediaType;

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return true;
        }

        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return mediaType.Equals(
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals(
                "application/xml",
                StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals(
                "application/xhtml+xml",
                StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals(
                "application/rss+xml",
                StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals(
                "application/atom+xml",
                StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith(
                "+json",
                StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith(
                "+xml",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHtml(
        MediaTypeHeaderValue? contentType,
        string text)
    {
        string? mediaType = contentType?.MediaType;

        if (mediaType?.Contains(
                "html",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = text.AsSpan().TrimStart();

        return trimmed.StartsWith(
                "<!doctype html",
                StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(
                "<html",
                StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding ResolveEncoding(
        MediaTypeHeaderValue? contentType)
    {
        string charset = contentType?.CharSet?
            .Trim()
            .Trim('"')
            .ToLowerInvariant()
            ?? string.Empty;

        return charset switch
        {
            "ascii" or "us-ascii" => Encoding.ASCII,
            "iso-8859-1" or "latin1" => Encoding.Latin1,
            "unicode" or "utf-16" or "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };
    }

    private static Result<WebReadResult> ClassifyHttpFailure(
        HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.GatewayTimeout)
        {
            return Failure(
                ErrorCodes.WebResearch.Timeout,
                "The page request timed out.");
        }

        if (statusCode >= HttpStatusCode.InternalServerError)
        {
            return Failure(
                ErrorCodes.WebResearch.ProviderUnavailable,
                "The remote server is currently unavailable.");
        }

        return Failure(
            ErrorCodes.WebResearch.RequestRejected,
            $"The remote server returned HTTP {(int)statusCode}.");
    }

    private static bool IsOutboundPolicyFailure(
        HttpRequestException exception)
    {
        string message = exception.Message;

        return message.Contains(
                "not permitted",
                StringComparison.OrdinalIgnoreCase)
            || message.Contains(
                "loopback, private, or link-local",
                StringComparison.OrdinalIgnoreCase);
    }

    private static Result<WebReadResult> Failure(
        string code,
        string message) =>
        Result<WebReadResult>.Failure(new Error(code, message));
}
