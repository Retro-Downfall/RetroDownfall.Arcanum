using System.Buffers;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// MCP Streamable HTTP client (2026-07-28 spec). Implements <see cref="IMcpClient"/> over stateless
/// HTTP POSTs: each JSON-RPC request is POSTed (JSON or SSE response), multi-round tool responses
/// (MRTR) are resolved via an <see cref="IMcpInputElicitor"/>, and cancellation closes the response
/// stream (no <c>notifications/cancelled</c> on HTTP). Connectivity failures and HTTP 4xx/5xx surface
/// as <see cref="McpTransportUnavailableException"/> so <see cref="McpBridgeTool"/> can fall back;
/// JSON-RPC <c>error</c> results and payload-cap violations propagate unwrapped (no fallback).
/// </summary>
internal sealed class McpHttpClient : IMcpClient
{

    // Bounds the MRTR re-POST loop so a misbehaving server cannot drive an unbounded request storm.
    private const int MaxInputRounds = 8;

    private readonly Uri _endpoint;

    private readonly HttpClient _httpClient;

    private readonly TimeSpan _defaultRequestTimeout;

    private readonly int _maxToolsListPages;

    private readonly int _maxToolsPerServer;

    private readonly int _maxToolsPerListPage;

    private readonly int _maxToolsTotalBytes;

    private readonly long _toolOutputCapBytes;

    private readonly int _maxJsonRpcLineBytes;

    private readonly IMcpInputElicitor? _inputElicitor;

    private readonly ILogger? _logger;

    private readonly McpJsonSerializerContext _json;

    private readonly CancellationTokenSource _disposeCts = new();

    private readonly SemaphoreSlim _initLock = new(1, 1);

    private bool _initialized;

    private volatile bool _disposed;

