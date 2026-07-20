using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Models.OpenAi;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.ViewModels;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Dedicated OpenAI-shaped HTTP path for <c>/v1/files</c> and <c>/v1/batches</c>. Uses the same
/// named <c>"ArcanumApi"</c> <see cref="HttpClient"/> and <c>X-Arcanum-Key</c> header as
/// <see cref="ArcanumApiClient"/>, but deserializes bare OpenAI success DTOs and the OpenAI error
/// envelope — never <c>ApiResponse&lt;T&gt;</c>.
/// </summary>
public sealed class OpenAiCompatApiClient
{

    private const string ApiKeyHeaderName = "X-Arcanum-Key";

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly IOptionsMonitor<TheForgeSettings> _settingsMonitor;

    private readonly ITheForgeApiKeyProvider _apiKeyProvider;

    private readonly ILogger<OpenAiCompatApiClient> _logger;

    public OpenAiCompatApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<TheForgeSettings> settingsMonitor,
        ITheForgeApiKeyProvider apiKeyProvider,
        ILogger<OpenAiCompatApiClient> logger)
    {

        _httpClientFactory = httpClientFactory;

        _settingsMonitor = settingsMonitor;

        _apiKeyProvider = apiKeyProvider;

        _logger = logger;

    }

    public Task<OpenAiResult<TResponse>> GetAsync<TResponse>(
        string path,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Get, path, requestContent: null, responseTypeInfo, cancellationToken);

    public Task<OpenAiResult<TResponse>> DeleteAsync<TResponse>(
        string path,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Delete, path, requestContent: null, responseTypeInfo, cancellationToken);

    public Task<OpenAiResult<TResponse>> PostAsync<TResponse>(
        string path,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, path, requestContent: null, responseTypeInfo, cancellationToken);

    public Task<OpenAiResult<TResponse>> PostAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, path, SerializeBody(body, requestTypeInfo), responseTypeInfo, cancellationToken);

    /// <summary>
    /// Multipart upload for <c>POST /v1/files</c> (<c>file</c> + <c>purpose</c> form fields).
    /// Streams the file from disk — does not load the whole upload into memory.
    /// </summary>
    public async Task<OpenAiResult<TResponse>> PostMultipartFileAsync<TResponse>(
        string path,
        string filePath,
        string purpose,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {

        try
        {

            using HttpClient client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);

            await using FileStream fileStream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            using MultipartFormDataContent form = new();

            StreamContent fileContent = new(fileStream);

            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            string fileName = Path.GetFileName(filePath);

            form.Add(fileContent, "file", fileName);

            form.Add(new StringContent(purpose), "purpose");

            using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = form };

            using HttpResponseMessage response = await client
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            return await ParseJsonResponseAsync(response, responseTypeInfo, path, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (InvalidOperationException ex)
        {

            _logger.LogWarning(ex, "OpenAI multipart POST {Path} aborted: missing API key.", path);

            return OpenAiResult<TResponse>.Fail("Security.MissingApiKey", ex.Message);

        }
        catch (HttpRequestException ex)
        {

            _logger.LogWarning(ex, "OpenAI multipart POST {Path} failed to reach Arcanum.", path);

            return OpenAiResult<TResponse>.Fail("Connection.Failed", ex.Message);

        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {

            _logger.LogWarning(ex, "OpenAI multipart POST {Path} timed out.", path);

            return OpenAiResult<TResponse>.Fail("Connection.Timeout", "The request to Arcanum timed out.");

        }
        catch (IOException ex)
        {

            _logger.LogWarning(ex, "OpenAI multipart POST {Path} could not read {FilePath}.", path, filePath);

            return OpenAiResult<TResponse>.Fail("Files.ReadFailed", ex.Message);

        }

    }

    /// <summary>
    /// Downloads raw bytes from a content route (e.g. <c>GET /v1/files/{id}/content</c>) and
    /// streams them to <paramref name="destinationPath"/> without buffering the whole payload.
    /// </summary>
    public async Task<OpenAiResult<bool>> DownloadToFileAsync(
        string path,
        string destinationPath,
        CancellationToken cancellationToken)
    {

        try
        {

            using HttpClient client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);

            using HttpRequestMessage request = new(HttpMethod.Get, path);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                return FailFromBody<bool>(body, path, (int)response.StatusCode, response.ReasonPhrase);

            }

            await using Stream network = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            string? directory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(directory))
            {

                Directory.CreateDirectory(directory);

            }

            await using FileStream file = new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            await network.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

            return OpenAiResult<bool>.Ok(true);

        }
        catch (InvalidOperationException ex)
        {

            _logger.LogWarning(ex, "OpenAI download GET {Path} aborted: missing API key.", path);

            return OpenAiResult<bool>.Fail("Security.MissingApiKey", ex.Message);

        }
        catch (HttpRequestException ex)
        {

            _logger.LogWarning(ex, "OpenAI download GET {Path} failed to reach Arcanum.", path);

            return OpenAiResult<bool>.Fail("Connection.Failed", ex.Message);

        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {

            _logger.LogWarning(ex, "OpenAI download GET {Path} timed out.", path);

            return OpenAiResult<bool>.Fail("Connection.Timeout", "The request to Arcanum timed out.");

        }
        catch (IOException ex)
        {

            _logger.LogWarning(ex, "OpenAI download GET {Path} could not write {Destination}.", path, destinationPath);

            return OpenAiResult<bool>.Fail("Files.WriteFailed", ex.Message);

        }

    }

    /// <summary>
    /// Opens a raw content stream for bounded preview. Caller must dispose the returned stream.
    /// On failure, <see cref="OpenAiResult{T}.Data"/> is null.
    /// </summary>
    public async Task<OpenAiResult<Stream>> OpenContentStreamAsync(string path, CancellationToken cancellationToken)
    {

        try
        {

            HttpClient client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);

            HttpRequestMessage request = new(HttpMethod.Get, path);

            HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                int statusCode = (int)response.StatusCode;

                string? reasonPhrase = response.ReasonPhrase;

                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                response.Dispose();

                client.Dispose();

                return FailFromBody<Stream>(body, path, statusCode, reasonPhrase);

            }

            Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Own the response + client for the lifetime of the content stream.
            return OpenAiResult<Stream>.Ok(new ContentStreamOwner(network, response, client));

        }
        catch (InvalidOperationException ex)
        {

            _logger.LogWarning(ex, "OpenAI content GET {Path} aborted: missing API key.", path);

            return OpenAiResult<Stream>.Fail("Security.MissingApiKey", ex.Message);

        }
        catch (HttpRequestException ex)
        {

            _logger.LogWarning(ex, "OpenAI content GET {Path} failed to reach Arcanum.", path);

            return OpenAiResult<Stream>.Fail("Connection.Failed", ex.Message);

        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {

            _logger.LogWarning(ex, "OpenAI content GET {Path} timed out.", path);

            return OpenAiResult<Stream>.Fail("Connection.Timeout", "The request to Arcanum timed out.");

        }

    }

    private async Task<OpenAiResult<TResponse>> SendJsonAsync<TResponse>(
        HttpMethod method,
        string path,
        HttpContent? requestContent,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {

        try
        {

            using HttpClient client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);

            using HttpRequestMessage request = new(method, path) { Content = requestContent };

            using HttpResponseMessage response = await client
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            return await ParseJsonResponseAsync(response, responseTypeInfo, path, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (InvalidOperationException ex)
        {

            _logger.LogWarning(ex, "OpenAI {Method} {Path} aborted: missing API key.", method, path);

            return OpenAiResult<TResponse>.Fail("Security.MissingApiKey", ex.Message);

        }
        catch (HttpRequestException ex)
        {

            _logger.LogWarning(ex, "OpenAI {Method} {Path} failed to reach Arcanum.", method, path);

            return OpenAiResult<TResponse>.Fail("Connection.Failed", ex.Message);

        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {

            _logger.LogWarning(ex, "OpenAI {Method} {Path} timed out.", method, path);

            return OpenAiResult<TResponse>.Fail("Connection.Timeout", "The request to Arcanum timed out.");

        }

    }

    private async Task<OpenAiResult<TResponse>> ParseJsonResponseAsync<TResponse>(
        HttpResponseMessage response,
        JsonTypeInfo<TResponse> responseTypeInfo,
        string path,
        CancellationToken cancellationToken)
    {

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {

            if (string.IsNullOrWhiteSpace(body))
            {

                return OpenAiResult<TResponse>.Fail(
                    $"Http.{(int)response.StatusCode}",
                    "Empty success body from Arcanum.");

            }

            try
            {

                TResponse? parsed = JsonSerializer.Deserialize(body, responseTypeInfo);

                if (parsed is null)
                {

                    return OpenAiResult<TResponse>.Fail("Parse.Null", "Response deserialized to null.");

                }

                return OpenAiResult<TResponse>.Ok(parsed);

            }
            catch (JsonException ex)
            {

                _logger.LogWarning(ex, "Failed to deserialize OpenAI success body from {Path}.", path);

                return OpenAiResult<TResponse>.Fail("Parse.Failed", ex.Message);

            }

        }

        return FailFromBody<TResponse>(body, path, (int)response.StatusCode, response.ReasonPhrase);

    }

    private OpenAiResult<T> FailFromBody<T>(string body, string path, int statusCode, string? reasonPhrase)
    {

        if (!string.IsNullOrWhiteSpace(body))
        {

            try
            {

                OpenAiErrorResponse? error = JsonSerializer.Deserialize(
                    body,
                    TheForgeJsonContext.Default.OpenAiErrorResponse);

                if (error?.Error is { } detail)
                {

                    string message = string.IsNullOrWhiteSpace(detail.Message)
                        ? reasonPhrase ?? "Request failed."
                        : detail.Message;

                    return OpenAiResult<T>.Fail(detail.Code ?? detail.Type, message);

                }

            }
            catch (JsonException ex)
            {

                _logger.LogWarning(ex, "Failed to deserialize OpenAI error body from {Path}.", path);

            }

        }

        return OpenAiResult<T>.Fail($"Http.{statusCode}", reasonPhrase ?? "Request failed.");

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

        HttpClient client = _httpClientFactory.CreateClient(ArcanumApiClient.HttpClientName);

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

    /// <summary>Disposes the HTTP response and client when the content stream is disposed.</summary>
    private sealed class ContentStreamOwner : Stream
    {

        private readonly Stream _inner;

        private readonly HttpResponseMessage _response;

        private readonly HttpClient _client;

        private bool _disposed;

        public ContentStreamOwner(Stream inner, HttpResponseMessage response, HttpClient client)
        {

            _inner = inner;

            _response = response;

            _client = client;

        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            if (disposing)
            {

                _inner.Dispose();

                _response.Dispose();

                _client.Dispose();

            }

            base.Dispose(disposing);

        }

    }

}
