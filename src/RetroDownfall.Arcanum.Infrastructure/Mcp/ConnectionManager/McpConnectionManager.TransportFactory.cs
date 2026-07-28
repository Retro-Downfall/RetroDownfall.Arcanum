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

    private int GetClampedExecuteCommandTimeoutSeconds()
    {
        int executeSeconds = Math.Clamp(settings.CurrentValue.Intelligence.ExecuteCommandTimeoutSeconds, 1, 600);

        int requestSeconds = ArcanumSettingClamps.McpRequestTimeoutSeconds(
            settings.CurrentValue.Mcp.RequestTimeoutSeconds);

        return Math.Min(executeSeconds, requestSeconds);
    }

    private TimeSpan GetClampedMcpRequestTimeout()
    {
        return TimeSpan.FromSeconds(
            ArcanumSettingClamps.McpRequestTimeoutSeconds(settings.CurrentValue.Mcp.RequestTimeoutSeconds));
    }

    private int GetClampedMcpMaxPaginationPages()
    {
        return ArcanumSettingClamps.McpMaxPaginationPages(settings.CurrentValue.Mcp.MaxPaginationPages);
    }

    private int GetClampedMcpMaxServers()
    {

        return ArcanumSettingClamps.McpMaxServers(settings.CurrentValue.Mcp.MaxServers);

    }

    private int GetClampedMcpMaxToolsPerServer()
    {

        return ArcanumSettingClamps.McpMaxToolsPerServer(settings.CurrentValue.Mcp.MaxToolsPerServer);

    }

    private int GetClampedMcpMaxToolsPerListPage()
    {

        return ArcanumSettingClamps.McpMaxToolsPerListPage(settings.CurrentValue.Mcp.MaxToolsPerListPage);

    }

    private int GetClampedMcpMaxToolsTotalBytes()
    {

        return ArcanumSettingClamps.McpMaxToolsTotalBytes(settings.CurrentValue.Mcp.MaxToolsTotalBytes);

    }

    private int GetClampedMcpMaxJsonRpcLineBytes()
    {

        return ArcanumSettingClamps.McpMaxJsonRpcLineBytes(settings.CurrentValue.Mcp.MaxJsonRpcLineBytes);

    }

    /// <summary>
    /// Builds the shared SDK <see cref="McpClientOptions"/> for every transport: client identity and the
    /// standard MCP elicitation handler, which bridges a server's <c>elicitation/create</c> request to the
    /// same <see cref="IHumanPromptRegistry"/> channel the in-process <c>ask_human</c> tool uses. Unlike
    /// the pre-SDK bespoke "multi-round tool response" extension (HTTP-only), this applies uniformly to
    /// every transport (stdio, Streamable HTTP, in-process).
    /// </summary>
    private McpClientOptions BuildMcpClientOptions()
    {
        return new McpClientOptions
        {
            ClientInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = typeof(McpConnectionManager).Assembly.GetName().Name ?? "RetroDownfall.Arcanum.Infrastructure",
                Version = typeof(McpConnectionManager).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            },
            InitializationTimeout = GetClampedMcpRequestTimeout(),
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = HandleElicitationAsync,
            },
        };
    }

    /// <summary>
    /// Bridges MCP <c>elicitation/create</c> to the same HITL channel as <c>ask_human</c>.
    /// Reads <see cref="HumanPromptLiveEmitterAmbient"/> (set only on attended streaming turns).
    /// Assumes the SDK invokes this handler on the tool-call <see cref="ExecutionContext"/> so
    /// <see cref="AsyncLocal{T}"/> flows; if a future SDK marshals off-context, ambient will be
    /// null and we decline (never an invisible waiter).
    /// </summary>
    private async ValueTask<ModelContextProtocol.Protocol.ElicitResult> HandleElicitationAsync(
        ModelContextProtocol.Protocol.ElicitRequestParams? request,
        CancellationToken cancellationToken)
    {
        IHumanPromptLiveEmitter? emitter = HumanPromptLiveEmitterAmbient.Current;
        if (emitter is null)
        {
            return DeclineElicitation(
                "Elicitation requires an attended streaming turn with a live human-response channel.");
        }

        if (!TryResolveTextCompatibleContentKey(request, out string contentKey, out string? schemaDeclineReason))
        {
            return DeclineElicitation(schemaDeclineReason ?? "Unsupported elicitation schema.");
        }

        IHumanPromptReservation? reservation = humanPromptRegistry.TryCreateReservation();
        if (reservation is null)
        {
            return DeclineElicitation(HumanPromptCapExceededException.DefaultMessage);
        }

        string callId = string.IsNullOrWhiteSpace(request?.ElicitationId)
            ? Guid.NewGuid().ToString("N")
            : request.ElicitationId!;
        string question = string.IsNullOrWhiteSpace(request?.Message)
            ? "The tool is requesting operator input."
            : request.Message.Trim();

        try
        {
            await EmitAskHumanCompatibleToolCallAsync(
                    emitter,
                    callId,
                    question,
                    reservation.PromptId,
                    cancellationToken)
                .ConfigureAwait(false);

            string value = await reservation
                .WaitAsync(HumanPromptElicitationHardCeiling, cancellationToken)
                .ConfigureAwait(false);

            await EmitAskHumanCompatibleToolResultAsync(
                    emitter,
                    callId,
                    value,
                    isError: false,
                    cancellationToken)
                .ConfigureAwait(false);

            return new ModelContextProtocol.Protocol.ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [contentKey] = JsonSerializer.SerializeToElement(value, McpJsonSerializerContext.Default.String),
                },
            };
        }
        catch (HumanPromptTimeoutException)
        {
            await EmitAskHumanCompatibleToolResultAsync(
                    emitter,
                    callId,
                    HumanPromptTimeoutException.DefaultMessage,
                    isError: true,
                    cancellationToken)
                .ConfigureAwait(false);

            return DeclineElicitation(HumanPromptTimeoutException.DefaultMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            await reservation.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Mirrors HumanPromptRegistry.HardCeiling without taking a dependency on the Api assembly.
    private static readonly TimeSpan HumanPromptElicitationHardCeiling = TimeSpan.FromMinutes(30);

    private static ModelContextProtocol.Protocol.ElicitResult DeclineElicitation(string reason) =>
        new()
        {
            Action = "decline",
            Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["reason"] = JsonSerializer.SerializeToElement(reason, McpJsonSerializerContext.Default.String),
            },
        };

    /// <summary>
    /// Text-only UI can satisfy free-text form elicitation (no schema, or a single string field).
    /// URL mode and multi-field / non-string schemas are declined immediately.
    /// </summary>
    private static bool TryResolveTextCompatibleContentKey(
        ModelContextProtocol.Protocol.ElicitRequestParams? request,
        out string contentKey,
        out string? declineReason)
    {
        contentKey = "value";
        declineReason = null;

        if (request is null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.Mode)
            && !string.Equals(request.Mode, "form", StringComparison.OrdinalIgnoreCase))
        {
            declineReason =
                $"Elicitation mode '{request.Mode}' is not supported by the text-only operator UI.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            declineReason = "URL-mode elicitation is not supported by the text-only operator UI.";
            return false;
        }

        ModelContextProtocol.Protocol.ElicitRequestParams.RequestSchema? schema = request.RequestedSchema;
        if (schema?.Properties is null || schema.Properties.Count == 0)
        {
            return true;
        }

        if (schema.Properties.Count != 1)
        {
            declineReason =
                "Structured multi-field elicitation schemas are not supported by the text-only operator UI.";
            return false;
        }

        KeyValuePair<string, ModelContextProtocol.Protocol.ElicitRequestParams.PrimitiveSchemaDefinition> only =
            schema.Properties.First();

        if (only.Value is not ModelContextProtocol.Protocol.ElicitRequestParams.StringSchema)
        {
            declineReason =
                "Only free-text (string) elicitation fields are supported by the text-only operator UI.";
            return false;
        }

        contentKey = only.Key;
        return true;
    }

    private static async Task EmitAskHumanCompatibleToolCallAsync(
        IHumanPromptLiveEmitter emitter,
        string callId,
        string question,
        string promptId,
        CancellationToken cancellationToken)
    {
        AskHumanParams args = new() { Question = question, PromptId = promptId };
        string argsJson = JsonSerializer.Serialize(args, McpJsonSerializerContext.Default.AskHumanParams);

        await emitter
            .EmitAsync(
                new IntelligenceEvent(
                    IntelligenceEventType.ToolCall,
                    "ask_human",
                    $"ask_human: {argsJson}",
                    null,
                    new IntelligenceToolCallEvent(callId, "ask_human", argsJson, Index: 0)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EmitAskHumanCompatibleToolResultAsync(
        IHumanPromptLiveEmitter emitter,
        string callId,
        string text,
        bool isError,
        CancellationToken cancellationToken)
    {
        await emitter
            .EmitAsync(
                new IntelligenceEvent(
                    isError ? IntelligenceEventType.ToolError : IntelligenceEventType.ToolResult,
                    "ask_human",
                    text,
                    null,
                    new IntelligenceToolCallEvent(callId, "ask_human", text, Index: 0)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private SdkMcpClientWrapper CreateSdkMcpClientWrapper(IClientTransport clientTransport)
    {
        return new SdkMcpClientWrapper(
            clientTransport,
            BuildMcpClientOptions(),
            GetClampedMcpRequestTimeout(),
            GetClampedMcpMaxPaginationPages(),
            GetClampedToolOutputCapBytes(),
            GetClampedMcpMaxToolsPerServer(),
            GetClampedMcpMaxToolsPerListPage(),
            GetClampedMcpMaxToolsTotalBytes());
    }

    private TimeSpan GetClampedMcpHttpRequestTimeout()
    {

        return TimeSpan.FromSeconds(
            ArcanumSettingClamps.McpHttpRequestTimeoutSeconds(settings.CurrentValue.Mcp.HttpRequestTimeoutSeconds));

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
    // Arcanum:Mcp:AllowedHttpHosts; and the SSRF policy (loopback / private / link-local blocking
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
                $"Plaintext http MCP server '{endpoint.Host}' requires the host in Arcanum:Mcp:AllowedHttpHosts; otherwise use https."));

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

        string[] allowed = settings.CurrentValue.Mcp.AllowedHttpHosts ?? [];

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
