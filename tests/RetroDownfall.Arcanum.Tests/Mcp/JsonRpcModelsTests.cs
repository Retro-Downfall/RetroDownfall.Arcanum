using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class JsonRpcModelsTests
{

    private static readonly McpJsonSerializerContext Json = McpJsonSerializerContext.Default;

    [Fact]
    public void JsonRpcRequest_round_trips_through_source_generated_context()
    {

        JsonElement id = JsonSerializer.SerializeToElement("abc", Json.String);

        JsonRpcRequest original = new()
        {
            Method = "tools/call",
            Id = id,
        };

        string wire = JsonSerializer.Serialize(original, Json.JsonRpcRequest);

        JsonRpcRequest? parsed = JsonSerializer.Deserialize(wire, Json.JsonRpcRequest);

        Assert.NotNull(parsed);

        Assert.Equal("tools/call", parsed!.Method);

        Assert.Equal("abc", parsed.Id!.Value.GetString());

        Assert.Equal("2.0", parsed.JsonRpc);

    }

    [Fact]
    public void JsonRpcResponse_round_trips_error_payload()
    {

        JsonElement id = JsonSerializer.SerializeToElement(7, Json.Int32);

        JsonRpcResponse original = new()
        {
            Id = id,
            Error = new JsonRpcError { Code = -32600, Message = "Invalid Request" },
        };

        string wire = JsonSerializer.Serialize(original, Json.JsonRpcResponse);

        JsonRpcResponse? parsed = JsonSerializer.Deserialize(wire, Json.JsonRpcResponse);

        Assert.NotNull(parsed);

        Assert.NotNull(parsed!.Error);

        Assert.Equal(-32600, parsed.Error!.Code);

    }

    [Fact]
    public void JsonRpcNotification_omits_id_on_wire()
    {

        JsonRpcNotification original = new()
        {
            Method = "notifications/cancelled",
        };

        string wire = JsonSerializer.Serialize(original, Json.JsonRpcNotification);

        using JsonDocument doc = JsonDocument.Parse(wire);

        Assert.False(doc.RootElement.TryGetProperty("id", out _));

        Assert.Equal("notifications/cancelled", doc.RootElement.GetProperty("method").GetString());

    }

}