    public McpHttpClient(
        Uri endpoint,
        HttpClient httpClient,
        TimeSpan defaultRequestTimeout,
        int maxToolsListPages,
        long toolOutputCapBytes,
        int maxToolsPerServer,
        int maxToolsPerListPage,
        int maxToolsTotalBytes,
        int maxJsonRpcLineBytes,
        IMcpInputElicitor? inputElicitor = null,
        ILogger? logger = null,
        McpJsonSerializerContext? jsonContext = null)
    {

        ArgumentNullException.ThrowIfNull(endpoint);

        ArgumentNullException.ThrowIfNull(httpClient);

        if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
        {

            throw new ArgumentException("MCP HTTP endpoint must use the http or https scheme.", nameof(endpoint));

        }

        if (maxToolsListPages < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxToolsListPages));

        }

        if (maxToolsPerServer < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxToolsPerServer));

        }

        if (maxToolsPerListPage < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxToolsPerListPage));

        }

        if (maxToolsTotalBytes < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxToolsTotalBytes));

        }

        if (maxJsonRpcLineBytes < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxJsonRpcLineBytes));

        }

        if (toolOutputCapBytes < 1L)
        {

            throw new ArgumentOutOfRangeException(nameof(toolOutputCapBytes));

        }

        _endpoint = endpoint;

        _httpClient = httpClient;

        _defaultRequestTimeout = defaultRequestTimeout;

        _maxToolsListPages = maxToolsListPages;

        _maxToolsPerServer = maxToolsPerServer;

        _maxToolsPerListPage = maxToolsPerListPage;

        _maxToolsTotalBytes = maxToolsTotalBytes;

        _toolOutputCapBytes = toolOutputCapBytes;

        _maxJsonRpcLineBytes = maxJsonRpcLineBytes;

        _inputElicitor = inputElicitor;

        _logger = logger;

        _json = jsonContext ?? McpJsonSerializerContext.Default;

    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            if (_initialized)
            {

                throw new InvalidOperationException("McpHttpClient is already initialized.");

            }

            await McpProtocol.InitializeAsync(
                    this,
                    McpProtocol.StreamableHttpProtocolVersion,
                    SendInitializedNotificationAsync,
                    _json,
                    _defaultRequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            _initialized = true;

        }
        finally
        {

            _initLock.Release();

        }

    }

    /// <inheritdoc />
    public async Task<JsonElement> SendRequestAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken = default,
        TimeSpan? requestTimeout = null)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        TimeSpan timeout = requestTimeout ?? _defaultRequestTimeout;

        string mcpName = ExtractToolName(method, parameters);

        JsonElement? currentParams = parameters;

        int round = 0;

        while (true)
        {

            JsonRpcResponse response = await ExecuteAsync(
                    timeout,
                    cancellationToken,
                    token => PostOnceAsync(method, currentParams, mcpName, token))
                .ConfigureAwait(false);

            JsonElement result = McpProtocol.ExtractResultOrThrow(response);

            if (!TryReadInputRequired(result, out McpInputRequiredResult? inputRequired))
            {

                return result;

            }

            if (_inputElicitor is null)
            {

                throw new InvalidOperationException(
                    "MCP server requested additional input (multi-round tool response) but no input elicitor is configured for the HTTP transport.");

            }

            if (round >= MaxInputRounds)
            {

                throw new InvalidOperationException(
                    $"MCP multi-round tool response exceeded the maximum of {MaxInputRounds} input rounds.");

            }

            IReadOnlyList<McpInputResponse> responses = await _inputElicitor
                .ElicitAsync(inputRequired!.InputRequests, cancellationToken)
                .ConfigureAwait(false);

            currentParams = BuildContinuationParams(currentParams, responses, inputRequired.RequestState);

            round++;

        }

    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized)
        {

            throw new InvalidOperationException("McpHttpClient must be initialized before calling GetToolsAsync.");

        }

        McpToolListCaps caps = new(
            _maxToolsListPages,
            _maxToolsPerServer,
            _maxToolsPerListPage,
            _maxToolsTotalBytes);

        return await McpProtocol.GetToolsAsync(this, caps, _toolOutputCapBytes, _json, cancellationToken)
            .ConfigureAwait(false);

    }

    public async ValueTask DisposeAsync()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        await _disposeCts.CancelAsync().ConfigureAwait(false);

        _disposeCts.Dispose();

        _initLock.Dispose();

        // _httpClient is owned by IHttpClientFactory; the client does not dispose it here.

    }

    private Task SendInitializedNotificationAsync(CancellationToken cancellationToken)
    {

        JsonRpcNotification initialized = new()
        {
            Method = "notifications/initialized",
            Params = null,
        };

        return ExecuteAsync(_defaultRequestTimeout, cancellationToken, async token =>
        {

            await McpHttpTransport
                .SendNotificationAsync(_httpClient, _endpoint, initialized, _json, _maxJsonRpcLineBytes, token)
                .ConfigureAwait(false);

            return true;

        });

    }

    private async Task<JsonRpcResponse> PostOnceAsync(
        string method,
        JsonElement? parameters,
        string mcpName,
        CancellationToken cancellationToken)
    {

        JsonElement idElement = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("N"), _json.String);

        JsonRpcRequest request = new()
        {
            Method = method,
            Params = parameters,
            Id = idElement,
        };

        HttpResponseMessage response = await McpHttpTransport
            .SendAsync(_httpClient, _endpoint, request, mcpName, _json, _maxJsonRpcLineBytes, cancellationToken)
            .ConfigureAwait(false);

        try
        {

            if (!response.IsSuccessStatusCode)
            {

                throw new McpTransportUnavailableException(
                    $"MCP HTTP server returned HTTP {(int)response.StatusCode}.");

            }

            return await McpHttpTransport
                .ParseResponseAsync(response, _json, _maxJsonRpcLineBytes, cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            // Disposing the response closes the (possibly still-open SSE) stream — this is also the
            // cancellation signal on Streamable HTTP per the 2026-07-28 spec.
            response.Dispose();

        }

    }

    // Threads a per-request timeout + dispose token onto the caller token and maps cancellation /
    // connectivity exceptions to the shared contract: caller-cancel propagates as
    // OperationCanceledException (no fallback), while timeout / disposal / connectivity become
    // McpTransportUnavailableException (fallback eligible). JSON-RPC errors and payload-cap
    // violations are thrown by inner layers and pass through unwrapped.
    private async Task<T> ExecuteAsync<T>(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> action)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        bool noTimeout = timeout == Timeout.InfiniteTimeSpan;

        using CancellationTokenSource? timeoutCts = noTimeout ? null : new CancellationTokenSource(timeout);

        using CancellationTokenSource linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _disposeCts.Token);

        try
        {

            return await action(linked.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (OperationCanceledException oce) when (timeoutCts is { IsCancellationRequested: true })
        {

            throw new McpTransportUnavailableException(
                "MCP HTTP request timed out before a response was received.", oce);

        }
        catch (OperationCanceledException oce) when (_disposeCts.IsCancellationRequested)
        {

            throw new McpTransportUnavailableException(
                "MCP HTTP client was disposed before a response was received.", oce);

        }
        catch (OperationCanceledException oce)
        {

            // HttpClient.Timeout (headers phase) surfaces a TaskCanceledException with no token set.
            throw new McpTransportUnavailableException(
                "MCP HTTP request was canceled or timed out before a response was received.", oce);

        }
        catch (Exception ex) when (IsConnectivityFailure(ex))
        {

            _logger?.LogDebug(ex, "MCP HTTP transport to {Endpoint} failed with a connectivity error.", _endpoint);

            throw new McpTransportUnavailableException(
                "MCP HTTP transport is unavailable: the server is unreachable.", ex);

        }

    }

    private bool TryReadInputRequired(JsonElement result, out McpInputRequiredResult? inputRequired)
    {

        inputRequired = null;

        if (result.ValueKind != JsonValueKind.Object)
        {

            return false;

        }

        if (!result.TryGetProperty("inputRequired", out JsonElement flag) || flag.ValueKind != JsonValueKind.True)
        {

            return false;

        }

        inputRequired = result.Deserialize(_json.McpInputRequiredResult);

        return inputRequired is not null;

    }

    private JsonElement BuildContinuationParams(
        JsonElement? originalParams,
        IReadOnlyList<McpInputResponse> responses,
        JsonElement requestState)
    {

        ArrayBufferWriter<byte> buffer = new(512);

        using (Utf8JsonWriter writer = new(buffer))
        {

            writer.WriteStartObject();

            if (originalParams is { ValueKind: JsonValueKind.Object } original)
            {

                foreach (JsonProperty property in original.EnumerateObject())
                {

                    if (property.NameEquals("inputResponses") || property.NameEquals("requestState"))
                    {

                        continue;

                    }

                    property.WriteTo(writer);

                }

            }

            writer.WritePropertyName("inputResponses");

            McpInputResponse[] array = responses as McpInputResponse[] ?? responses.ToArray();

            JsonSerializer.Serialize(writer, array, _json.McpInputResponseArray);

            writer.WritePropertyName("requestState");

            if (requestState.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {

                writer.WriteNullValue();

            }
            else
            {

                requestState.WriteTo(writer);

            }

            writer.WriteEndObject();

        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);

        return document.RootElement.Clone();

    }

    private static string ExtractToolName(string method, JsonElement? parameters)
    {

        if (string.Equals(method, "tools/call", StringComparison.Ordinal)
            && parameters is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("name", out JsonElement nameEl)
            && nameEl.ValueKind == JsonValueKind.String)
        {

            return nameEl.GetString() ?? method;

        }

        return method;

    }

    private static bool IsConnectivityFailure(Exception exception) =>
        exception is HttpRequestException or IOException;

}
