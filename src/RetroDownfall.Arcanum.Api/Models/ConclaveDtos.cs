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
public sealed record DispatchSendingRequest(string? AgentUrl, string? Goal, string? Name);

/// <summary>
/// Terminal outcome of an outbound Sending (<c>POST /api/conclave/sendings</c>).
/// </summary>
/// <param name="TaskId">
/// The remote agent's A2A task id, or <c>null</c> when the remote replied with an immediate stateless
/// message rather than creating a task.
/// </param>
public sealed record SendingDispatchDto(
    string AgentUrl,
    string? TaskId,
    string ResponseText);
