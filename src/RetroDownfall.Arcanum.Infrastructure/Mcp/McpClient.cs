using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// MCP session client: correlates JSON-RPC responses by <c>id</c>, performs <c>initialize</c> / <c>tools/list</c>, and exposes <see cref="McpBridgeTool"/> instances.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: MCP JSON-RPC session client over transports; covered via McpClientTests and InProcessMcpTransport integration tests.
internal sealed class McpClient : IMcpClient
{
    private readonly TimeSpan _defaultRequestTimeout;

    private readonly int _maxToolsListPages;

    private readonly int _maxToolsPerServer;

    private readonly int _maxToolsPerListPage;

    private readonly int _maxToolsTotalBytes;

    private readonly long _toolOutputCapBytes;

    private readonly IMcpTransport _transport;

    private readonly McpJsonSerializerContext _json;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private int _correlationStarted;

    private Task? _correlationTask;

    private bool _initialized;

    private volatile bool _disposed;

    private readonly McpRequestCancellationBroker? _requestCancellationBroker;

    public McpClient(
        IMcpTransport transport,
        TimeSpan defaultRequestTimeout,
        int maxToolsListPages,
        long toolOutputCapBytes,
        int maxToolsPerServer,
        int maxToolsPerListPage,
        int maxToolsTotalBytes,
        McpRequestCancellationBroker? requestCancellationBroker = null,
        McpJsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

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

        if (toolOutputCapBytes < 1L)
        {
            throw new ArgumentOutOfRangeException(nameof(toolOutputCapBytes));
        }

        _transport = transport;

        _defaultRequestTimeout = defaultRequestTimeout;

        _maxToolsListPages = maxToolsListPages;

        _maxToolsPerServer = maxToolsPerServer;

        _maxToolsPerListPage = maxToolsPerListPage;

        _maxToolsTotalBytes = maxToolsTotalBytes;

        _toolOutputCapBytes = toolOutputCapBytes;

        _requestCancellationBroker = requestCancellationBroker;

        _json = jsonContext ?? McpJsonSerializerContext.Default;
    }

