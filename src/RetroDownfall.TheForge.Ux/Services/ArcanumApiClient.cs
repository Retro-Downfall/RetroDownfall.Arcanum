using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Wraps the named <c>"ArcanumApi"</c> <see cref="HttpClient"/> (from <see cref="IHttpClientFactory"/>)
/// with the <c>X-Arcanum-Key</c> header, <see cref="ApiResponse{T}"/> envelope handling, and the two
/// Arcanum streaming shapes: NDJSON (<see cref="PostNdjsonStreamAsync{TRequest,TFrame}"/>) and SSE
/// (<see cref="GetSseAsync"/>). Every ViewModel and per-route service goes through this client —
/// nothing calls <see cref="HttpClient"/> directly (Implementation Rule #2, docs/THE_FORGE.md).
/// </summary>
public sealed class ArcanumApiClient
{

    public const string HttpClientName = "ArcanumApi";

    private const string ApiKeyHeaderName = "X-Arcanum-Key";

    /// <summary>Envelope code for a <c>the-forge.json</c> <c>BaseUrl</c> that is not an absolute http/https URL.</summary>
    internal const string InvalidBaseUrlCode = "Config.InvalidBaseUrl";

    /// <summary>Envelope code for a stored master API key that cannot be sent as an HTTP header value.</summary>
    internal const string InvalidApiKeyCode = "Config.InvalidApiKey";

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly IOptionsMonitor<TheForgeSettings> _settingsMonitor;

    private readonly ITheForgeApiKeyProvider _apiKeyProvider;

    private readonly ILogger<ArcanumApiClient> _logger;

