using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

            using HttpResponseMessage response = await client
                .DeleteAsync(path, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;

        }
        catch (HttpRequestException ex)
        {

            _logger.LogWarning(ex, "DELETE {Path} failed.", path);

            return false;

        }
        catch (InvalidOperationException ex)
        {

            _logger.LogWarning(ex, "DELETE {Path} aborted: missing API key.", path);

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
    /// NDJSON streaming (<c>POST /api/intelligence/ping-stream</c>, <c>POST /api/spells/{name}/execute-stream</c>,
    /// <c>POST /api/llama/models/pull</c>): one JSON frame per <c>\n</c>-terminated line, no <c>[DONE]</c> terminator.
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

            string? line;

            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {

                if (string.IsNullOrWhiteSpace(line))
                {

                    continue;

                }

                TFrame? frame = DeserializeOrLog(line, frameTypeInfo, path);

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

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        client.BaseAddress = new Uri(settings.BaseUrl, UriKind.Absolute);

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
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
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

}
