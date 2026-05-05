using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// MCP client transport over paired <see cref="Channel{T}"/> lines (newline-delimited JSON-RPC), in-process with no stdio.
/// </summary>
internal sealed class InProcessMcpTransport : IMcpTransport
{
    private const int ChannelCapacity = 256;

    private readonly ChannelWriter<string> _toServer;

    private readonly ChannelReader<string> _fromServer;

    private readonly Channel<McpInboundEnvelope> _inbound;

    private readonly McpJsonSerializerContext _json;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly CancellationTokenSource _lifetimeCts = new();

    private Task? _readLoopTask;

    private bool _started;

    private volatile bool _disposed;

    internal InProcessMcpTransport(
        ChannelWriter<string> toServer,
        ChannelReader<string> fromServer,
        McpJsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(toServer);

        ArgumentNullException.ThrowIfNull(fromServer);

        _toServer = toServer;

        _fromServer = fromServer;

        _json = jsonContext ?? McpJsonSerializerContext.Default;

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

    /// <summary>
    /// Creates a client transport and matching <see cref="ArcanumInternalToolServer"/> sharing bounded line channels.
    /// </summary>
    public static (InProcessMcpTransport Transport, ArcanumInternalToolServer Server) CreatePair(
        ILogger<ArcanumInternalToolServer>? logger = null)
    {
        BoundedChannelOptions lineOptions = new(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        };

        Channel<string> clientToServer = Channel.CreateBounded<string>(lineOptions);

        Channel<string> serverToClient = Channel.CreateBounded<string>(lineOptions);

        InProcessMcpTransport transport = new(clientToServer.Writer, serverToClient.Reader);

        ArcanumInternalToolServer server = new(clientToServer.Reader, serverToClient.Writer, logger);

        return (transport, server);
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

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string json = JsonSerializer.Serialize(request, _json.JsonRpcRequest);

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
                    McpInboundEnvelope envelope = McpInboundJsonRpc.ParseInbound(line, _json);

                    await _inbound.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (JsonException)
                {
                    // Malformed line from in-process server — ignore (no stderr analogue).
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
