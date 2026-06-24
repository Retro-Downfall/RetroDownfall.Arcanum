using System.Text.Json;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

internal sealed class FakeMcpTransport : IMcpTransport
{

    private readonly Channel<McpInboundEnvelope> _inbound;

    private volatile bool _disposed;

    private Func<JsonRpcRequest, Task<JsonRpcResponse?>>? _requestHandler;

    public FakeMcpTransport()
    {

        BoundedChannelOptions options = new(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        };

        _inbound = Channel.CreateBounded<McpInboundEnvelope>(options);

    }

    public ChannelReader<McpInboundEnvelope> InboundReader => _inbound.Reader;

    public List<JsonRpcRequest> WrittenRequests { get; } = [];

    public List<JsonRpcNotification> WrittenNotifications { get; } = [];

    public void SetRequestHandler(Func<JsonRpcRequest, Task<JsonRpcResponse?>> handler)
    {

        _requestHandler = handler;

    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.CompletedTask;

    }

    public async Task WriteRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        WrittenRequests.Add(request);

        if (_requestHandler is null)
        {

            return;

        }

        JsonRpcResponse? response = await _requestHandler(request).ConfigureAwait(false);

        if (response is null)
        {

            return;

        }

        await _inbound.Writer
            .WriteAsync(new McpInboundEnvelope(McpInboundKind.Response, response, null, null), cancellationToken)
            .ConfigureAwait(false);

    }

    public Task WriteNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken = default)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        WrittenNotifications.Add(notification);

        return Task.CompletedTask;

    }

    public Task PushResponseAsync(JsonRpcResponse response, CancellationToken cancellationToken = default)
    {

        return _inbound.Writer
            .WriteAsync(new McpInboundEnvelope(McpInboundKind.Response, response, null, null), cancellationToken)
            .AsTask();

    }

    public static JsonElement StringId(string id)
    {

        return JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.String);

    }

    public static JsonElement ObjectResult(object value)
    {

        return JsonSerializer.SerializeToElement(value);

    }

    public async ValueTask DisposeAsync()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _inbound.Writer.TryComplete();

        await Task.CompletedTask.ConfigureAwait(false);

    }

}
