using System.Text;

using System.Text.Json;

using System.Text.Json.Nodes;

using System.Threading.Channels;

using ModelContextProtocol;

using ModelContextProtocol.Protocol;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class ChannelClientTransportTests
{
    private const int MaxLineBytes = 512;

    [Fact]
    public async Task SendMessageAsync_rejects_request_whose_line_with_delimiter_exceeds_cap()
    {
        // The internal server measures the whole line it reads, delimiter included. A request whose JSON
        // is exactly the cap is therefore one byte over it on the wire and would be dropped by the
        // server, stranding the call until its own timeout; the client must refuse it up front.
        (Channel<string> toServer, Channel<string> fromServer) = CreateChannels();

        await using ITransport transport = await ConnectAsync(toServer, fromServer);

        JsonRpcRequest atCap = RequestWithJsonBytes(MaxLineBytes);

        _ = await Assert.ThrowsAsync<McpLineSizeExceededException>(
            () => transport.SendMessageAsync(atCap));

        Assert.False(toServer.Reader.TryRead(out _), "A line over the cap was written to the server channel.");
    }

    [Fact]
    public async Task SendMessageAsync_writes_a_request_whose_line_with_delimiter_fits_the_cap_exactly()
    {
        (Channel<string> toServer, Channel<string> fromServer) = CreateChannels();

        await using ITransport transport = await ConnectAsync(toServer, fromServer);

        JsonRpcRequest justUnder = RequestWithJsonBytes(MaxLineBytes - 1);

        await transport.SendMessageAsync(justUnder);

        Assert.True(toServer.Reader.TryRead(out string? line));

        Assert.Equal(MaxLineBytes, Encoding.UTF8.GetByteCount(line));

        Assert.EndsWith("\n", line, StringComparison.Ordinal);
    }

    private static (Channel<string> ToServer, Channel<string> FromServer) CreateChannels() =>
        (Channel.CreateUnbounded<string>(), Channel.CreateUnbounded<string>());

    private static async Task<ITransport> ConnectAsync(Channel<string> toServer, Channel<string> fromServer)
    {
        ChannelClientTransport clientTransport = new(
            toServer.Writer,
            fromServer.Reader,
            MaxLineBytes);

        return await clientTransport.ConnectAsync();
    }

    // Builds a tools/call request whose serialized JSON is exactly jsonBytes UTF-8 bytes long.
    private static JsonRpcRequest RequestWithJsonBytes(int jsonBytes)
    {
        int baseline = SerializedBytes(PaddedRequest(0));

        JsonRpcRequest request = PaddedRequest(jsonBytes - baseline);

        Assert.Equal(jsonBytes, SerializedBytes(request));

        return request;
    }

    private static JsonRpcRequest PaddedRequest(int padding) =>
        new()
        {
            Method = "tools/call",
            Id = new RequestId(1),
            Params = new JsonObject { ["name"] = "read_file_chunk", ["pad"] = new string('p', padding) },
        };

    private static int SerializedBytes(JsonRpcRequest request) =>
        Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize<JsonRpcMessage>(request, McpJsonUtilities.DefaultOptions));
}
