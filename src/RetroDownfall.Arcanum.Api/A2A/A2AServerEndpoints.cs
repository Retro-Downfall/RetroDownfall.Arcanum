using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;

using A2A;
using A2A.AspNetCore;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Api.A2A;

/// <summary>
/// Maps The Conclave's A2A (Agent-to-Agent) server surface: the JSON-RPC endpoints (<c>MapA2A</c>) and an
/// authenticated Agent Card ("Heraldry") describing Arcanum to external agents.
/// </summary>
/// <remarks>
/// Registered on <c>apiGroup</c> (not a standalone route), so every A2A route inherits
/// <see cref="ApiKeyEndpointFilter"/> and the active rate limiter exactly like every other <c>/api</c> route
/// — deliberately not the public, unauthenticated <c>/.well-known/agent-card.json</c> convention (see
/// <c>docs/Arcanum.DESIGN.md</c> &#167;5.7.1). Structural mapping happens once at startup from the config snapshot at boot;
/// <see cref="ArcanumA2AAgentHandler"/> itself still re-checks <c>IOptionsMonitor</c> per call, matching
/// every other Conclave gate.
/// <para>
/// The feature gates (<c>Arcanum:Features:Conclave</c> plus <c>Arcanum:Features:A2AServer</c>) are the only
/// opt-in. There is deliberately <em>no</em> edition gate: A2A is off by default, but an operator who turns it
/// on gets a working server on any edition rather than a silently dead surface (issue #12).
/// </para>
/// </remarks>
[ExcludeFromCodeCoverage] // Reason: thin HTTP mapping/serialization glue; behavior covered via ArcanumA2AAgentHandler and A2A integration tests.
internal static class A2AServerEndpoints
{

    private const string ApiGroupPrefix = "/api";

    /// <summary>
    /// Resolves the absolute path the A2A server is mounted at from the configured
    /// <c>Arcanum:Integrations:A2A:ServerPath</c>.
    /// </summary>
    /// <remarks>
    /// Every A2A route must live behind <see cref="ApiKeyEndpointFilter"/>, and <c>apiGroup</c> is rooted at
    /// <c>/api</c>. A configured path outside that prefix is therefore <em>mounted under</em> it rather than
    /// refused: an operator who asks for <c>/conclave/a2a</c> gets <c>/api/conclave/a2a</c> and a working
    /// server. Refusing to map (the previous behavior) left the operator with a silently dead surface and no
    /// diagnostic — the effective path is reported through <c>GET /api/meta</c> and <c>GET /api/health</c>
    /// so the mount point is never a guess (issue #12).
    /// </remarks>
    internal static string ResolveServerPath(string? configuredPath)
    {

        string trimmed = configuredPath?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {

            return ArcanumRuntimeDefaults.Conclave.A2A.ServerPath;

        }

        if (trimmed == ApiGroupPrefix
            || trimmed.StartsWith($"{ApiGroupPrefix}/", StringComparison.Ordinal))
        {

            return trimmed.TrimEnd('/') is { Length: > 0 } normalized ? normalized : ApiGroupPrefix;

        }

        string relative = trimmed.Trim('/');

        return relative.Length == 0 ? ApiGroupPrefix : $"{ApiGroupPrefix}/{relative}";

    }

    public static RouteGroupBuilder MapA2AServer(this RouteGroupBuilder apiGroup, ArcanumSettings startupSettings)
    {

        ConclaveA2ASettings a2a = startupSettings.ResolveA2A();

        if (!startupSettings.ResolveConclave().Enabled || !a2a.Enabled || !a2a.ServerEnabled)
        {

            return apiGroup;

        }

        string serverPath = ResolveServerPath(a2a.ServerPath);

        string relative = serverPath[ApiGroupPrefix.Length..];

        if (relative.Length == 0)
        {

            relative = "/";

        }

        A2AServer server = ((IEndpointRouteBuilder)apiGroup).ServiceProvider.GetRequiredService<A2AServer>();

        apiGroup.MapA2A(server, relative);

        // The A2A SDK's own JsonSerializerOptions already carries a source-generated resolver for AgentCard
        // (A2AJsonUtilities); extracting a concrete JsonTypeInfo<AgentCard> from it keeps this endpoint fully
        // AOT/trim-safe instead of using the reflection-fallback-capable Results.Json(T, JsonSerializerOptions) overload.
        JsonTypeInfo<AgentCard> agentCardTypeInfo = (JsonTypeInfo<AgentCard>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard));

        apiGroup.MapGet(
            $"{relative}/agent-card",
            (IOptionsMonitor<ArcanumSettings> settings, HttpContext ctx) =>
            {

                AgentCard card = BuildAgentCard(settings.CurrentValue, ctx, serverPath);

                return Results.Json(card, agentCardTypeInfo);

            })
        .WithName("GetArcanumAgentCard");

        return apiGroup;

    }

    internal static AgentCard BuildAgentCard(ArcanumSettings settings, HttpContext context, string serverPath)
    {

        ConclaveA2ASettings a2a = settings.ResolveA2A();

        string interfaceUrl = $"{context.Request.Scheme}://{context.Request.Host}{serverPath}";

        string version = typeof(A2AServerEndpoints).Assembly.GetName().Version?.ToString() ?? "0.1.0";

        return new AgentCard
        {
            Name = string.IsNullOrWhiteSpace(a2a.AgentCardName) ? "Arcanum" : a2a.AgentCardName,
            Description = string.IsNullOrWhiteSpace(a2a.AgentCardDescription)
                ? "Arcanum: an autonomous Apprentice orchestration engine, exposed here via the A2A protocol. Sending a message spawns a headless Apprentice that plans and executes the delegated goal inside a sandboxed workspace."
                : a2a.AgentCardDescription,
            Version = version,
            SupportedInterfaces =
            [
                new AgentInterface
                {
                    Url = interfaceUrl,
                    ProtocolBinding = "JSONRPC",
                    ProtocolVersion = "1.0",
                },
            ],
            Capabilities = new AgentCapabilities
            {
                Streaming = true,

                // Advertised only when the surface is genuinely enabled: a card that promises push
                // notifications an operator never turned on is a peer waiting forever (issue #67).
                PushNotifications = a2a.PushNotificationsEnabled,
            },
            // Operator-declared when configured, otherwise the single historical skill and text/plain in
            // and out — so a default card stays byte-identical to the pre-#63 one and existing peers are
            // unaffected.
            Skills = [.. A2AAgentCardPolicy.ResolveSkills(a2a)],
            DefaultInputModes = [.. A2AAgentCardPolicy.ResolveInputModes(a2a)],
            DefaultOutputModes = [.. A2AAgentCardPolicy.ResolveOutputModes(a2a)],
            SecuritySchemes = new Dictionary<string, SecurityScheme>
            {
                ["arcanumApiKey"] = new SecurityScheme
                {
                    ApiKeySecurityScheme = new ApiKeySecurityScheme
                    {
                        Name = ArcanumApiHeaders.ApiKey,
                        Location = "header",
                    },
                },
            },
        };

    }

}
