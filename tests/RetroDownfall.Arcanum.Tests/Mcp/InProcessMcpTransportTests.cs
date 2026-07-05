using System.Text.Json;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class InProcessMcpTransportTests
{

    [Fact]
    public async Task WriteRequestAsync_writes_newline_delimited_json_to_server_channel()
    {

        (InProcessMcpTransport transport, Channel<string> toServer, _) = CreateTransportPair();

        await using (transport)
        {

            await transport.StartAsync();

            JsonRpcRequest request = new()
            {
                Method = "initialize",
                Id = JsonSerializer.SerializeToElement("1", McpJsonSerializerContext.Default.String),
            };

            await transport.WriteRequestAsync(request);

            string line = await toServer.Reader.ReadAsync();

            Assert.EndsWith("\n", line);

            Assert.Contains("\"initialize\"", line, StringComparison.Ordinal);

        }

    }

    [Fact]
    public async Task Read_loop_parses_inbound_response_envelope()
    {

        (InProcessMcpTransport transport, _, ChannelWriter<string> fromServer) = CreateTransportPair();

        await using (transport)
        {

            await transport.StartAsync();

            string responseLine =
                "{\"jsonrpc\":\"2.0\",\"id\":\"9\",\"result\":{\"ok\":true}}";

            await fromServer.WriteAsync(responseLine);

            McpInboundEnvelope envelope = await transport.InboundReader.ReadAsync();

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            Assert.NotNull(envelope.Response);

            Assert.Equal("9", envelope.Response!.Id.GetRawText().Trim('"'));

        }

    }

    [Fact]
    public async Task StartAsync_twice_throws_InvalidOperationException()
    {

        (InProcessMcpTransport transport, _, _) = CreateTransportPair();

        await using (transport)
        {

            await transport.StartAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.StartAsync());

        }

    }

    private static (InProcessMcpTransport Transport, Channel<string> ToServer, ChannelWriter<string> FromServer) CreateTransportPair()
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
            maxJsonRpcLineBytes: 2_097_152);

        return (transport, clientToServer, serverToClient.Writer);

    }

}
