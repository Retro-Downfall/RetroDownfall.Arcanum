using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Raw newline-delimited JSON-RPC test harness over paired <see cref="Channel{T}"/> lines, in-process
/// with no stdio. Production code no longer uses this class to drive
/// <see cref="ArcanumInternalToolServer"/> — <see cref="ChannelClientTransport"/> (an SDK
/// <c>IClientTransport</c>) does, via the same channel pair created by
/// <see cref="CreateServerChannelPair"/>. This class is retained purely as a low-level test seam that
/// exercises the server's wire protocol directly (raw <see cref="JsonRpcRequest"/> objects and inbound
/// envelopes) without going through the SDK, since <see cref="ArcanumInternalToolServer"/>'s own framing
/// is unchanged by the SDK migration.
/// </summary>
internal sealed class InProcessMcpTransport : IMcpTransport
{
    private const int ChannelCapacity = 256;

    private const int MalformedLinePreviewLength = 80;

    private readonly ChannelWriter<string> _toServer;

    private readonly ChannelReader<string> _fromServer;

    private readonly Channel<McpInboundEnvelope> _inbound;

    private readonly McpJsonSerializerContext _json;

    private readonly int _maxJsonRpcLineBytes;

    private readonly ILogger? _logger;

    private readonly string _ambientConnectionKey;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly CancellationTokenSource _lifetimeCts = new();

    private Task? _readLoopTask;

    private bool _started;

    private volatile bool _disposed;

    internal CancellationToken LifetimeCancellation => _lifetimeCts.Token;

    /// <summary>Per-connection key shared with the paired <see cref="ArcanumInternalToolServer"/>.</summary>
    internal string AmbientConnectionKey => _ambientConnectionKey;

