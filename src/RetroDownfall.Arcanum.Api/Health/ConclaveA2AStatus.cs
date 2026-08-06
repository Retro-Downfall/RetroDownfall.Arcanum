using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Api.Health;

/// <summary>
/// Lifecycle state of The Conclave's A2A surface, as reported by <c>GET /api/health</c>,
/// <c>GET /api/meta</c>, and <c>arcanum conclave status</c>.
/// </summary>
public enum ConclaveA2AState
{

    /// <summary>No Conclave or A2A opt-in, and no A2A identity/allowlist configuration present.</summary>
    Disabled = 0,

    /// <summary>
    /// A2A identity, allowlist, or endpoint settings are present, but no feature gate turns them on. The
    /// operator did the configuration work and is one flag away from a working surface.
    /// </summary>
    Configured = 1,

    /// <summary>A surface is enabled but a prerequisite is missing, so requests will fail.</summary>
    Degraded = 2,

    /// <summary>Every enabled surface has what it needs to serve traffic.</summary>
    Healthy = 3,

}

/// <summary>
/// Shared Conclave / A2A status for health, meta, and the CLI — the single projection of "is A2A off,
/// merely configured, broken, or working?".
/// </summary>
/// <remarks>
/// Deliberately reports the <em>count</em> of allowed remote agents rather than the entries: allowlist
/// values can name private partner endpoints, and this projection is returned to every authenticated
/// client (docs/Arcanum.DESIGN.md &#167;5.7.1).
/// </remarks>
public sealed record ConclaveA2AStatus(
    ConclaveA2AState State,
    bool ConclaveEnabled,
    bool ServerEnabled,
    bool ClientEnabled,
    string? ServerPath,
    string? AgentCardPath,
    int AllowedRemoteAgentCount,
    string Detail)
{

    /// <summary>Stable lowercase wire name for <paramref name="state"/>.</summary>
    public static string WireName(ConclaveA2AState state) => state switch
    {
        ConclaveA2AState.Disabled => "disabled",
        ConclaveA2AState.Configured => "configured",
        ConclaveA2AState.Degraded => "degraded",
        ConclaveA2AState.Healthy => "healthy",
        _ => "disabled",
    };

    /// <summary>Stable lowercase wire name for this status.</summary>
    public string StateName => WireName(State);

    /// <summary>
    /// Maps a Conclave state onto readiness. Only <see cref="ConclaveA2AState.Degraded"/> — an enabled
    /// surface that cannot serve traffic — is a health problem; off and merely-configured are deliberate
    /// operator states and must not add standing noise to <c>GET /api/health</c>.
    /// </summary>
    public static HealthStatus ToHealthStatus(ConclaveA2AState state) =>
        state == ConclaveA2AState.Degraded ? HealthStatus.Degraded : HealthStatus.Healthy;

    /// <summary>Builds the <c>Conclave</c> component reported by <c>GET /api/health</c>.</summary>
    public static HealthComponentDto BuildHealthComponent(ArcanumSettings settings)
    {

        ConclaveA2AStatus status = Resolve(settings);

        return new HealthComponentDto(
            "Conclave",
            ToHealthStatus(status.State),
            $"state={status.StateName}; server={status.ServerEnabled}; client={status.ClientEnabled}. {status.Detail}");

    }

    public static ConclaveA2AStatus Resolve(ArcanumSettings settings)
    {

        ConclaveA2ASettings a2a = settings.ResolveA2A();

        bool conclaveEnabled = settings.ResolveConclave().Enabled;

        int allowlistCount = (a2a.AllowedRemoteAgents ?? [])
            .Count(static entry => !string.IsNullOrWhiteSpace(entry));

        string? serverPath = a2a.ServerEnabled
            ? A2AServerEndpoints.ResolveServerPath(a2a.ServerPath)
            : null;

        string? agentCardPath = serverPath is null ? null : $"{serverPath}/agent-card";

        if (!conclaveEnabled)
        {

            ConclaveA2AState idleState = HasA2AConfiguration(a2a)
                ? ConclaveA2AState.Configured
                : ConclaveA2AState.Disabled;

            string idleDetail = idleState == ConclaveA2AState.Configured
                ? "A2A settings are present but no surface is enabled. Set Arcanum:Features:A2AServer to accept "
                    + "inbound Sendings, Arcanum:Features:A2AClient to dispatch them, or Arcanum:Features:Conclave "
                    + "for local cast_sending delegation only."
                : "The Conclave is disabled. Set Arcanum:Features:Conclave for local cast_sending delegation, or "
                    + "Arcanum:Features:A2AServer / Arcanum:Features:A2AClient for the A2A surface.";

            return new ConclaveA2AStatus(
                idleState,
                ConclaveEnabled: false,
                ServerEnabled: false,
                ClientEnabled: false,
                ServerPath: null,
                AgentCardPath: null,
                allowlistCount,
                idleDetail);

        }

        List<string> notes = [];

        ConclaveA2AState state = ConclaveA2AState.Healthy;

        if (a2a.ServerEnabled)
        {

            Result<string> workspace = ArcanumA2AAgentHandler.ResolveWorkspace(settings, a2a);

            if (workspace.IsFailure)
            {

                state = ConclaveA2AState.Degraded;

                notes.Add(
                    $"inbound Sendings have no usable workspace ({workspace.Error.Message}) — configure "
                    + "Arcanum:Security:CampaignRoots, and optionally Arcanum:Integrations:A2A:DefaultWorkspace");

            }
            else
            {

                notes.Add($"inbound A2A server mounted at {serverPath} (API key required)");

            }

        }

        if (a2a.ClientEnabled)
        {

            notes.Add(
                allowlistCount == 0
                    ? "outbound dispatch_sending enabled; no AllowedRemoteAgents allowlist, so any SSRF-guarded public target is a candidate"
                    : $"outbound dispatch_sending enabled; {allowlistCount} allowed remote agent(s)");

        }

        if (!a2a.ServerEnabled && !a2a.ClientEnabled)
        {

            notes.Add("local cast_sending delegation only; no A2A surface is enabled");

        }

        return new ConclaveA2AStatus(
            state,
            ConclaveEnabled: true,
            a2a.ServerEnabled,
            a2a.ClientEnabled,
            serverPath,
            agentCardPath,
            allowlistCount,
            string.Join("; ", notes) + ".");

    }

    private static bool HasA2AConfiguration(ConclaveA2ASettings a2a) =>
        !string.IsNullOrWhiteSpace(a2a.AgentCardName)
        || !string.IsNullOrWhiteSpace(a2a.AgentCardDescription)
        || !string.IsNullOrWhiteSpace(a2a.DefaultWorkspace)
        || (a2a.AllowedRemoteAgents ?? []).Any(static entry => !string.IsNullOrWhiteSpace(entry))
        || (!string.IsNullOrWhiteSpace(a2a.ServerPath)
            && !string.Equals(
                a2a.ServerPath.Trim(),
                ArcanumRuntimeDefaults.Conclave.A2A.ServerPath,
                StringComparison.Ordinal));

}
