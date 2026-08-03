namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// MCP runtime projection. The plaintext-host allowlist comes from
/// <c>Arcanum:Integrations:Mcp</c>; transport, pagination, frame, and schema limits are code-owned.
/// </summary>
public sealed record McpSettings
{

    public int InitializationTimeoutSeconds { get; set; } = 60;

    public bool BootstrapBlocksStartup { get; set; } = true;

    public int MaxToolsTotalBytes { get; set; } = 1_048_576;

    public int MaxJsonRpcLineBytes { get; set; } = 2_228_224;

    /// <summary>
    /// Connection timeout (seconds) for the named <c>HttpClient("McpHttp")</c>. Established MCP
    /// requests have no Arcanum-owned total invocation deadline.
    /// </summary>
    public int HttpConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Hosts permitted over plaintext <c>http</c> for Streamable HTTP MCP servers (e.g.
    /// <c>["localhost"]</c> for a trusted dev gateway). Default empty: remote HTTP servers must use
    /// <c>https</c>. SSRF protection (loopback / private / link-local blocking via
    /// <c>OutboundUrlGuard</c>) still applies regardless of this list.
    /// </summary>
    public string[] AllowedHttpHosts { get; set; } = [];

}
