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

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

}
