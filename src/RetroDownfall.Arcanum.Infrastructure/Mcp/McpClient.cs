using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// MCP session client: correlates JSON-RPC responses by <c>id</c>, performs <c>initialize</c> / <c>tools/list</c>, and exposes <see cref="McpBridgeTool"/> instances.
/// </summary>
internal sealed class McpClient : IAsyncDisposable
{
    private const int MaxToolsListPages = 32;

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    private readonly McpProcessTransport _transport;

    private readonly McpJsonSerializerContext _json;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _disposeCts = new();

    private readonly SemaphoreSlim _initLock = new(1, 1);

    private int _correlationStarted;

    private Task? _correlationTask;

    private bool _initialized;

    private bool _disposed;

    public McpClient(McpProcessTransport transport, McpJsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;

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

            McpInitializeParams initParams = new()
            {
                ProtocolVersion = "2024-11-05",
                Capabilities = new McpClientCapabilities(),
                ClientInfo = new McpClientInfo
                {
                    Name = typeof(McpClient).Assembly.GetName().Name ?? "RetroDownfall.Arcanum.Infrastructure",
                    Version = typeof(McpClient).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                },
            };

            JsonElement initElement = JsonSerializer.SerializeToElement(initParams, _json.McpInitializeParams);

            _ = await SendRequestAsync("initialize", initElement, cancellationToken, DefaultRequestTimeout)
                .ConfigureAwait(false);

            JsonRpcNotification initialized = new()
            {
                Method = "notifications/initialized",
                Params = null,
            };

            await _transport.WriteNotificationAsync(initialized, cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
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

        TimeSpan timeout = requestTimeout ?? DefaultRequestTimeout;

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

        using CancellationTokenSource timeoutCts = new(timeout);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token,
            _disposeCts.Token);

        CancellationToken waitToken = linked.Token;

        try
        {
            await _transport.WriteRequestAsync(request, cancellationToken).ConfigureAwait(false);

            return await tcs.Task.WaitAsync(waitToken).ConfigureAwait(false);
        }
        finally
        {
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

        List<McpBridgeTool> tools = [];

        string? cursor = null;

        for (int page = 0; page < MaxToolsListPages; page++)
        {
            JsonElement? listParams = cursor is null
                ? null
                : JsonSerializer.SerializeToElement(new McpToolsListParams { Cursor = cursor }, _json.McpToolsListParams);

            JsonElement pageResult = await SendRequestAsync("tools/list", listParams, cancellationToken, DefaultRequestTimeout)
                .ConfigureAwait(false);

            if (!pageResult.TryGetProperty("tools", out JsonElement toolsArray) || toolsArray.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (JsonElement tool in toolsArray.EnumerateArray())
            {
                if (!tool.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? name = nameEl.GetString();

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string description = string.Empty;

                if (tool.TryGetProperty("description", out JsonElement descEl) && descEl.ValueKind == JsonValueKind.String)
                {
                    description = descEl.GetString() ?? string.Empty;
                }

                JsonElement inputSchema;

                if (tool.TryGetProperty("inputSchema", out JsonElement schemaEl) && schemaEl.ValueKind == JsonValueKind.Object)
                {
                    inputSchema = schemaEl.Clone();
                }
                else
                {
                    inputSchema = JsonSerializer.SerializeToElement(new McpEmptyJsonObject(), _json.McpEmptyJsonObject);
                }

                tools.Add(new McpBridgeTool(name, description, inputSchema, this));
            }

            if (!pageResult.TryGetProperty("nextCursor", out JsonElement next) || next.ValueKind != JsonValueKind.String)
            {
                break;
            }

            cursor = next.GetString();

            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }

        return tools;
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
            await foreach (McpInboundEnvelope env in _transport.InboundReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (env.Kind != McpInboundKind.Response || env.Response is not { } response)
                {
                    continue;
                }

                HandleInboundResponse(response);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            FailAllPending(new InvalidOperationException("MCP transport closed before a response was received."));
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
            tcs.TrySetException(new InvalidOperationException(FormulateRpcErrorMessage(rpcError)));

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

    private static string NormalizeRpcId(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? id.GetRawText(),
            _ => id.GetRawText(),
        };
    }

    private static string FormulateRpcErrorMessage(JsonRpcError error)
    {
        string message = $"{error.Code}: {error.Message}";

        if (error.Data is { } data)
        {
            message += " " + data.GetRawText();
        }

        return message.Trim();
    }

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
