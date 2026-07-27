namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// MCP runtime projection. The plaintext-host allowlist comes from
/// <c>Arcanum:Integrations:Mcp</c>; transport, pagination, frame, and schema limits are code-owned.
/// </summary>
public sealed record McpSettings
{

    public int RequestTimeoutSeconds { get; set; } = 60;

    public int MaxPaginationPages { get; set; } = 32;

    public bool BootstrapBlocksStartup { get; set; } = true;

    public int MaxServers { get; set; } = 50;

    public int MaxToolsPerServer { get; set; } = 256;

    public int MaxToolsPerListPage { get; set; } = 64;

    public int MaxToolsTotalBytes { get; set; } = 1_048_576;

    public int MaxJsonRpcLineBytes { get; set; } = 2_228_224;

    /// <summary>
    /// Timeout (seconds) for the named <c>HttpClient("McpHttp")</c> used by the Streamable HTTP
    /// transport (headers phase; the per-request JSON-RPC timeout governs streamed bodies).
    /// Default 120; clamp 10&#8211;600.
    /// </summary>
    public int HttpRequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Hosts permitted over plaintext <c>http</c> for Streamable HTTP MCP servers (e.g.
    /// <c>["localhost"]</c> for a trusted dev gateway). Default empty: remote HTTP servers must use
    /// <c>https</c>. SSRF protection (loopback / private / link-local blocking via
    /// <c>OutboundUrlGuard</c>) still applies regardless of this list.
    /// </summary>
    public string[] AllowedHttpHosts { get; set; } = [];

}
