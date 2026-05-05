using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Root shape of a standard <c>mcp.json</c> configuration file.
/// </summary>
public sealed record McpConfig
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig>? McpServers { get; init; }
}

/// <summary>
/// One stdio MCP server entry under <c>mcpServers</c>.
/// </summary>
public sealed record McpServerConfig
{
    public string? Command { get; init; }

    public string[]? Args { get; init; }

    public Dictionary<string, string>? Env { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpConfig))]
[JsonSerializable(typeof(McpServerConfig))]
[JsonSerializable(typeof(Dictionary<string, McpServerConfig>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
public partial class McpConfigJsonSerializerContext : JsonSerializerContext;
