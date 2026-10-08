using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ModelContextProtocol.Client;
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

    private const int DelimiterCap = 256;

    // The internal server measures the whole line it reads, newline delimiter included (the same rule
    // ChannelClientTransport applies on the SDK path). A writer that measures the payload alone lets a
    // payload of exactly the cap through, which the server then drops as one byte over, stranding the
    // call until its own timeout. Each writer must therefore refuse a payload equal to the cap.
    [Theory]
    [InlineData("request")]
    [InlineData("notification")]
    [InlineData("opaque-request")]
    public async Task Writers_reject_a_payload_equal_to_the_cap_because_the_delimiter_makes_the_line_one_byte_over(
        string writer)
    {
        (InProcessMcpTransport transport, Channel<string> clientToServer) = CreateTransport();

        await using (transport)
        {
            await transport.StartAsync();

            McpLineSizeExceededException oversized = await Assert.ThrowsAsync<McpLineSizeExceededException>(
                () => WriteAsync(transport, writer, payloadBytes: DelimiterCap));

            Assert.Equal(DelimiterCap + 1, oversized.ActualUtf8Bytes);

            Assert.False(clientToServer.Reader.TryRead(out _), "A line over the cap was written to the server channel.");
        }
    }

    [Theory]
    [InlineData("request")]
    [InlineData("notification")]
    [InlineData("opaque-request")]
    public async Task Writers_accept_a_payload_whose_line_with_delimiter_fits_the_cap_exactly(string writer)
    {
        (InProcessMcpTransport transport, Channel<string> clientToServer) = CreateTransport();

        await using (transport)
        {
            await transport.StartAsync();

            await WriteAsync(transport, writer, payloadBytes: DelimiterCap - 1);

            Assert.True(clientToServer.Reader.TryRead(out string? line));

            Assert.Equal(DelimiterCap, Encoding.UTF8.GetByteCount(line));

            Assert.EndsWith("\n", line, StringComparison.Ordinal);
        }
    }

    private static (InProcessMcpTransport Transport, Channel<string> ClientToServer) CreateTransport()
    {
        BoundedChannelOptions lineOptions = new(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        };

        Channel<string> clientToServer = Channel.CreateBounded<string>(lineOptions);

        Channel<string> serverToClient = Channel.CreateBounded<string>(lineOptions);

        return (
            new InProcessMcpTransport(clientToServer.Writer, serverToClient.Reader, maxJsonRpcLineBytes: DelimiterCap),
            clientToServer);
    }

    private static Task WriteAsync(InProcessMcpTransport transport, string writer, int payloadBytes) =>
        writer switch
        {
            "notification" => transport.WriteNotificationAsync(NotificationWithPayloadBytes(payloadBytes)),
            "opaque-request" => transport.WriteRequestWithOpaqueAmbientAsync(RequestWithPayloadBytes(payloadBytes)),
            _ => transport.WriteRequestAsync(RequestWithPayloadBytes(payloadBytes)),
        };

    // A ping request whose serialized JSON, newline excluded, is exactly payloadBytes UTF-8 bytes.
    private static JsonRpcRequest RequestWithPayloadBytes(int payloadBytes)
    {
        int baseline = SerializedBytes(PaddedRequest(0));

        JsonRpcRequest request = PaddedRequest(payloadBytes - baseline);

        Assert.Equal(payloadBytes, SerializedBytes(request));

        return request;
    }

    private static JsonRpcNotification NotificationWithPayloadBytes(int payloadBytes)
    {
        int baseline = SerializedBytes(PaddedNotification(0));

        JsonRpcNotification notification = PaddedNotification(payloadBytes - baseline);

        Assert.Equal(payloadBytes, SerializedBytes(notification));

        return notification;
    }

    private static JsonRpcRequest PaddedRequest(int padding) =>
        new()
        {
            Method = "ping" + new string('p', padding),
            Id = JsonSerializer.SerializeToElement("1", McpJsonSerializerContext.Default.String),
        };

    private static JsonRpcNotification PaddedNotification(int padding) =>
        new()
        {
            Method = "notifications/ping" + new string('p', padding),
        };

    private static int SerializedBytes(JsonRpcRequest request) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, McpJsonSerializerContext.Default.JsonRpcRequest));

    private static int SerializedBytes(JsonRpcNotification notification) =>
        Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(notification, McpJsonSerializerContext.Default.JsonRpcNotification));

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

    // A <see cref="..."/> that names a type nobody declares any more silently rots: the compiler
    // never checks it (no documentation file is generated) and it keeps pointing a maintainer at
    // a transport that the SDK migration deleted. Every Mcp-prefixed cref in the MCP sources must
    // resolve to a real type in either the Infrastructure assembly or the MCP SDK assembly.
    [Fact]
    public void Mcp_doc_comment_crefs_name_types_that_still_exist()
    {
        string mcpSourceRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Mcp");

        Assert.True(Directory.Exists(mcpSourceRoot), $"Missing MCP source root: {mcpSourceRoot}");

        HashSet<string> declaredTypeNames = typeof(McpOutboundLineGuard).Assembly.GetTypes()
            .Concat(typeof(IClientTransport).Assembly.GetTypes())
            .Select(static type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Regex crefPattern = new("<see cref=\"(?<name>Mcp[A-Za-z0-9_]*)\"\\s*/>");

        List<string> unresolved = [];

        foreach (string file in Directory.EnumerateFiles(mcpSourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in crefPattern.Matches(File.ReadAllText(file)))
            {
                string name = match.Groups["name"].Value;

                if (!declaredTypeNames.Contains(name))
                {
                    unresolved.Add($"{Path.GetFileName(file)}: {name}");
                }
            }
        }

        Assert.True(
            unresolved.Count == 0,
            $"XML doc cref targets naming types that no longer exist:\n  {string.Join("\n  ", unresolved)}");
    }

    private static string FindRepositoryRoot() =>
        global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot();
}
