using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

/// <summary>
/// JSON-RPC 2.0 error object (<c>error</c> on a response). Nested object does not include <c>jsonrpc</c>.
/// </summary>
public sealed record JsonRpcError
{
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}

/// <summary>
/// JSON-RPC 2.0 request (outbound or server-originated). <see cref="Id"/> omitted for notifications — use <see cref="JsonRpcNotification"/> instead.
/// </summary>
public sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }
}

/// <summary>
/// JSON-RPC 2.0 response. Exactly one of <see cref="Result"/> or <see cref="Error"/> should be present on the wire; both may be modeled as optional here and validated by higher layers.
/// </summary>
public sealed record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; init; }

    [JsonPropertyName("id")]
    public required JsonElement Id { get; init; }
}

/// <summary>
/// JSON-RPC 2.0 notification — a request object without an <c>id</c> member.
/// </summary>
public sealed record JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(McpInitializeParams))]
[JsonSerializable(typeof(McpClientCapabilities))]
[JsonSerializable(typeof(McpClientInfo))]
[JsonSerializable(typeof(McpToolsListParams))]
[JsonSerializable(typeof(McpToolsCallParams))]
[JsonSerializable(typeof(McpEmptyJsonObject))]
[JsonSerializable(typeof(McpEmptyJsonObject[]))]
[JsonSerializable(typeof(McpServerInfoWire))]
[JsonSerializable(typeof(McpServerCapabilitiesWire))]
[JsonSerializable(typeof(McpInitializeServerResult))]
[JsonSerializable(typeof(McpToolsListResultWire))]
[JsonSerializable(typeof(McpToolContentTextWire))]
[JsonSerializable(typeof(McpToolContentTextWire[]))]
[JsonSerializable(typeof(McpToolsCallResultWire))]
[JsonSerializable(typeof(McpToolDefinitionWire))]
[JsonSerializable(typeof(McpToolDefinitionWire[]))]
[JsonSerializable(typeof(ReadFileChunkParams))]
[JsonSerializable(typeof(ReplaceTextBlockParams))]
[JsonSerializable(typeof(WriteFileParams))]
[JsonSerializable(typeof(ListDirectoryParams))]
[JsonSerializable(typeof(ExecuteCommandParams))]
[JsonSerializable(typeof(AskHumanParams))]
[JsonSerializable(typeof(ReadLoreParams))]
[JsonSerializable(typeof(ScribeLoreParams))]
[JsonSerializable(typeof(DeleteLoreParams))]
[JsonSerializable(typeof(SearchArchivesParams))]
[JsonSerializable(typeof(ReadSagaParams))]
[JsonSerializable(typeof(AdjustInitiativeArgs))]
[JsonSerializable(typeof(UseCommlinkParams))]
[JsonSerializable(typeof(PetitionDungeonMasterParams))]
[JsonSerializable(typeof(CastSendingParams))]
[JsonSerializable(typeof(CastSendingResultWire))]
[JsonSerializable(typeof(McpCancelledParams))]
[JsonSerializable(typeof(McpInputRequiredResult))]
[JsonSerializable(typeof(McpInputRequest))]
[JsonSerializable(typeof(McpInputRequest[]))]
[JsonSerializable(typeof(McpInputResponse))]
[JsonSerializable(typeof(McpInputResponse[]))]
public partial class McpJsonSerializerContext : JsonSerializerContext;
