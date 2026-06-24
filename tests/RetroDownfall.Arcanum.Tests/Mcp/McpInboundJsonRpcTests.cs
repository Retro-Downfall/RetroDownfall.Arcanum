using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpInboundJsonRpcTests
{

    private static readonly McpJsonSerializerContext Json = McpJsonSerializerContext.Default;

    private const int DefaultMaxJsonRpcLineBytes = 2_097_152;

    [Fact]
    public void ParseInbound_parses_response()
    {

        string line = "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"ok\":true}}";

        McpInboundEnvelope envelope = McpInboundJsonRpc.ParseInbound(line, Json, DefaultMaxJsonRpcLineBytes);

        Assert.Equal(McpInboundKind.Response, envelope.Kind);

        Assert.NotNull(envelope.Response);

        Assert.Equal("1", envelope.Response!.Id.GetRawText().Trim('"'));

    }

    [Fact]
    public void ParseInbound_parses_notification()
    {

        string line = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";

        McpInboundEnvelope envelope = McpInboundJsonRpc.ParseInbound(line, Json, DefaultMaxJsonRpcLineBytes);

        Assert.Equal(McpInboundKind.Notification, envelope.Kind);

        Assert.Equal("notifications/initialized", envelope.Notification!.Method);

    }

    [Fact]
    public void ParseInbound_parses_request()
    {

        string line = "{\"jsonrpc\":\"2.0\",\"method\":\"tools/list\",\"id\":\"2\"}";

        McpInboundEnvelope envelope = McpInboundJsonRpc.ParseInbound(line, Json, DefaultMaxJsonRpcLineBytes);

        Assert.Equal(McpInboundKind.Request, envelope.Kind);

        Assert.Equal("tools/list", envelope.Request!.Method);

    }

    [Fact]
    public void ParseInbound_rejects_response_without_id()
    {

        string line = "{\"jsonrpc\":\"2.0\",\"result\":{}}";

        Assert.Throws<JsonException>(() => McpInboundJsonRpc.ParseInbound(line, Json, DefaultMaxJsonRpcLineBytes));

    }

    [Fact]
    public void ParseInbound_rejects_unrecognized_shape()
    {

        string line = "{\"jsonrpc\":\"2.0\"}";

        Assert.Throws<JsonException>(() => McpInboundJsonRpc.ParseInbound(line, Json, DefaultMaxJsonRpcLineBytes));

    }

    [Fact]
    public void ParseInboundCore_rejects_non_object_root()
    {

        using JsonDocument doc = JsonDocument.Parse("[]");

        Assert.Throws<JsonException>(() => McpInboundJsonRpc.ParseInboundCore(doc.RootElement, Json));

    }

}
