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
/// One MCP server entry under <c>mcpServers</c> (stdio subprocess or Streamable HTTP endpoint).
/// </summary>
public sealed record McpServerConfig
{
    public string? Command { get; init; }

    public string[]? Args { get; init; }

    public Dictionary<string, string>? Env { get; init; }

    public bool AlwaysOn { get; init; } = true;

    public string? Url { get; init; }

    public string? Cwd { get; init; }

    /// <summary>
    /// Explicit transport selector: <c>"stdio"</c> or <c>"http"</c> (Streamable HTTP, 2026-07-28).
    /// When null, the transport is inferred from <see cref="Url"/> (a URL implies <c>http</c>,
    /// otherwise <c>stdio</c>). An explicit <c>"sse"</c> selects the legacy SSE transport, which
    /// remains unsupported.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Names of host-process environment variables an stdio server is permitted to inherit even
    /// though the host environment is otherwise stripped (e.g. <c>["PATH", "HOME"]</c> so an
    /// <c>npx</c>-launched server can locate Node.js and the npm cache). Listed names bypass the
    /// <see cref="McpSecurityLimits.IsBlockedEnvironmentVariable"/> deny-list; ignored for HTTP servers.
    /// </summary>
    public string[]? InheritEnv { get; init; }
}

/// <summary>
/// On-disk trusted workspace-local <c>mcp.json</c> approvals (workspace root path → SHA-256 hex of file bytes).
/// </summary>
public sealed class TrustedMcpWorkspaceDocument
{

    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);

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
[JsonSerializable(typeof(TrustedMcpWorkspaceDocument))]
public partial class McpConfigJsonSerializerContext : JsonSerializerContext;
