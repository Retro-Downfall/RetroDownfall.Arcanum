namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Configuration for <strong>The Conclave</strong> &#8212; the overarching multi-agent coordination
/// network in which the Master (Wizard) coordinates multiple Apprentices.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> gates the cross-Apprentice delegation surface ("Cast Sending"). When
/// <c>false</c> (the default), the in-process <c>cast_sending</c> MCP tool is not advertised and the
/// <c>POST /api/apprentices/{id}/cast</c> endpoint refuses delegation, so operators opt in to
/// multi-agent fan-out explicitly. Bound from <c>Arcanum:Conclave</c>.
/// </para>
/// <para>
/// This block replaces the former reserved <c>Arcanum:Bureau</c> placeholder. Documented in
/// DESIGN.md &#167;3.4 / &#167;16.
/// </para>
/// </remarks>
public sealed record ConclaveSettings
{

    /// <summary>
    /// When <c>true</c>, enables The Conclave's cross-Apprentice delegation (Cast Sending). Default <c>false</c>.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Maximum delegation depth from a Conclave root Apprentice (0 = root only, no children). Default <c>3</c>.
    /// </summary>
    public int MaxDelegationDepth { get; init; } = 3;

    /// <summary>
    /// Maximum total descendant Apprentices allowed under one Conclave root. Default <c>16</c>.
    /// </summary>
    public int MaxDescendantsPerRoot { get; init; } = 16;

    /// <summary>
    /// A2A (Agent-to-Agent) protocol interoperability surface for The Conclave. Bound from
    /// <c>Arcanum:Conclave:A2A</c>. See <see cref="ConclaveA2ASettings"/>.
    /// </summary>
    public ConclaveA2ASettings A2A { get; init; } = new();

}

/// <summary>
/// Configuration for the A2A (Agent-to-Agent) protocol surface layered on top of The Conclave.
/// </summary>
/// <remarks>
/// <para>
/// Both <see cref="Enabled"/> (this block) and the parent <see cref="ConclaveSettings.Enabled"/> must be
/// <c>true</c> for any A2A surface — server or client — to activate. Default is <c>false</c>: zero behavior
/// change until an operator explicitly opts in. Bound from <c>Arcanum:Conclave:A2A</c>.
/// </para>
/// <para>
/// <see cref="ServerEnabled"/> exposes Arcanum Apprentices as A2A tasks to external agents (the "Heraldry" /
/// Agent Card surface). <see cref="ClientEnabled"/> enables the in-process <c>dispatch_sending</c> MCP tool
/// so an Apprentice can delegate a "Sending" to a remote A2A agent (the "Archmage Client"). Documented in
/// DESIGN.md &#167;3.4 / &#167;5.7.1.
/// </para>
/// </remarks>
public sealed record ConclaveA2ASettings
{

    /// <summary>
    /// Master toggle gating both the A2A server and client surfaces. Default <c>false</c>.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// When <c>true</c> (and <see cref="Enabled"/>), exposes Arcanum Apprentices as an A2A server. Default <c>false</c>.
    /// </summary>
    public bool ServerEnabled { get; init; } = false;

    /// <summary>
    /// HTTP path under which the A2A server endpoints (and authenticated Agent Card) are mapped. Default <c>/api/conclave/a2a</c>.
    /// </summary>
    public string ServerPath { get; init; } = "/api/conclave/a2a";

    /// <summary>
    /// Display name advertised on the A2A Agent Card ("Heraldry").
    /// </summary>
    public string? AgentCardName { get; init; }

    /// <summary>
    /// Display description advertised on the A2A Agent Card ("Heraldry").
    /// </summary>
    public string? AgentCardDescription { get; init; }

    /// <summary>
    /// When <c>true</c> (and <see cref="Enabled"/>), advertises and enables the in-process <c>dispatch_sending</c>
    /// MCP tool so an Apprentice can delegate to an external A2A agent. Default <c>false</c>.
    /// </summary>
    public bool ClientEnabled { get; init; } = false;

    /// <summary>
    /// Maximum number of concurrently in-flight external (client-side) A2A delegations. Default <c>50</c>, clamped 1-500.
    /// </summary>
    public int MaxExternalTasks { get; init; } = 50;

    /// <summary>
    /// Per-delegation timeout, in minutes, for a client-side <c>dispatch_sending</c> call. Default <c>60</c>, clamped 5-1440.
    /// </summary>
    public int ExternalTaskTimeoutMinutes { get; init; } = 60;

    /// <summary>
    /// Optional allowlist of remote Agent Card URLs (or origins) that <c>dispatch_sending</c> may target.
    /// Empty (default) means any URL is a candidate, subject to the outbound SSRF guard, which always applies
    /// regardless of this allowlist.
    /// </summary>
    public string[] AllowedRemoteAgents { get; init; } = [];

    /// <summary>
    /// Fallback workspace path for inbound A2A tasks (server side) when the request carries no workspace or
    /// campaign hint. Empty (default) falls back to <c>Arcanum:Host:Workspace</c>, then the process's current
    /// directory, validated the same way as every other Apprentice workspace resolution.
    /// </summary>
    public string DefaultWorkspace { get; init; } = string.Empty;

}
