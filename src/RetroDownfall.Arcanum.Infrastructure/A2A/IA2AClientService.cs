using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// How far an outbound Sending should carry the work before it hands control back.
/// </summary>
public enum A2ADispatchMode
{

    /// <summary>
    /// Block until the remote task reaches an A2A terminal state. <c>input-required</c> and
    /// <c>auth-required</c> end the Sending with an actionable reason and cancel the remote task,
    /// because a blocking call has no way to supply the follow-up. This is the default and the
    /// historical behavior.
    /// </summary>
    Blocking = 0,

    /// <summary>
    /// Block until the remote task settles <em>or</em> asks for something. <c>input-required</c> /
    /// <c>auth-required</c> return successfully with <see cref="A2ADispatchResult.Continuation"/> set
    /// and the remote task left alive, so the Mage can answer it with
    /// <see cref="IA2AClientService.ContinueSendingAsync"/> (issue #64).
    /// </summary>
    Continuable = 1,

}

/// <summary>What a remote agent is waiting for before it can finish.</summary>
public enum A2AContinuationNeed
{

    /// <summary>The remote agent asked for more input (<c>input-required</c>).</summary>
    Input = 0,

    /// <summary>The remote agent asked for authentication (<c>auth-required</c>).</summary>
    Authentication = 1,

}

/// <summary>
/// A Sending that stopped short of a terminal state and can be resumed with more information.
/// </summary>
/// <param name="TaskId">The live remote task id to continue.</param>
/// <param name="Need">Whether the remote wants input or credentials.</param>
/// <param name="Reason">The remote agent's own explanation of what it needs.</param>
public sealed record A2ASendingContinuation(string TaskId, A2AContinuationNeed Need, string Reason);

/// <summary>
/// Outcome of a successful <see cref="IA2AClientService.DispatchSendingAsync"/> call.
/// </summary>
/// <param name="TaskId">
/// The remote agent's A2A task id when the exchange used task-based communication, or <c>null</c> when the
/// remote agent replied with an immediate stateless message.
/// </param>
/// <param name="ResponseText">The remote agent's final response text.</param>
/// <param name="Cost">
/// What the delegated work cost on the far side, or <see cref="A2ARemoteCost.Unknown"/> when the peer
/// reported nothing. Never silently zero (issue #60).
/// </param>
/// <param name="DispatchedAt">When the remote task was accepted. Distinct from <paramref name="SettledAt"/>.</param>
/// <param name="SettledAt">When the remote task reached its final observed state.</param>
/// <param name="Continuation">
/// Set only in <see cref="A2ADispatchMode.Continuable"/> mode when the remote stopped at
/// <c>input-required</c> / <c>auth-required</c> and is waiting to be answered.
/// </param>
public sealed record A2ADispatchResult(
    string? TaskId,
    string ResponseText,
    A2ARemoteCost? Cost = null,
    DateTimeOffset DispatchedAt = default,
    DateTimeOffset SettledAt = default,
    A2ASendingContinuation? Continuation = null)
{

    /// <summary>Never null: an absent cost is explicitly unknown, not zero.</summary>
    public A2ARemoteCost RemoteCost => Cost ?? A2ARemoteCost.Unknown;

    /// <summary>
    /// Remote wall-clock, derivable because the dispatch and settle instants are stamped separately.
    /// <c>null</c> when either instant is unset (a stateless reply that never created a task).
    /// </summary>
    public TimeSpan? RemoteDuration =>
        DispatchedAt == default || SettledAt == default || SettledAt < DispatchedAt
            ? null
            : SettledAt - DispatchedAt;

}

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
    /// cancels the operation. Local cancellation also cancels the remote task.
    /// </summary>
    /// <param name="delegationChain">
    /// A2A delegation hops that led here, oldest first. This node is appended and the result travels with
    /// the Sending so a downstream agent can detect a loop back to an earlier hop. Omit (or pass empty) for
    /// a Sending that originates locally.
    /// </param>
    /// <param name="progress">
    /// Optional observer notified on each remote task-state change while the Sending is in flight, so a
    /// long Sending is not a black box between dispatch and its terminal frame (issue #61).
    /// </param>
    /// <param name="mode">
    /// Whether <c>input-required</c> / <c>auth-required</c> end the Sending (default) or return a
    /// continuation the Mage can answer (issue #64).
    /// </param>
    Task<Result<A2ADispatchResult>> DispatchSendingAsync(
        string goal,
        string? name,
        string agentUrl,
        IReadOnlyList<string>? delegationChain = null,
        CancellationToken cancellationToken = default,
        IProgress<A2ASendingProgress>? progress = null,
        A2ADispatchMode mode = A2ADispatchMode.Blocking);

    /// <summary>
    /// Answers a Sending that stopped at <c>input-required</c> / <c>auth-required</c> and resumes waiting
    /// on the same remote task, rather than cancelling it and starting over (issue #64).
    /// </summary>
    /// <param name="taskId">The remote task id from <see cref="A2ADispatchResult.Continuation"/>.</param>
    /// <param name="message">The follow-up the remote agent asked for.</param>
    Task<Result<A2ADispatchResult>> ContinueSendingAsync(
        string agentUrl,
        string taskId,
        string message,
        IReadOnlyList<string>? delegationChain = null,
        CancellationToken cancellationToken = default,
        IProgress<A2ASendingProgress>? progress = null,
        A2ADispatchMode mode = A2ADispatchMode.Blocking);

    /// <summary>
    /// Cancels a remote A2A task this instance previously dispatched, subject to the same allowlist,
    /// SSRF, and credential-scoping rules as a dispatch.
    /// </summary>
    /// <remarks>
    /// Startup reconciliation uses this to stop remote work that outlived the process which started it:
    /// without a durable record and a way to act on it, an abandoned Sending kept running and billing on
    /// the far side (issue #62).
    /// </remarks>
    Task<Result> CancelRemoteTaskAsync(
        string agentUrl,
        string taskId,
        CancellationToken cancellationToken = default);

}
