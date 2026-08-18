using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using SdkMcpClient = ModelContextProtocol.Client.McpClient;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Adapts the official ModelContextProtocol SDK's <see cref="SdkMcpClient"/> to <see cref="IMcpClient"/>.
/// Owns one SDK client session over a caller-supplied <see cref="IClientTransport"/> (stdio, Streamable
/// HTTP, or the in-process <see cref="ChannelClientTransport"/>), follows <c>tools/list</c> pagination
/// until completion with cursor-cycle detection, and lets <c>tools/call</c> run until completion or
/// caller cancellation unless a caller explicitly supplies a request policy.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: thin adapter over the ModelContextProtocol SDK client; covered via SdkMcpClientWrapperTests using an in-memory transport.
internal sealed class SdkMcpClientWrapper : IMcpClient
{
    private readonly IClientTransport _clientTransport;

    private readonly McpClientOptions _clientOptions;

    private readonly ILoggerFactory? _loggerFactory;

    private readonly TimeSpan _initializationTimeout;

    private readonly int _maxToolsTotalBytes;

    private readonly long _toolOutputCapBytes;

    /// <summary>
    /// How many consecutive <c>tools/list</c> pages may contribute no accounted tool metadata before
    /// the walk is abandoned. The cumulative <c>maxToolsTotalBytes</c> budget is the physical bound
    /// on the catalog, but it only advances for tools that are actually accepted, so a server that
    /// answers every page with an empty <c>tools</c> array (or with tools whose names are blank, which
    /// are skipped before any byte is charged) plus a brand new cursor satisfies neither that budget
    /// nor the cursor-cycle guard. This bounds no-progress paging without capping the page count of a
    /// server that is genuinely handing back a catalog.
    /// </summary>
    private const int MaxConsecutiveNoProgressToolPages = 64;

    private readonly SemaphoreSlim _initLock = new(1, 1);

    private readonly CancellationTokenSource _completionCts =
        new();

    private SdkMcpClient? _sdkClient;

    private bool _initialized;

    private volatile bool _disposed;

    /// <summary>
    /// Invoked when the SDK client's session ends on its own (subprocess crash, dropped HTTP
    /// session, closed channel) rather than via an intentional <see cref="DisposeAsync"/>. Mirrors
    /// the pre-SDK-migration <c>McpProcessTransport.OnTransportEnded</c> callback so
    /// <see cref="McpConnectionManager"/> can reactively transition the entry to
    /// <see cref="RetroDownfall.Arcanum.Core.Mcp.McpServerState.Error"/> and publish an
    /// <see cref="RetroDownfall.Arcanum.Core.Mcp.McpServerEvent"/> instead of only discovering the
    /// failure lazily on the next tool call.
    /// </summary>
    public Action? OnTransportEnded { get; set; }