    public ArcanumApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<TheForgeSettings> settingsMonitor,
        ITheForgeApiKeyProvider apiKeyProvider,
        ILogger<ArcanumApiClient> logger)
    {

        _httpClientFactory = httpClientFactory;

        _settingsMonitor = settingsMonitor;

        _apiKeyProvider = apiKeyProvider;

        _logger = logger;

    }

    public Task<ApiResponse<TResponse>?> GetAsync<TResponse>(
        string path,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, requestContent: null, responseTypeInfo, cancellationToken);

    public Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(
        string path,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, path, requestContent: null, responseTypeInfo, cancellationToken);

    /// <summary>
    /// For routes that respond <c>204 No Content</c> / a bare status on success (e.g. <c>DELETE /api/campaigns/{id}</c>).
    /// Returns <see langword="true"/> on any 2xx, <see langword="false"/> otherwise.
    /// </summary>
    public async Task<bool> DeleteNoContentAsync(string path, CancellationToken cancellationToken)
    {

        try
        {

            using HttpClient client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);

            using HttpRequestMessage request = new(HttpMethod.Delete, path);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;

        }
        catch (HttpRequestException ex)
        {

            _logger.LogWarning(ex, "DELETE {Path} failed.", path);

            return false;

        }
        catch (ArcanumClientConfigurationException ex)
        {

            _logger.LogError(ex, "DELETE {Path} aborted: {Code}.", path, ex.Code);

            return false;

        }
        catch (InvalidOperationException ex)
        {

            _logger.LogWarning(ex, "DELETE {Path} aborted: missing API key.", path);

            return false;

        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {

            // A TaskCanceledException that did NOT originate from the caller's token is HttpClient's own
            // request-timeout signal. Callers of this method have no failure channel other than the
            // returned bool, so report it as a failed delete rather than letting it escape as if the
            // caller had cancelled — see the matching clause in SendAsync.
            _logger.LogWarning(ex, "DELETE {Path} timed out.", path);

            return false;

        }

    }

    public Task<ApiResponse<TResponse>?> PostAsync<TResponse>(
        string path,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, requestContent: null, responseTypeInfo, cancellationToken);

    public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, SerializeBody(body, requestTypeInfo), responseTypeInfo, cancellationToken);

    public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, path, SerializeBody(body, requestTypeInfo), responseTypeInfo, cancellationToken);

    /// <summary>
    /// <c>PATCH</c> with a JSON body — used by <c>PATCH /api/workspaces/{id}/files/contents</c>
    /// (text-block replace). Mirrors <see cref="PutAsync{TRequest, TResponse}"/>; <see cref="SendAsync"/>
    /// already accepts any <see cref="HttpMethod"/>.
    /// </summary>
    public Task<ApiResponse<TResponse>?> PatchAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, path, SerializeBody(body, requestTypeInfo), responseTypeInfo, cancellationToken);

    /// <summary>
    /// NDJSON streaming (<c>POST /api/intelligence/ping-stream</c>, <c>POST /api/spells/{name}/execute-stream</c>):
    /// one JSON frame per <c>\n</c>-terminated line, no <c>[DONE]</c> terminator.
    /// </summary>
    public async IAsyncEnumerable<TFrame> PostNdjsonStreamAsync<TRequest, TFrame>(
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TFrame> frameTypeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        HttpClient client;

        try
        {

            client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (ArcanumClientConfigurationException ex)
        {

            _logger.LogError(ex, "NDJSON stream POST {Path} aborted: {Code}.", path, ex.Code);

            yield break;

        }
        catch (InvalidOperationException ex)
        {

            _logger.LogWarning(ex, "NDJSON stream POST {Path} aborted: missing API key.", path);

            yield break;

        }

        using (client)
        {

            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = SerializeBody(body, requestTypeInfo),
            };

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                _logger.LogWarning("NDJSON stream POST {Path} returned {StatusCode}.", path, (int)response.StatusCode);

                yield break;

            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using StreamReader reader = new(stream, Encoding.UTF8);

            BoundedTextLineReader lineReader = new(reader);

            while (true)
            {
                BoundedTextLineReadResult read = await lineReader
                    .ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!read.HasLine)
                {
                    break;
                }

                if (read.IsTooLong)
                {
                    _logger.LogWarning(
                        "NDJSON stream response from {Path} exceeded the maximum line size; frame discarded.",
                        path);

                    continue;
                }

                string line = read.Line;

                if (string.IsNullOrWhiteSpace(line))
                {

                    continue;

                }

                byte[]? utf8Line = null;
                if (typeof(TFrame) == typeof(IntelligenceEvent))
                {
                    utf8Line = Encoding.UTF8.GetBytes(line);
                    IntelligenceEventDiscriminatorResult discriminator =
                        IntelligenceEventDiscriminator.InspectAndNormalize(utf8Line);
                    if (discriminator == IntelligenceEventDiscriminatorResult.Unknown)
                    {
                        continue;
                    }

                    if (discriminator == IntelligenceEventDiscriminatorResult.Malformed)
                    {
                        _logger.LogWarning("Failed to deserialize response from {Path}.", path);
                        continue;
                    }
                }

                TFrame? frame = utf8Line is null
                    ? DeserializeOrLog(line, frameTypeInfo, path)
                    : DeserializeOrLog(utf8Line, frameTypeInfo, path);

                if (frame is not null)
                {

                    yield return frame;

                }

            }

        }

    }

    /// <summary>
    /// SSE streaming (session/chronicle/event routes): accumulates <c>data:</c> lines into one frame
    /// per blank-line boundary, ignores <c>:</c> keep-alive comments, and stops at <c>data: [DONE]</c>.
    /// Returns raw <see cref="SseEvent"/> frames — <c>ArcanumSseClient</c> deserializes the payload
    /// into concrete types per route.
    /// </summary>
    public async IAsyncEnumerable<SseEvent> GetSseAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        HttpClient client;

        try
        {

            client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (ArcanumClientConfigurationException ex)
        {

            _logger.LogError(ex, "SSE GET {Path} aborted: {Code}.", path, ex.Code);

            yield break;

        }
        catch (InvalidOperationException ex)
        {

            _logger.LogWarning(ex, "SSE GET {Path} aborted: missing API key.", path);

            yield break;

        }

        using (client)
        {

            using HttpRequestMessage request = new(HttpMethod.Get, path);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                _logger.LogWarning("SSE GET {Path} returned {StatusCode}.", path, (int)response.StatusCode);

                yield break;

            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using StreamReader reader = new(stream, Encoding.UTF8);

            await foreach (SseEvent sseEvent in SseFrameParser.ParseAsync(reader, cancellationToken).ConfigureAwait(false))
            {

                yield return sseEvent;

            }

        }

    }

    private async Task<HttpClient> CreateClientAsync(CancellationToken cancellationToken)
    {

        TheForgeSettings settings = _settingsMonitor.CurrentValue;

        string? apiKey = await _apiKeyProvider.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {

            throw new InvalidOperationException(
                "No master API key is available. Store one with `arcanum key set`, run `arcanum serve` once, "
                + "or paste a key when prompted. Shared OS identity: arcanum/master-api-key.");

        }

        // the-forge.json is reload-on-change and hand-editable, and only the Setup Wizard validates the
        // URL. Without these guards a malformed value escapes as UriFormatException/FormatException —
        // neither of which any caller catches — instead of the envelope this layer promises.
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out Uri? baseAddress)
            || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {

            throw new ArcanumClientConfigurationException(
                InvalidBaseUrlCode,
                $"The Forge setting 'BaseUrl' is not an absolute http/https URL: '{settings.BaseUrl}'. "
                + "Correct it in the-forge.json or re-run the setup wizard.");

        }

        if (apiKey.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {

            throw new ArcanumClientConfigurationException(
                InvalidApiKeyCode,
                "The master API key contains a line break or NUL character and cannot be sent as an HTTP "
                + "header. Re-enter the key on a single line.");

        }

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        client.BaseAddress = baseAddress;

        client.DefaultRequestHeaders.Remove(ApiKeyHeaderName);

        client.DefaultRequestHeaders.Add(ApiKeyHeaderName, apiKey);

        return client;

    }

    private static StringContent SerializeBody<TRequest>(TRequest body, JsonTypeInfo<TRequest> requestTypeInfo)
    {

        string json = JsonSerializer.Serialize(body, requestTypeInfo);

        StringContent content = new(json, Encoding.UTF8);

        content.Headers.ContentType = JsonMediaType;

        return content;

    }

    private async Task<ApiResponse<TResponse>?> SendAsync<TResponse>(
        HttpMethod method,
        string path,
        HttpContent? requestContent,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        CancellationToken cancellationToken)
    {

        try
        {

            using HttpClient client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);

            using HttpRequestMessage request = new(method, path) { Content = requestContent };

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[]? body = await BoundedHttpContentReader
                .TryReadAsync(response.Content, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (body is null)
            {

                return Failure(
                    responseTypeInfo,
                    "Api.ResponseTooLarge",
                    "The API response exceeded the maximum allowed size and was not read.");

            }

            if (body.Length == 0 || IsJsonWhitespace(body))
            {

                return response.IsSuccessStatusCode
                    ? null
                    : Failure(responseTypeInfo, $"Http.{(int)response.StatusCode}", response.ReasonPhrase ?? "Request failed.");

            }

            ApiResponse<TResponse>? parsed = DeserializeOrLog(body, responseTypeInfo, path);

            if (parsed is not null)
            {

                return parsed;

            }

            return response.IsSuccessStatusCode
                ? null
                : Failure(responseTypeInfo, $"Http.{(int)response.StatusCode}", response.ReasonPhrase ?? "Request failed.");

        }
        catch (ArcanumClientConfigurationException ex)
        {

            // Distinct from Security.MissingApiKey: the key is present, the-forge.json is malformed.
            _logger.LogError(ex, "{Method} {Path} aborted: {Code}.", method, path, ex.Code);

            return Failure(responseTypeInfo, ex.Code, ex.Message);

        }
        catch (InvalidOperationException ex)
        {

            _logger.LogWarning(ex, "{Method} {Path} aborted: missing API key.", method, path);

            return Failure(responseTypeInfo, "Security.MissingApiKey", ex.Message);

        }
        catch (HttpRequestException ex)
        {

            _logger.LogWarning(ex, "{Method} {Path} failed to reach Arcanum.", method, path);

            return Failure(responseTypeInfo, "Connection.Failed", ex.Message);

        }
        catch (IOException ex)
        {

            _logger.LogWarning(ex, "{Method} {Path} response could not be read.", method, path);

            return Failure(responseTypeInfo, "Connection.Failed", ex.Message);

        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {

            // A TaskCanceledException that did NOT originate from the caller's token is HttpClient's
            // own request-timeout signal, not a caller-requested cancellation — surface it as a
            // connection failure like any other transport fault, rather than letting it propagate as
            // if the caller cancelled.
            _logger.LogWarning(ex, "{Method} {Path} timed out.", method, path);

            return Failure(responseTypeInfo, "Connection.Timeout", "The request to Arcanum timed out.");

        }

    }

    private static bool IsJsonWhitespace(ReadOnlySpan<byte> body)
    {
        foreach (byte value in body)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    private static ApiResponse<TResponse> Failure<TResponse>(
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        string code,
        string message)
    {

        _ = responseTypeInfo; // Kept for call-site symmetry / future typed-error mapping.

        return new ApiResponse<TResponse>(default, false, new Error(code, message), null);

    }

    private T? DeserializeOrLog<T>(string json, JsonTypeInfo<T> typeInfo, string path)
    {

        try
        {

            return JsonSerializer.Deserialize(json, typeInfo);

        }
        catch (JsonException ex)
        {

            _logger.LogWarning(ex, "Failed to deserialize response from {Path}.", path);

            return default;

        }

    }

    private T? DeserializeOrLog<T>(ReadOnlySpan<byte> json, JsonTypeInfo<T> typeInfo, string path)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize response from {Path}.", path);
            return default;
        }
    }

}

/// <summary>
/// A <c>the-forge.json</c> value that cannot be turned into a request (malformed <c>BaseUrl</c>, API key
/// carrying a header-illegal character). Derives from <see cref="InvalidOperationException"/> so the
/// streaming call sites' existing guard still stops the stream rather than letting the fault escape,
/// while <see cref="Code"/> keeps the envelope distinct from <c>Security.MissingApiKey</c>.
/// </summary>
internal sealed class ArcanumClientConfigurationException : InvalidOperationException
{

    public ArcanumClientConfigurationException(string code, string message)
        : base(message)
    {

        Code = code;

    }

    public string Code { get; }

}