    /// <summary>
    /// Starts the transport, inbound correlation loop, MCP <c>initialize</c>, and <c>notifications/initialized</c>.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                throw new InvalidOperationException("McpClient is already initialized.");
            }

            await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsureCorrelationLoopStarted();
            await McpProtocol.InitializeAsync(
                    this,
                    McpProtocol.StdioProtocolVersion,
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

    private Task SendInitializedNotificationAsync(CancellationToken cancellationToken)
    {
        JsonRpcNotification initialized = new()
        {
            Method = "notifications/initialized",
            Params = null,
        };

        return _transport.WriteNotificationAsync(initialized, cancellationToken);
    }

    /// <summary>
    /// Sends a JSON-RPC request and awaits the <c>result</c> object. JSON-RPC <c>error</c> responses throw <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<JsonElement> SendRequestAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken = default,
        TimeSpan? requestTimeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        TimeSpan timeout = requestTimeout ?? _defaultRequestTimeout;
        string id = Guid.NewGuid().ToString("N");
        TaskCompletionSource<JsonElement> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs))
        {
            throw new InvalidOperationException("Failed to register pending JSON-RPC request (id collision).");
        }

        JsonElement idElement = JsonSerializer.SerializeToElement(id, _json.String);
        JsonRpcRequest request = new()
        {
            Method = method,
            Params = parameters,
            Id = idElement,
        };

        bool noPerRequestTimeout = timeout == Timeout.InfiniteTimeSpan;

        using CancellationTokenSource? timeoutCts = noPerRequestTimeout ? null : new CancellationTokenSource(timeout);

        using CancellationTokenSource linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _disposeCts.Token);

        CancellationToken waitToken = linked.Token;

        _requestCancellationBroker?.Register(id, waitToken);

        // W3.4 Group C #5: register the wire-cancel notification on the linked wait token
        // (caller + timeout + dispose), NOT the caller's token alone. Otherwise a per-request
        // TIMEOUT cancels the local wait but never tells the external server to stop, leaving
        // it processing an orphaned request. Both caller-cancel and timeout now dispatch
        // notifications/cancelled. The registration handle is intentionally not held for
        // finally-disposal: disposing it in the finally races with the callback (the finally
        // can unregister the callback before the cancelling thread invokes it, dropping the
        // notification). The registration is cleaned up when the linked CTS is disposed at
        // method exit instead.
        _ = waitToken.Register(() => DispatchWireCancelNotification(id));

        try
        {
            try
            {

                await _transport.WriteRequestAsync(request, cancellationToken).ConfigureAwait(false);

            }

            catch (OperationCanceledException)
            {

                // Caller cancellation is not a transport failure — propagate without wrapping.
                throw;

            }

            catch (Exception ex) when (IsTransportWriteFailure(ex))
            {

                // W3.4 Group C #6: classify transport/connectivity failures (channel closed,
                // broken stdin, transport disposed) as McpTransportUnavailableException so
                // McpBridgeTool can safely fall back to the global server. Tool-execution
                // failures (InvalidOperationException from RPC errors / isError) and payload
                // limits (McpLineSizeExceededException) are NOT transport failures and must
                // not trigger a fallback, so they propagate unwrapped.

                throw new McpTransportUnavailableException(
                    "MCP transport is unavailable: the local server is down or unreachable.",
                    ex);

            }

            return await tcs.Task.WaitAsync(waitToken).ConfigureAwait(false);
        }
        finally
        {
            _requestCancellationBroker?.Unregister(id);

            if (_pending.TryRemove(id, out TaskCompletionSource<JsonElement>? leftover))
            {
                leftover.TrySetCanceled(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Calls <c>tools/list</c> (with pagination) and maps each tool to <see cref="McpBridgeTool"/>.
    /// </summary>
    public async Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("McpClient must be initialized before calling GetToolsAsync.");
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
        FailAllPending(new ObjectDisposedException(nameof(McpClient)));
        await _transport.DisposeAsync().ConfigureAwait(false);
        Task loop = _correlationTask ?? Task.CompletedTask;
        await AwaitTaskGracefullyAsync(loop).ConfigureAwait(false);
        _disposeCts.Dispose();
        _initLock.Dispose();
    }

    private void EnsureCorrelationLoopStarted()
    {
        if (Interlocked.CompareExchange(ref _correlationStarted, 1, 0) != 0)
        {
            return;
        }

        CancellationToken token = _disposeCts.Token;
        _correlationTask = Task.Run(() => ProcessInboundLoopAsync(token), CancellationToken.None);
    }

    private async Task ProcessInboundLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (McpInboundEnvelope env in _transport.InboundReader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (env.Kind != McpInboundKind.Response || env.Response is not { } response)
                {
                    continue;
                }

                try
                {
                    HandleInboundResponse(response);
                }
                catch (Exception inner)
                {
                    // A bad handler must not fault the correlation loop and starve every pending request.
                    System.Diagnostics.Debug.WriteLine($"MCP correlation loop handler error: {inner.GetType().Name}: {inner.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception outer)
        {
            System.Diagnostics.Debug.WriteLine($"MCP correlation loop exited unexpectedly: {outer.GetType().Name}: {outer.Message}");
        }
        finally
        {
            // W3.4 Group C #6: the transport closed before a response arrived — this is a
            // transport/connectivity failure (the local server died), so fail pending
            // requests with McpTransportUnavailableException to allow McpBridgeTool to fall
            // back to the global server.
            FailAllPending(new McpTransportUnavailableException("MCP transport closed before a response was received."));
        }
    }

    private void HandleInboundResponse(JsonRpcResponse response)
    {
        string idKey = NormalizeRpcId(response.Id);
        if (!_pending.TryRemove(idKey, out TaskCompletionSource<JsonElement>? tcs))
        {
            return;
        }

        if (response.Error is { } rpcError)
        {
            tcs.TrySetException(new InvalidOperationException(McpProtocol.FormulateRpcErrorMessage(rpcError)));
            return;
        }

        if (response.Result is not { } result)
        {
            tcs.TrySetException(new InvalidOperationException("JSON-RPC response missing result."));
            return;
        }

        tcs.TrySetResult(result);
    }

    private void FailAllPending(Exception exception)
    {
        foreach (KeyValuePair<string, TaskCompletionSource<JsonElement>> kv in _pending.ToArray())
        {
            if (_pending.TryRemove(kv.Key, out TaskCompletionSource<JsonElement>? tcs))
            {
                tcs.TrySetException(exception);
            }
        }
    }

    private void DispatchWireCancelNotification(string requestId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                McpCancelledParams cancelParams = new()
                {
                    RequestId = requestId,
                    Reason = "Client cancelled",
                };

                JsonElement paramsElement = JsonSerializer.SerializeToElement(
                    cancelParams, _json.McpCancelledParams);

                JsonRpcNotification notification = new()
                {
                    Method = "notifications/cancelled",
                    Params = paramsElement,
                };

                await _transport.WriteNotificationAsync(notification, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort wire cancel; failures are expected during shutdown and must not crash the process.
            }
        });
    }

    internal static string NormalizeRpcId(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? id.GetRawText(),
            _ => id.GetRawText(),
        };
    }

    // W3.4 Group C #6: classifies exceptions raised by IMcpTransport.WriteRequestAsync as a
    // transport/connectivity failure (the local server is down or unreachable). Tool-execution
    // failures (InvalidOperationException from RPC errors / isError) and payload limits
    // (McpLineSizeExceededException) are intentionally excluded so McpBridgeTool does not
    // re-run a possibly-mutating tool on the fallback server.
    private static bool IsTransportWriteFailure(Exception exception) =>
        exception is ChannelClosedException
            or IOException
            or ObjectDisposedException;

    private static async Task AwaitTaskGracefullyAsync(Task task)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try
        {
            await task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort shutdown.
        }
    }
}
