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
using RetroDownfall.Arcanum.Core.Environment;

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
/// </remarks>
[ExcludeFromCodeCoverage] // Reason: thin HTTP mapping/serialization glue; behavior covered via ArcanumA2AAgentHandler and A2A integration tests.
internal static class A2AServerEndpoints
{

    private const string ApiGroupPrefix = "/api";

    public static RouteGroupBuilder MapA2AServer(this RouteGroupBuilder apiGroup, ArcanumSettings startupSettings)
    {

        ArcanumEdition edition = ArcanumEnvironment.ResolveEdition(startupSettings.Edition);

        if (edition != ArcanumEdition.Development)
        {
            return apiGroup;
        }

        ConclaveA2ASettings a2a = startupSettings.ResolveA2A();

        if (!startupSettings.ResolveConclave().Enabled || !a2a.Enabled || !a2a.ServerEnabled)
        {

            return apiGroup;

        }

        string serverPath = string.IsNullOrWhiteSpace(a2a.ServerPath) ? "/api/conclave/a2a" : a2a.ServerPath.Trim();

        if (!serverPath.StartsWith(ApiGroupPrefix, StringComparison.Ordinal))
        {

            // Every A2A route must live behind ApiKeyEndpointFilter (constraint: security boundaries
            // apply). apiGroup is rooted at "/api", so a ServerPath outside that prefix cannot be mapped
            // here without either duplicating the filter or risking an unauthenticated route; refuse to
            // map rather than silently expose the surface unauthenticated.
            return apiGroup;

        }

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
                PushNotifications = false,
            },
            Skills =
            [
                new AgentSkill
                {
                    Id = "apprentice-goal-execution",
                    Name = "Autonomous goal execution",
                    Description =
                        "Delegates a natural-language goal to an Arcanum Apprentice: an autonomous sub-agent with file, command, and tool access inside a sandboxed workspace.",
                    InputModes = ["text/plain"],
                    OutputModes = ["text/plain"],
                },
            ],
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
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