    public SdkMcpClientWrapper(
        IClientTransport clientTransport,
        McpClientOptions clientOptions,
        TimeSpan initializationTimeout,
        long toolOutputCapBytes,
        int maxToolsTotalBytes,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(clientTransport);

        ArgumentNullException.ThrowIfNull(clientOptions);

        if (initializationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initializationTimeout));
        }

        if (maxToolsTotalBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolsTotalBytes));
        }

        if (toolOutputCapBytes < 1L)
        {
            throw new ArgumentOutOfRangeException(nameof(toolOutputCapBytes));
        }

        _clientTransport = clientTransport;

        _clientOptions = clientOptions;

        _initializationTimeout = initializationTimeout;

        _maxToolsTotalBytes = maxToolsTotalBytes;

        _toolOutputCapBytes = toolOutputCapBytes;

        _loggerFactory = loggerFactory;
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
                throw new InvalidOperationException("SdkMcpClientWrapper is already initialized.");
            }

            using CancellationTokenSource initialization =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            initialization.CancelAfter(_initializationTimeout);

            _sdkClient = await SdkMcpClient
                .CreateAsync(
                    _clientTransport,
                    _clientOptions,
                    _loggerFactory,
                    initialization.Token)
                .ConfigureAwait(false);

            _initialized = true;

            _ = ObserveCompletionAsync(_sdkClient);
        }
        finally
        {
            _initLock.Release();
        }
    }

    // McpClient.Completion completes both on graceful disposal and on unexpected closure (process
    // crash, dropped session, closed channel). _disposed is only set at the start of our own
    // DisposeAsync, before it awaits the SDK client's disposal, so by the time this continuation
    // observes Completion, _disposed reliably distinguishes "we tore this down on purpose" from
    // "the session ended on its own" — only the latter should fire OnTransportEnded.
    private async Task ObserveCompletionAsync(SdkMcpClient sdkClient)
    {
        try
        {
            await sdkClient.Completion.ConfigureAwait(false);
        }
        catch
        {
            // Completion is documented to always complete successfully; defensive only.
        }

        try
        {
            await _completionCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Intentional disposal already completed the same lifetime.
        }

        if (!_disposed)
        {
            OnTransportEnded?.Invoke();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _sdkClient is null)
        {
            throw new InvalidOperationException("SdkMcpClientWrapper must be initialized before calling GetToolsAsync.");
        }

        List<McpBridgeTool> bridgeTools = [];

        string? cursor = null;

        HashSet<string> seenCursors = new(StringComparer.Ordinal);

        long totalBytes = 0L;

        int consecutiveNoProgressPages = 0;

        while (true)
        {
            if (cursor is not null && !seenCursors.Add(cursor))
            {
                throw new InvalidDataException(
                    "The MCP server repeated a tools/list cursor; pagination cannot continue safely.");
            }

            ListToolsResult pageResult = await _sdkClient
                .ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, cancellationToken)
                .ConfigureAwait(false);

            // Per-tool and cumulative byte accounting runs as each page lands, not after the walk
            // finishes: deferring it to the end would let a server that keeps handing back fresh
            // cursors accumulate an unbounded catalog before the cap that is supposed to bound the
            // allocation is ever consulted.
            long pageBytes = 0L;

            foreach (Tool tool in pageResult.Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Name))
                {
                    continue;
                }

                string description = McpSecurityLimits.BoundToolDescription(tool.Description ?? string.Empty);

                System.Text.Json.JsonElement inputSchema = McpSecurityLimits.BoundToolInputSchema(
                    tool.InputSchema,
                    McpJsonSerializerContext.Default);

                long toolBytes = Encoding.UTF8.GetByteCount(description) + Encoding.UTF8.GetByteCount(inputSchema.GetRawText());

                if (totalBytes + toolBytes > _maxToolsTotalBytes)
                {
                    throw new InvalidDataException(
                        $"The MCP tool catalog exceeded the physical metadata allocation boundary of {_maxToolsTotalBytes} UTF-8 bytes; the server must expose a smaller or paged schema catalog.");
                }

                totalBytes += toolBytes;

                pageBytes += toolBytes;

                bridgeTools.Add(new McpBridgeTool(tool.Name, description, inputSchema, this, _toolOutputCapBytes));
            }

            consecutiveNoProgressPages = pageBytes > 0L ? 0 : consecutiveNoProgressPages + 1;

            if (consecutiveNoProgressPages > MaxConsecutiveNoProgressToolPages)
            {
                throw new InvalidDataException(
                    $"The MCP server returned {MaxConsecutiveNoProgressToolPages} consecutive tools/list pages carrying no tool metadata while still advancing the cursor; pagination cannot make progress.");
            }

            cursor = pageResult.NextCursor;

            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }

        return bridgeTools;
    }

    /// <inheritdoc />
    public async Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _sdkClient is null)
        {
            throw new InvalidOperationException("SdkMcpClientWrapper must be initialized before calling CallToolAsync.");
        }

        TimeSpan timeout = requestTimeout ?? Timeout.InfiniteTimeSpan;

        bool noPerRequestTimeout = timeout == Timeout.InfiniteTimeSpan;

        using CancellationTokenSource? timeoutCts = noPerRequestTimeout ? null : new CancellationTokenSource(timeout);

        using CancellationTokenSource linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _completionCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token,
                _completionCts.Token);

        try
        {
            return await _sdkClient
                .CallToolAsync(toolName, arguments, progress: null, options: null, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation is not a transport failure — propagate without wrapping. The SDK
            // dispatches the wire notifications/cancelled frame itself when linked.Token fires.
            throw;
        }
        catch (OperationCanceledException oce)
        {
            // Timeout or client/session completion after CallToolAsync began is conservatively
            // classified as dispatched-or-unknown. A fallback must not re-run the operation.
            throw new McpTransportUnavailableException(
                timeoutCts?.IsCancellationRequested == true
                    ? "MCP tool call timed out before a response was received."
                    : "MCP client session completed before a tool response was received.",
                McpRequestDispatchState.DispatchedOrUnknown,
                oce);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _completionCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        if (_sdkClient is not null)
        {
            await _sdkClient.DisposeAsync().ConfigureAwait(false);
        }

        _completionCts.Dispose();
        _initLock.Dispose();
    }
}
