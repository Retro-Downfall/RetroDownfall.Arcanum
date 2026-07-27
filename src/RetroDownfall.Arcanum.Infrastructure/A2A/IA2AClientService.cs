using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Outcome of a successful <see cref="IA2AClientService.DispatchSendingAsync"/> call.
/// </summary>
/// <param name="TaskId">
/// The remote agent's A2A task id when the exchange used task-based communication, or <c>null</c> when the
/// remote agent replied with an immediate stateless message.
/// </param>
/// <param name="ResponseText">The remote agent's final response text.</param>
public sealed record A2ADispatchResult(string? TaskId, string ResponseText);

/// <summary>
/// The <strong>Archmage Client</strong>: the Conclave's outward-facing delegate to external A2A-compatible
/// agents. Wraps Agent Card discovery, SSRF-guarded outbound connections, and blocking task dispatch behind
/// the in-process <c>dispatch_sending</c> MCP tool.
/// </summary>
public interface IA2AClientService
{

    /// <summary>
    /// Discovers <paramref name="agentUrl"/>'s Agent Card, sends <paramref name="goal"/> as a new A2A message,
    /// and blocks until the remote agent responds, the task reaches a terminal state, or the caller/host
    /// cancels the operation.
    /// </summary>
    Task<Result<A2ADispatchResult>> DispatchSendingAsync(
        string goal,
        string? name,
        string agentUrl,
        CancellationToken cancellationToken = default);

}