    internal InProcessMcpTransport(
        ChannelWriter<string> toServer,
        ChannelReader<string> fromServer,
        int maxJsonRpcLineBytes,
        McpJsonSerializerContext? jsonContext = null,
        ILogger? logger = null,
        string? ambientConnectionKey = null)
    {
        ArgumentNullException.ThrowIfNull(toServer);

        ArgumentNullException.ThrowIfNull(fromServer);

        if (maxJsonRpcLineBytes < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxJsonRpcLineBytes));

        }

        _toServer = toServer;

        _fromServer = fromServer;

        _maxJsonRpcLineBytes = maxJsonRpcLineBytes;

        _json = jsonContext ?? McpJsonSerializerContext.Default;

        _logger = logger;

        _ambientConnectionKey = string.IsNullOrWhiteSpace(ambientConnectionKey)
            ? Guid.NewGuid().ToString("N")
            : ambientConnectionKey;

        BoundedChannelOptions envelopeOptions = new(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        };

        _inbound = Channel.CreateBounded<McpInboundEnvelope>(envelopeOptions);
    }

    /// <inheritdoc />
    public ChannelReader<McpInboundEnvelope> InboundReader => _inbound.Reader;

    // W3.4 Group C #4: test seam that writes a raw line directly to the server channel,
    // bypassing the outbound line-size guard. Used to exercise the server's INBOUND line cap
    // (which is a separate defense) without the client's outbound cap rejecting the line
    // first. Production code must not call this.
    internal Task WriteRawLineForTestsAsync(string line, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _toServer.WriteAsync(line + "\n", cancellationToken).AsTask();

    }

    /// <summary>
    /// Creates a raw test-harness client transport and matching <see cref="ArcanumInternalToolServer"/>
    /// sharing bounded line channels. Production wiring uses <see cref="CreateServerChannelPair"/> instead
    /// (see the class remarks).
    /// </summary>
    public static (InProcessMcpTransport Transport, ArcanumInternalToolServer Server) CreatePair(
        IHumanPromptRegistry humanPromptRegistry,
        IServiceScopeFactory scopeFactory,
        IUnseenServantPacer pacer,
        string? workspaceRootNormalizedOrNull,
        TimeSpan executeCommandTimeout,
        int executeCommandTimeoutSecondsForDisplay,
        int listDirectoryMaxPaths,
        IntelligenceSettings intelligenceSettings,
        long maxFileReadSizeBytes,
        bool conclaveEnabled,
        bool sagaEnabled,
        bool a2aClientEnabled,
        bool attachmentsToolEnabled,
        int maxJsonRpcLineBytes,
        ILogger<ArcanumInternalToolServer>? logger = null,
        McpJsonSerializerContext? jsonContext = null,
        bool allowHostProcessTools = true)
    {
        (Channel<string> clientToServer, Channel<string> serverToClient, ArcanumInternalToolServer server) = BuildChannelsAndServer(
            humanPromptRegistry,
            scopeFactory,
            pacer,
            workspaceRootNormalizedOrNull,
            executeCommandTimeout,
            executeCommandTimeoutSecondsForDisplay,
            listDirectoryMaxPaths,
            intelligenceSettings,
            maxFileReadSizeBytes,
            conclaveEnabled,
            sagaEnabled,
            a2aClientEnabled,
            attachmentsToolEnabled,
            maxJsonRpcLineBytes,
            logger,
            jsonContext,
            allowHostProcessTools);

        InProcessMcpTransport transport = new(
            clientToServer.Writer,
            serverToClient.Reader,
            maxJsonRpcLineBytes,
            jsonContext,
            logger,
            server.AmbientConnectionKey);

        return (transport, server);
    }

    /// <summary>
    /// Creates the raw channel pair and matching <see cref="ArcanumInternalToolServer"/> for production
    /// wiring: the caller attaches an SDK <c>IClientTransport</c> (<see cref="ChannelClientTransport"/>)
    /// to <paramref name="server"/>'s <see cref="ChannelWriter{T}"/>/<see cref="ChannelReader{T}"/> pair.
    /// </summary>
    public static (ChannelWriter<string> ToServer, ChannelReader<string> FromServer, ArcanumInternalToolServer Server) CreateServerChannelPair(
        IHumanPromptRegistry humanPromptRegistry,
        IServiceScopeFactory scopeFactory,
        IUnseenServantPacer pacer,
        string? workspaceRootNormalizedOrNull,
        TimeSpan executeCommandTimeout,
        int executeCommandTimeoutSecondsForDisplay,
        int listDirectoryMaxPaths,
        IntelligenceSettings intelligenceSettings,
        long maxFileReadSizeBytes,
        bool conclaveEnabled,
        bool sagaEnabled,
        bool a2aClientEnabled,
        bool attachmentsToolEnabled,
        int maxJsonRpcLineBytes,
        ILogger<ArcanumInternalToolServer>? logger = null,
        McpJsonSerializerContext? jsonContext = null,
        bool allowHostProcessTools = false)
    {
        (Channel<string> clientToServer, Channel<string> serverToClient, ArcanumInternalToolServer server) = BuildChannelsAndServer(
            humanPromptRegistry,
            scopeFactory,
            pacer,
            workspaceRootNormalizedOrNull,
            executeCommandTimeout,
            executeCommandTimeoutSecondsForDisplay,
            listDirectoryMaxPaths,
            intelligenceSettings,
            maxFileReadSizeBytes,
            conclaveEnabled,
            sagaEnabled,
            a2aClientEnabled,
            attachmentsToolEnabled,
            maxJsonRpcLineBytes,
            logger,
            jsonContext,
            allowHostProcessTools);

        return (clientToServer.Writer, serverToClient.Reader, server);
    }

    private static (Channel<string> ClientToServer, Channel<string> ServerToClient, ArcanumInternalToolServer Server) BuildChannelsAndServer(
        IHumanPromptRegistry humanPromptRegistry,
        IServiceScopeFactory scopeFactory,
        IUnseenServantPacer pacer,
        string? workspaceRootNormalizedOrNull,
        TimeSpan executeCommandTimeout,
        int executeCommandTimeoutSecondsForDisplay,
        int listDirectoryMaxPaths,
        IntelligenceSettings intelligenceSettings,
        long maxFileReadSizeBytes,
        bool conclaveEnabled,
        bool sagaEnabled,
        bool a2aClientEnabled,
        bool attachmentsToolEnabled,
        int maxJsonRpcLineBytes,
        ILogger<ArcanumInternalToolServer>? logger,
        McpJsonSerializerContext? jsonContext,
        bool allowHostProcessTools)
    {
        ArgumentNullException.ThrowIfNull(humanPromptRegistry);

        ArgumentNullException.ThrowIfNull(scopeFactory);

        ArgumentNullException.ThrowIfNull(pacer);

        ArgumentNullException.ThrowIfNull(intelligenceSettings);

        // clientToServer: single writer (the client transport), single reader (RunAsync's read loop).
        Channel<string> clientToServer = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        });

        // serverToClient: multiple writers. ArcanumInternalToolServer now dispatches each inbound
        // line concurrently (see RunAsync) so a notifications/cancelled for an in-flight tools/call
        // can be read and processed while that call is still running instead of being stuck behind
        // it in a sequential read loop — so more than one handler can be writing its response here
        // at the same time. Single reader (the client transport's pump).
        Channel<string> serverToClient = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true,
        });

        string ambientConnectionKey = Guid.NewGuid().ToString("N");

        ArcanumInternalToolServer server = new(
            clientToServer.Reader,
            serverToClient.Writer,
            humanPromptRegistry,
            scopeFactory,
            pacer,
            workspaceRootNormalizedOrNull,
            executeCommandTimeout,
            executeCommandTimeoutSecondsForDisplay,
            listDirectoryMaxPaths,
            intelligenceSettings,
            maxFileReadSizeBytes,
            conclaveEnabled,
            sagaEnabled,
            a2aClientEnabled,
            attachmentsToolEnabled,
            maxJsonRpcLineBytes,
            allowHostProcessTools,
            ambientConnectionKey,
            logger,
            jsonContext);

        return (clientToServer, serverToClient, server);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
        {
            throw new InvalidOperationException("InProcessMcpTransport has already been started.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        _started = true;

        CancellationToken token = _lifetimeCts.Token;

        _readLoopTask = Task.Run(() => ReadFromServerLoopAsync(token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        JsonRpcRequest toSend = SessionAttachmentAmbientSend.ApplyAmbientBinding(_ambientConnectionKey, request);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string json = JsonSerializer.Serialize(toSend, _json.JsonRpcRequest);

            McpOutboundLineGuard.Enforce(json, _maxJsonRpcLineBytes);

            await _toServer.WriteAsync(json + "\n", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Test seam: write a request after applying opaque-token ambient binding only (skips request-id map).
    /// </summary>
    internal async Task WriteRequestWithOpaqueAmbientAsync(
        JsonRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        JsonRpcRequest toSend = SessionAttachmentAmbientSend.ApplyOpaqueTokenBindingOnly(request);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string json = JsonSerializer.Serialize(toSend, _json.JsonRpcRequest);

            McpOutboundLineGuard.Enforce(json, _maxJsonRpcLineBytes);

            await _toServer.WriteAsync(json + "\n", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task WriteNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string json = JsonSerializer.Serialize(notification, _json.JsonRpcNotification);

            McpOutboundLineGuard.Enforce(json, _maxJsonRpcLineBytes);

            await _toServer.WriteAsync(json + "\n", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        _toServer.TryComplete();

        _inbound.Writer.TryComplete();

        Task readLoop = _readLoopTask ?? Task.CompletedTask;

        await AwaitTaskGracefullyAsync(readLoop).ConfigureAwait(false);

        _lifetimeCts.Dispose();

        _writeLock.Dispose();
    }

    private async Task ReadFromServerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (string line in _fromServer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    McpInboundEnvelope envelope = McpInboundJsonRpc.ParseInbound(line, _json, _maxJsonRpcLineBytes);

                    await _inbound.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ChannelClosedException)
                {
                    // The inbound channel is being torn down; stop reading further lines.
                    return;
                }
                catch (JsonException ex)
                {
                    if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
                    {
                        string preview = line.Length > MalformedLinePreviewLength
                            ? line[..MalformedLinePreviewLength] + "\u2026"
                            : line;

                        _logger.LogDebug(
                            ex,
                            "InProcessMcpTransport dropped a malformed inbound line: {LinePreview}",
                            preview);
                    }
                }
                catch (Exception ex)
                {
                    if (_logger is not null && _logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(
                            ex,
                            "InProcessMcpTransport read loop dropped a line due to an unexpected exception ({ExceptionType}).",
                            ex.GetType().Name);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
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
