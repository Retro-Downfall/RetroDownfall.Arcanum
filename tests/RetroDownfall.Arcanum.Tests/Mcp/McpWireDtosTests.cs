using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpWireDtosTests
{

    private static readonly McpJsonSerializerContext Json = McpJsonSerializerContext.Default;

    [Fact]
    public void McpInitializeParams_round_trips()
    {

        McpInitializeParams original = new()
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpClientCapabilities(),
            ClientInfo = new McpClientInfo { Name = "arcanum", Version = "1.0.0" },
        };

        string wire = JsonSerializer.Serialize(original, Json.McpInitializeParams);

        McpInitializeParams? parsed = JsonSerializer.Deserialize(wire, Json.McpInitializeParams);

        Assert.NotNull(parsed);

        Assert.Equal("2024-11-05", parsed!.ProtocolVersion);

        Assert.Equal("arcanum", parsed.ClientInfo.Name);

    }

    [Fact]
    public void McpToolsCallParams_round_trips_arguments_object()
    {

        JsonElement args = JsonSerializer.SerializeToElement(
            new ReadFileChunkParams { RelativePath = "src/a.cs", StartLine = 1, EndLine = 5 },
            Json.ReadFileChunkParams);

        McpToolsCallParams original = new()
        {
            Name = "read_file_chunk",
            Arguments = args,
        };

        string wire = JsonSerializer.Serialize(original, Json.McpToolsCallParams);

        McpToolsCallParams? parsed = JsonSerializer.Deserialize(wire, Json.McpToolsCallParams);

        Assert.NotNull(parsed);

        Assert.Equal("read_file_chunk", parsed!.Name);

        Assert.Equal("src/a.cs", parsed.Arguments.GetProperty("relativePath").GetString());

    }

    [Fact]
    public void McpCancelledParams_round_trips_request_id()
    {

        McpCancelledParams original = new()
        {
            RequestId = "deadbeef",
            Reason = "Client cancelled",
        };

        string wire = JsonSerializer.Serialize(original, Json.McpCancelledParams);

        McpCancelledParams? parsed = JsonSerializer.Deserialize(wire, Json.McpCancelledParams);

        Assert.NotNull(parsed);

        Assert.Equal("deadbeef", parsed!.RequestId);

        Assert.Equal("Client cancelled", parsed.Reason);

    }

}
