namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Operator-facing state of The Conclave and its A2A surface (<c>GET /api/conclave/status</c>).
/// </summary>
/// <param name="State">
/// <c>disabled</c>, <c>configured</c>, <c>degraded</c>, or <c>healthy</c>.
/// </param>
/// <param name="ServerPath">Effective mount path of the inbound A2A server, or <c>null</c> when it is off.</param>
/// <param name="AgentCardPath">Effective path of the authenticated Agent Card ("Heraldry"), or <c>null</c>.</param>
/// <param name="AllowedRemoteAgentCount">
/// Size of the outbound allowlist. The entries themselves are deliberately not returned: they can name
/// private partner endpoints (docs/Arcanum.DESIGN.md &#167;5.7.1).
/// </param>
public sealed record ConclaveStatusDto(
    string State,
    bool ConclaveEnabled,
    bool A2AServerEnabled,
    bool A2AClientEnabled,
    string? ServerPath,
    string? AgentCardPath,
    int AllowedRemoteAgentCount,
    string Detail);

/// <summary>Request body for <c>POST /api/conclave/sendings</c>.</summary>
/// <param name="Continuable">
/// When <c>true</c>, a remote agent that reaches <c>input-required</c>/<c>auth-required</c> returns a
/// continuation instead of ending the Sending, so it can be answered with
/// <c>POST /api/conclave/sendings/{taskId}/continue</c> (issue #64). Default <c>false</c> preserves the
/// original blocking behavior.
/// </param>
/// <param name="SkillId">
/// Optional Agent Card skill id to target. The dispatch fails with <c>Sending.SkillNotAdvertised</c>
/// before the remote task is created if the peer advertises no such skill (issue #65).
/// </param>
/// <param name="AcceptedOutputModes">
/// Optional media types to accept back. Omitted means "whatever this instance can consume"
/// (<c>Arcanum:Integrations:A2A:InputModes</c>, defaulting to <c>text/plain</c>). A peer whose card can
/// produce none of them is refused with <c>Sending.ModalityMismatch</c> before the remote task exists.
/// </param>
/// <param name="Callback">
/// When <c>true</c>, ask the peer to report back when it finishes instead of holding one of this
/// instance's concurrent-Sending slots for the whole remote run (issue #67). Falls back to the ordinary
/// wait when the peer cannot accept a callback. Takes precedence over <paramref name="Continuable"/>.
/// </param>
public sealed record DispatchSendingRequest(
    string? AgentUrl,
    string? Goal,
    string? Name,
    bool? Continuable = null,
    string? SkillId = null,
    string[]? AcceptedOutputModes = null,
    bool? Callback = null);

/// <summary>Request body for <c>POST /api/conclave/sendings/{taskId}/continue</c>.</summary>
/// <param name="SkillId"><inheritdoc cref="DispatchSendingRequest" path="/param[@name='SkillId']"/></param>
/// <param name="AcceptedOutputModes">
/// <inheritdoc cref="DispatchSendingRequest" path="/param[@name='AcceptedOutputModes']"/>
/// </param>
public sealed record ContinueSendingRequest(
    string? AgentUrl,
    string? Message,
    bool? Continuable = null,
    string? SkillId = null,
    string[]? AcceptedOutputModes = null);

/// <summary>
/// Terminal outcome of an outbound Sending (<c>POST /api/conclave/sendings</c>).
/// </summary>
/// <param name="TaskId">
/// The remote agent's A2A task id, or <c>null</c> when the remote replied with an immediate stateless
/// message rather than creating a task.
/// </param>
/// <param name="CostKnown">
/// Whether the peer reported what the delegated work cost. <c>false</c> means <em>unknown</em>, never
/// free: A2A has no standard usage field, and recording an unreported Sending as zero would quietly
/// understate what a delegated operation cost (issue #60).
/// </param>
/// <param name="DispatchedAt">
/// When the remote accepted the task. Distinct from <paramref name="SettledAt"/> so remote wall-clock is
/// derivable rather than collapsed onto one instant.
/// </param>
/// <param name="ContinuationNeed">
/// <c>input</c> or <c>auth</c> when the remote stopped short and is waiting to be answered; otherwise
/// <c>null</c>.
/// </param>
public sealed record SendingDispatchDto(
    string AgentUrl,
    string? TaskId,
    string ResponseText,
    bool CostKnown = false,
    long? RemoteTotalTokens = null,
    decimal? RemoteCostUsd = null,
    DateTimeOffset? DispatchedAt = null,
    DateTimeOffset? SettledAt = null,
    string? ContinuationNeed = null);
