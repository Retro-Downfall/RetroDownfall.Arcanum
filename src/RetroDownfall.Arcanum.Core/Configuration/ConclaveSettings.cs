namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection for <strong>The Conclave</strong> &#8212; the overarching multi-agent
/// coordination network in which the Master coordinates multiple Apprentices.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> gates the cross-Apprentice delegation surface ("Cast Sending"). When
/// <c>false</c> (the default), the in-process <c>create_agent_task</c> MCP tool is not advertised and the
/// <c>POST /api/apprentices/{id}/cast</c> endpoint refuses delegation. Operators may opt in directly
/// via <c>Arcanum:Features:Conclave</c>; either A2A server/client opt-in also derives Conclave
/// availability because those surfaces depend on it. Model task tools additionally require a live
/// Guild invocation and exact tool authority; this switch alone does not enable them.
/// </para>
/// </remarks>
public sealed record ConclaveSettings
{

    /// <summary>
    /// When <c>true</c>, enables The Conclave's cross-Apprentice delegation (Cast Sending). Derived
    /// from the direct Conclave opt-in or either A2A surface; default <c>false</c>.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// A2A (Agent-to-Agent) protocol projection for The Conclave. See
    /// <see cref="ConclaveA2ASettings"/>.
    /// </summary>
    public ConclaveA2ASettings A2A { get; set; } = new();

}

/// <summary>
/// Runtime projection for the A2A (Agent-to-Agent) protocol surface layered on The Conclave.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> is derived from the server/client opt-ins, and either opt-in also derives
/// parent <see cref="ConclaveSettings.Enabled"/> availability. Server/client activation comes from
/// <c>Arcanum:Features:A2AServer</c> / <c>Arcanum:Features:A2AClient</c>; identity and endpoint data come from
/// <c>Arcanum:Integrations:A2A</c>. Operators do not need to maintain separate parent toggles.
/// </para>
/// <para>
/// <see cref="ServerEnabled"/> exposes Arcanum Apprentices as A2A tasks to external agents (the "Heraldry" /
/// Agent Card surface). <see cref="ClientEnabled"/> enables outbound operator Sending dispatch through
/// the Archmage Client and supplies the remote transport capability for Guild <c>create_agent_task</c>.
/// The model tool also requires a live Guild invocation, exact grants and a configured discovery destination. Documented in
/// <c>docs/Arcanum.DESIGN.md</c> &#167;3.4 / &#167;5.7.1.
/// </para>
/// </remarks>
public sealed record ConclaveA2ASettings
{

    /// <summary>
    /// Derived gate for the A2A server and client surfaces. Default <c>false</c>.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When <c>true</c> (and <see cref="Enabled"/>), exposes Arcanum Apprentices as an A2A server. Default <c>false</c>.
    /// </summary>
    public bool ServerEnabled { get; set; } = false;

    /// <summary>
    /// HTTP path under which the A2A server endpoints (and authenticated Agent Card) are mapped. Default <c>/api/conclave/a2a</c>.
    /// </summary>
    public string ServerPath { get; set; } = "/api/conclave/a2a";

    /// <summary>
    /// Display name advertised on the A2A Agent Card ("Heraldry").
    /// </summary>
    public string? AgentCardName { get; set; }

    /// <summary>
    /// Display description advertised on the A2A Agent Card ("Heraldry").
    /// </summary>
    public string? AgentCardDescription { get; set; }

    /// <summary>
    /// When <c>true</c> (and <see cref="Enabled"/>), enables outbound operator Sending dispatch and the
    /// remote transport capability used by Guild <c>create_agent_task</c>. Model use additionally requires
    /// a live Guild invocation, exact grants and a configured discovery destination. Default <c>false</c>.
    /// </summary>
    public bool ClientEnabled { get; set; } = false;

    /// <summary>
    /// Optional outbound allowlist of remote Agent Card URLs (or origins). Empty (default) adds no
    /// restriction beyond the outbound SSRF guard for operator Sending dispatch. Guild model tasks
    /// additionally require an exact configured discovery destination, independently of this allowlist.
    /// </summary>
    public string[] AllowedRemoteAgents { get; set; } = [];

    /// <summary>Explicit authenticated inbound principals and standing grants. Disabled unless enabled per peer.</summary>
    public A2AInboundPeerSettings[] InboundPeers { get; set; } = [];

    /// <summary>
    /// Fallback workspace path for inbound A2A tasks (server side) when the request carries no workspace or
    /// campaign hint. Empty (default) falls back to <c>Arcanum:Workspaces:DefaultRoot</c>, then the
    /// process's current directory, validated the same way as every other Apprentice workspace resolution.
    /// </summary>
    public string DefaultWorkspace { get; set; } = string.Empty;

    /// <summary>
    /// Optional environment-variable name holding the outbound peer credential for A2A dispatch.
    /// The transport presents it only to a destination vouched for by a non-empty allowlist.
    /// Empty (default) sends no credential; authenticated peers require configured credentials and grants.
    /// </summary>
    public string OutboundCredentialEnvironmentVariable { get; set; } = string.Empty;

    /// <summary>
    /// Header carrying the outbound credential. Defaults to Arcanum's own API-key header
    /// (<c>X-Arcanum-Key</c>); set it to <c>Authorization</c> with a <c>Bearer …</c> value for agents that
    /// expect standard bearer auth.
    /// </summary>
    public string OutboundCredentialHeader { get; set; } = "X-Arcanum-Key";

    /// <summary>
    /// Operator-declared Agent Card skills. Empty (default) advertises the single historical
    /// <c>apprentice-goal-execution</c> skill so the default card is unchanged (issue #63).
    /// </summary>
    public A2ASkillSettings[] Skills { get; set; } = [];

    /// <summary>Advertised inbound media types. Empty (default) means <c>text/plain</c>.</summary>
    public string[] InputModes { get; set; } = [];

    /// <summary>Advertised outbound media types. Empty (default) means <c>text/plain</c>.</summary>
    public string[] OutputModes { get; set; } = [];

    /// <summary>
    /// Whether the A2A push-notification surface is enabled in either direction. Default <c>false</c>;
    /// the Agent Card advertises <c>pushNotifications</c> only when this is on (issue #67).
    /// </summary>
    public bool PushNotificationsEnabled { get; set; }

    /// <summary>
    /// Externally reachable base URL peers post outbound-Sending callbacks to. Empty (default) means
    /// callback mode is unavailable — this instance cannot tell a peer where to reach it.
    /// </summary>
    public string PushCallbackBaseUrl { get; set; } = string.Empty;

}
