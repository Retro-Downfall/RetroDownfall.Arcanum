using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// SDK <see cref="IClientTransport"/> over a paired <see cref="Channel{T}"/> of newline-delimited
/// JSON-RPC lines, connecting directly to <see cref="ArcanumInternalToolServer"/> with no subprocess or
/// network hop. Wraps the raw channel pair produced by
/// <see cref="InProcessMcpTransport.CreateServerChannelPair"/>. <see cref="ArcanumInternalToolServer"/>'s
/// own wire framing is unchanged; this class only bridges its newline-delimited JSON strings to the SDK's
/// <see cref="JsonRpcMessage"/> duplex.
/// </summary>
internal sealed class ChannelClientTransport(
    ChannelWriter<string> toServer,
    ChannelReader<string> fromServer,
    int maxJsonRpcLineBytes,
    string? ambientConnectionKey = null) : IClientTransport
{

    private readonly string _ambientConnectionKey = string.IsNullOrWhiteSpace(ambientConnectionKey)
        ? Guid.NewGuid().ToString("N")
        : ambientConnectionKey;

    /// <inheritdoc />
    public string Name => "arcanum-internal (in-process)";

    /// <inheritdoc />
    public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ChannelSessionTransport session = new(toServer, fromServer, maxJsonRpcLineBytes, _ambientConnectionKey);

        session.Start();

        return Task.FromResult<ITransport>(session);
    }

    [ExcludeFromCodeCoverage] // Reason: thin channel<->JsonRpcMessage bridge; covered via ChannelClientTransportTests round-trip and line-cap cases.
    private sealed class ChannelSessionTransport : ITransport
    {
        // AOT-safe polymorphic (de)serialization: JsonSerializer.Serialize/Deserialize<T>(…, JsonSerializerOptions)
        // are annotated RequiresUnreferencedCode/RequiresDynamicCode since the analyzer can't prove the
        // options' resolver is source-generated. Resolving the JsonTypeInfo once via the officially
        // documented GetTypeInfo(Type) pattern and calling the JsonTypeInfo overloads avoids both warnings.
        private static readonly JsonTypeInfo<JsonRpcMessage> JsonRpcMessageTypeInfo =
            (JsonTypeInfo<JsonRpcMessage>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage));

        private readonly ChannelWriter<string> _toServer;

        private readonly ChannelReader<string> _fromServer;

        private readonly int _maxJsonRpcLineBytes;

        private readonly string _ambientConnectionKey;

        private readonly Channel<JsonRpcMessage> _inbound = Channel.CreateUnbounded<JsonRpcMessage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        private readonly CancellationTokenSource _lifetimeCts = new();

        private readonly SemaphoreSlim _writeLock = new(1, 1);

        private Task? _pumpTask;

        private volatile bool _disposed;

        public ChannelSessionTransport(
            ChannelWriter<string> toServer,
            ChannelReader<string> fromServer,
            int maxJsonRpcLineBytes,
            string ambientConnectionKey)
        {
            _toServer = toServer;

            _fromServer = fromServer;

            _maxJsonRpcLineBytes = maxJsonRpcLineBytes;

            _ambientConnectionKey = ambientConnectionKey;
        }

        /// <inheritdoc />
        public string? SessionId => null;

        /// <inheritdoc />
        public ChannelReader<JsonRpcMessage> MessageReader => _inbound.Reader;

        public void Start()
        {
            _pumpTask = Task.Run(() => PumpInboundAsync(_lifetimeCts.Token), CancellationToken.None);
        }

        /// <inheritdoc />
        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            SessionAttachmentAmbientSend.BindSdkToolsCall(_ambientConnectionKey, message);

            string json = JsonSerializer.Serialize(message, JsonRpcMessageTypeInfo);

            if (McpSecurityLimits.ExceedsMaxLineUtf8Bytes(json, _maxJsonRpcLineBytes))
            {
                throw new McpLineSizeExceededException(_maxJsonRpcLineBytes, Encoding.UTF8.GetByteCount(json));
            }

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _toServer.WriteAsync(json + "\n", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task PumpInboundAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (string line in _fromServer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (McpSecurityLimits.ExceedsMaxLineUtf8Bytes(line, _maxJsonRpcLineBytes))
                    {
                        // The server enforces its own inbound cap on its side of the channel; an
                        // oversized line here would only originate from a misbehaving peer. Drop it
                        // rather than fault the whole session.
                        continue;
                    }

                    JsonRpcMessage? message;

                    try
                    {
                        message = JsonSerializer.Deserialize(line, JsonRpcMessageTypeInfo);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (message is null)
                    {
                        continue;
                    }

                    await _inbound.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
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

        /// <inheritdoc />
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

            Task pump = _pumpTask ?? Task.CompletedTask;

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

            try
            {
                await pump.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Best-effort shutdown.
            }

            _lifetimeCts.Dispose();

            _writeLock.Dispose();
        }
    }
}
