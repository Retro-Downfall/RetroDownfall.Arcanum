namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection for <strong>The Conclave</strong> &#8212; the overarching multi-agent
/// coordination network in which the Master coordinates multiple Apprentices.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> gates the cross-Apprentice delegation surface ("Cast Sending"). When
/// <c>false</c> (the default), the in-process <c>cast_sending</c> MCP tool is not advertised and the
/// <c>POST /api/apprentices/{id}/cast</c> endpoint refuses delegation. Operators may opt in directly
/// via <c>Arcanum:Features:Conclave</c>; either A2A server/client opt-in also derives Conclave
/// availability because those surfaces depend on it.
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
/// Agent Card surface). <see cref="ClientEnabled"/> enables the in-process <c>dispatch_sending</c> MCP tool
/// so an Apprentice can delegate a "Sending" to a remote A2A agent (the "Archmage Client"). Documented in
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
    /// When <c>true</c> (and <see cref="Enabled"/>), advertises and enables the in-process <c>dispatch_sending</c>
    /// MCP tool so an Apprentice can delegate to an external A2A agent. Default <c>false</c>.
    /// </summary>
    public bool ClientEnabled { get; set; } = false;

    /// <summary>
    /// Optional allowlist of remote Agent Card URLs (or origins) that <c>dispatch_sending</c> may target.
    /// Empty (default) means any URL is a candidate, subject to the outbound SSRF guard, which always applies
    /// regardless of this allowlist.
    /// </summary>
    public string[] AllowedRemoteAgents { get; set; } = [];

    /// <summary>
    /// Fallback workspace path for inbound A2A tasks (server side) when the request carries no workspace or
    /// campaign hint. Empty (default) falls back to <c>Arcanum:Workspaces:DefaultRoot</c>, then the
    /// process's current directory, validated the same way as every other Apprentice workspace resolution.
    /// </summary>
    public string DefaultWorkspace { get; set; } = string.Empty;

    /// <summary>
    /// Optional environment-variable name holding the credential <c>dispatch_sending</c> presents to remote
    /// agents. Empty (default) sends no credential, which means only unauthenticated peers are reachable —
    /// notably <em>not</em> another Arcanum, whose A2A routes all require an API key.
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

}
