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
/// HTTP, or the in-process <see cref="ChannelClientTransport"/>), manually paginates <c>tools/list</c> so
/// Arcanum's <c>MaxPaginationPages</c>/<c>MaxToolsPerServer</c>/<c>MaxToolsPerListPage</c>/<c>MaxToolsTotalBytes</c>
/// caps still apply exactly as before, and wraps <c>tools/call</c> with Arcanum's own per-request timeout
/// (the SDK imposes none of its own beyond the caller's <see cref="CancellationToken"/>).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: thin adapter over the ModelContextProtocol SDK client; covered via SdkMcpClientWrapperTests using an in-memory transport.
internal sealed class SdkMcpClientWrapper : IMcpClient
{
    private readonly IClientTransport _clientTransport;

    private readonly McpClientOptions _clientOptions;

    private readonly ILoggerFactory? _loggerFactory;

    private readonly TimeSpan _defaultRequestTimeout;

    private readonly int _maxToolsListPages;

    private readonly int _maxToolsPerServer;

    private readonly int _maxToolsPerListPage;

    private readonly int _maxToolsTotalBytes;

    private readonly long _toolOutputCapBytes;

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
        TimeSpan defaultRequestTimeout,
        int maxToolsListPages,
        long toolOutputCapBytes,
        int maxToolsPerServer,
        int maxToolsPerListPage,
        int maxToolsTotalBytes,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(clientTransport);

        ArgumentNullException.ThrowIfNull(clientOptions);

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

        _clientTransport = clientTransport;

        _clientOptions = clientOptions;

        _defaultRequestTimeout = defaultRequestTimeout;

        _maxToolsListPages = maxToolsListPages;

        _maxToolsPerServer = maxToolsPerServer;

        _maxToolsPerListPage = maxToolsPerListPage;

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

            _sdkClient = await SdkMcpClient
                .CreateAsync(_clientTransport, _clientOptions, _loggerFactory, cancellationToken)
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

        List<Tool> collected = [];

        string? cursor = null;

        HashSet<string> seenCursors = new(StringComparer.Ordinal);

        for (int page = 0; page < _maxToolsListPages && collected.Count < _maxToolsPerServer; page++)
        {
            if (cursor is not null && !seenCursors.Add(cursor))
            {
                // A server that echoes back a cursor it already returned would otherwise loop forever.
                break;
            }

            ListToolsResult pageResult = await _sdkClient
                .ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, cancellationToken)
                .ConfigureAwait(false);

            int toolsOnPage = 0;

            foreach (Tool tool in pageResult.Tools)
            {
                if (collected.Count >= _maxToolsPerServer || toolsOnPage >= _maxToolsPerListPage)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(tool.Name))
                {
                    continue;
                }

                collected.Add(tool);

                toolsOnPage++;
            }

            cursor = pageResult.NextCursor;

            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }

        List<McpBridgeTool> bridgeTools = new(collected.Count);

        long totalBytes = 0L;

        foreach (Tool tool in collected)
        {
            string description = McpSecurityLimits.BoundToolDescription(tool.Description ?? string.Empty);

            System.Text.Json.JsonElement inputSchema = McpSecurityLimits.BoundToolInputSchema(
                tool.InputSchema,
                McpJsonSerializerContext.Default);

            long toolBytes = Encoding.UTF8.GetByteCount(description) + Encoding.UTF8.GetByteCount(inputSchema.GetRawText());

            if (totalBytes + toolBytes > _maxToolsTotalBytes)
            {
                break;
            }

            totalBytes += toolBytes;

            bridgeTools.Add(new McpBridgeTool(tool.Name, description, inputSchema, this, _toolOutputCapBytes));
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

        TimeSpan timeout = requestTimeout ?? _defaultRequestTimeout;

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
