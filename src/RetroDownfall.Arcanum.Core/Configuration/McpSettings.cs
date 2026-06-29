namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// MCP host client and transport security limits.
/// </summary>
public sealed record McpSettings
{

    public int RequestTimeoutSeconds { get; init; } = 60;

    public int MaxPaginationPages { get; init; } = 32;

    public bool BootstrapBlocksStartup { get; init; } = true;

    public int MaxServers { get; init; } = 50;

    public int MaxToolsPerServer { get; init; } = 256;

    public int MaxToolsPerListPage { get; init; } = 64;

    public int MaxToolsTotalBytes { get; init; } = 1_048_576;

    public int MaxJsonRpcLineBytes { get; init; } = 2_228_224;

    /// <summary>
    /// Timeout (seconds) for the named <c>HttpClient("McpHttp")</c> used by the Streamable HTTP
    /// transport (headers phase; the per-request JSON-RPC timeout governs streamed bodies).
    /// Default 120; clamp 10&#8211;600.
    /// </summary>
    public int HttpRequestTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Hosts permitted over plaintext <c>http</c> for Streamable HTTP MCP servers (e.g.
    /// <c>["localhost"]</c> for a trusted dev gateway). Default empty: remote HTTP servers must use
    /// <c>https</c>. SSRF protection (loopback / private / link-local blocking via
    /// <c>OutboundUrlGuard</c>) still applies regardless of this list.
    /// </summary>
    public string[] AllowedHttpHosts { get; init; } = [];

}
