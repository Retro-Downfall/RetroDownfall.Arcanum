using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed partial class McpConnectionManager
{

    /// <summary>
    /// Decides whether to strip the inherited host environment before spawning an MCP server
    /// subprocess. Secure default: strip for ALL servers — global (modeled as
    /// <c>ScopeWorkingDirectory == null</c>) and workspace-scoped alike — so secrets such as the
    /// <c>ARCANUM_*</c> provider API keys never leak into child processes. A per-server opt-in to
    /// inherit the host environment is a deliberate follow-up; <see cref="McpServerConfig"/> has no
    /// such field yet.
    /// </summary>
    internal static bool ShouldStripUserEnvironment(McpServerConfig cfg)
    {

        ArgumentNullException.ThrowIfNull(cfg);

        return true;

    }

    // W-MCP-HTTP: an stdio server may opt specific host variables back in via `inheritEnv` (e.g.
    // PATH/HOME for npx). Names are matched case-insensitively so the deny-list bypass works on
    // either casing; the host lookup uses the operator-provided name verbatim. Returns null when
    // nothing is opted in so the secure strip-everything default is preserved.
    internal static IReadOnlySet<string>? BuildInheritEnvironmentAllowlist(string[]? inheritEnv)
    {

        if (inheritEnv is not { Length: > 0 })
        {

            return null;

        }

        HashSet<string> allowlist = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in inheritEnv)
        {

            if (!string.IsNullOrWhiteSpace(name))
            {

                allowlist.Add(name.Trim());

            }

        }

        return allowlist.Count == 0 ? null : allowlist;

    }

    // W-MCP-HTTP: an explicit `type` wins; otherwise a configured `url` implies the Streamable
    // HTTP transport (2026-07-28) and a bare command implies stdio. An explicit `type: "sse"`
    // selects the legacy SSE transport, which remains unsupported. Unknown `type` values fall
    // back to URL inference so a hand-edited config still resolves to a usable transport.
    internal static McpServerTransport InferTransport(McpServerConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        if (!string.IsNullOrWhiteSpace(cfg.Type))
        {
            if (string.Equals(cfg.Type, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                return McpServerTransport.Stdio;
            }

            if (string.Equals(cfg.Type, "http", StringComparison.OrdinalIgnoreCase))
            {
                return McpServerTransport.Http;
            }

            if (string.Equals(cfg.Type, "sse", StringComparison.OrdinalIgnoreCase))
            {
                return McpServerTransport.Sse;
            }
        }

        if (!string.IsNullOrWhiteSpace(cfg.Url))
        {
            return McpServerTransport.Http;
        }

        return McpServerTransport.Stdio;
    }

    private TimeSpan GetClampedMcpInitializationTimeout()
    {
        return TimeSpan.FromSeconds(
            ArcanumSettingClamps.McpInitializationTimeoutSeconds(
                ArcanumRuntimeDefaults.Mcp.InitializationTimeoutSeconds));

    }

    private int GetClampedMcpMaxToolsTotalBytes()
    {

        return ArcanumSettingClamps.McpMaxToolsTotalBytes(
            ArcanumRuntimeDefaults.Mcp.MaxToolsTotalBytes);

    }

    private int GetClampedMcpMaxJsonRpcLineBytes()
    {

        return ArcanumSettingClamps.McpMaxJsonRpcLineBytes(
            ArcanumRuntimeDefaults.Mcp.MaxJsonRpcLineBytes);

    }

    private readonly McpElicitationBridge _elicitationBridge = new(humanPromptRegistry);

    /// <summary>
    /// Builds the shared SDK <see cref="McpClientOptions"/> for every transport: client identity and the
    /// standard MCP elicitation handler, which bridges a server's <c>elicitation/create</c> request to the
    /// same <see cref="IHumanPromptRegistry"/> channel the in-process <c>ask_human</c> tool uses. Unlike
    /// the pre-SDK bespoke "multi-round tool response" extension (HTTP-only), this applies uniformly to
    /// every transport (stdio, Streamable HTTP, in-process).
    /// </summary>
    private McpClientOptions BuildMcpClientOptions(McpElicitationSink elicitationSink)
    {
        return new McpClientOptions
        {
            ClientInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = typeof(McpConnectionManager).Assembly.GetName().Name ?? "RetroDownfall.Arcanum.Infrastructure",
                Version = typeof(McpConnectionManager).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            },
            InitializationTimeout = GetClampedMcpInitializationTimeout(),
            Handlers = _elicitationBridge.CreateClientHandlers(elicitationSink),
        };
    }

    private SdkMcpClientWrapper CreateSdkMcpClientWrapper(IClientTransport clientTransport)
    {
        // One sink per connection: the handler built into the options and the wrapper that enters
        // each tool call share it, which is how a server-initiated request finds its turn.
        McpElicitationSink elicitationSink = new();

        return new SdkMcpClientWrapper(
            clientTransport,
            BuildMcpClientOptions(elicitationSink),
            GetClampedMcpInitializationTimeout(),
            GetClampedToolOutputCapBytes(),
            GetClampedMcpMaxToolsTotalBytes(),
            elicitationSink);
    }

    private SdkMcpClientWrapper CreateHttpMcpClient(Uri endpoint)
    {

        HttpClient httpClient = httpClientFactory.CreateClient(McpHttpClientName);

        HttpClientTransport transport = new(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);

        return CreateSdkMcpClientWrapper(transport);

    }

    // W-MCP-HTTP: validates a Streamable HTTP endpoint before connecting. The URL must be an
    // absolute http/https URI; plaintext http is refused unless the host is in
    // Arcanum:Integrations:Mcp:AllowedHttpHosts; and the SSRF policy (loopback / private / link-local blocking
    // with DNS-rebind pinning) is enforced up front via OutboundUrlGuard and again at connect time
    // by the named client's egress handler.
    private async Task<Result<Uri>> ResolveValidatedHttpEndpointAsync(McpServerConfig cfg, CancellationToken cancellationToken)
    {

        string? url = cfg.Url;

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? endpoint))
        {

            return Result<Uri>.Failure(new Error("Mcp.InvalidUrl", "MCP HTTP server requires an absolute http or https url."));

        }

        if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
        {

            return Result<Uri>.Failure(new Error("Mcp.InvalidUrl", "MCP HTTP server url must use the http or https scheme."));

        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !IsHttpHostAllowed(endpoint.Host))
        {

            return Result<Uri>.Failure(new Error(
                "Mcp.InsecureUrl",
                $"Plaintext http MCP server '{endpoint.Host}' requires the host in Arcanum:Integrations:Mcp:AllowedHttpHosts; otherwise use https."));

        }

        Result outbound = await OutboundUrlGuard.ValidateUntrustedUrlAsync(url, cancellationToken).ConfigureAwait(false);

        if (outbound.IsFailure)
        {

            return Result<Uri>.Failure(new Error("Mcp.BlockedUrl", outbound.Error.Message));

        }

        return Result<Uri>.Success(endpoint);

    }

    private bool IsHttpHostAllowed(string host)
    {

        string[] allowed =
            settings.CurrentValue.Integrations?.Mcp?.AllowedHttpHosts ?? [];

        foreach (string candidate in allowed)
        {

            if (!string.IsNullOrWhiteSpace(candidate)
                && string.Equals(host, candidate.Trim(), StringComparison.OrdinalIgnoreCase))
            {

                return true;

            }

        }

        return false;

    }

}
