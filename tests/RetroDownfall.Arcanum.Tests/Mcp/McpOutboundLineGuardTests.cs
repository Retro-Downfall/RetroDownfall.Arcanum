using System.Text.Json;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpOutboundLineGuardTests
{

    // W3.4 Group C #4: an outbound request whose serialized line exceeds MaxJsonRpcLineBytes
    // must be rejected BEFORE any byte is written to the server channel. The transport throws
    // McpLineSizeExceededException and the channel stays empty (no partial/oversized line
    // reaches the server). AOT-safe: serialization uses the source-generated context.
    [Fact]
    public async Task WriteRequestAsync_oversized_line_throws_and_writes_nothing()
    {

        BoundedChannelOptions lineOptions = new(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        };

        Channel<string> clientToServer = Channel.CreateBounded<string>(lineOptions);

        Channel<string> serverToClient = Channel.CreateBounded<string>(lineOptions);

        InProcessMcpTransport transport = new(
            clientToServer.Writer,
            serverToClient.Reader,
            maxJsonRpcLineBytes: 64);

        await using (transport)
        {

            await transport.StartAsync();

            JsonRpcRequest request = new()
            {
                Method = new string('x', 128),
                Id = JsonSerializer.SerializeToElement("1", McpJsonSerializerContext.Default.String),
            };

            await Assert.ThrowsAsync<McpLineSizeExceededException>(() => transport.WriteRequestAsync(request));

            Assert.False(clientToServer.Reader.TryRead(out _), "Oversized line was written to the server channel.");

        }

    }

    [Fact]
    public async Task WriteNotificationAsync_oversized_line_throws_and_writes_nothing()
    {

        BoundedChannelOptions lineOptions = new(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        };

        Channel<string> clientToServer = Channel.CreateBounded<string>(lineOptions);

        Channel<string> serverToClient = Channel.CreateBounded<string>(lineOptions);

        InProcessMcpTransport transport = new(
            clientToServer.Writer,
            serverToClient.Reader,
            maxJsonRpcLineBytes: 32);

        await using (transport)
        {

            await transport.StartAsync();

            JsonRpcNotification notification = new()
            {
                Method = new string('y', 64),
            };

            await Assert.ThrowsAsync<McpLineSizeExceededException>(() => transport.WriteNotificationAsync(notification));

            Assert.False(clientToServer.Reader.TryRead(out _), "Oversized notification was written to the server channel.");

        }

    }

    [Fact]
    public async Task WriteRequestAsync_undersized_line_is_written_normally()
    {

        BoundedChannelOptions lineOptions = new(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        };

        Channel<string> clientToServer = Channel.CreateBounded<string>(lineOptions);

        Channel<string> serverToClient = Channel.CreateBounded<string>(lineOptions);

        InProcessMcpTransport transport = new(
            clientToServer.Writer,
            serverToClient.Reader,
            maxJsonRpcLineBytes: 4096);

        await using (transport)
        {

            await transport.StartAsync();

            JsonRpcRequest request = new()
            {
                Method = "ping",
                Id = JsonSerializer.SerializeToElement("1", McpJsonSerializerContext.Default.String),
            };

            await transport.WriteRequestAsync(request);

            Assert.True(clientToServer.Reader.TryRead(out string? line));

            Assert.EndsWith("\n", line);

        }

    }

}
