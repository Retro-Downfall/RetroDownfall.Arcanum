using RetroDownfall.Arcanum.Core.Daemons;

namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

public interface IDaemonExecutionRepository
{

    Task<DaemonExecutionSummary[]> GetHistoryAsync(string? daemonId, CancellationToken ct);

    Task<DaemonExecutionDetail?> GetAsync(string executionId, CancellationToken ct);

    Task<string> StartAsync(string daemonId, string daemonName, CancellationToken ct);

    /// <summary>
    /// Atomically reserves the in-flight slot for <paramref name="daemonId"/> and starts an
    /// execution with the caller-supplied <paramref name="executionId"/>. Returns <see langword="true"/>
    /// when the slot was free and the execution was recorded; <see langword="false"/> when a
    /// execution is already running for this daemon. Use this for the on-demand path so the
    /// not-already-running check and the reservation cannot be interleaved by a concurrent call.
    /// </summary>
    Task<bool> TryStartAsync(string daemonId, string daemonName, string executionId, CancellationToken ct);

    Task<DaemonExecutionSummary> CompleteAsync(string executionId, CancellationToken ct);

    Task<DaemonExecutionSummary> FailAsync(string executionId, string errorMessage, CancellationToken ct);

    /// <summary>
    /// Signals the execution's cancellation token and records it terminal. Cancellation is cooperative, so
    /// returning proves only that the request was recorded — the job body may still be unwinding, and the
    /// per-daemon in-flight reservation is deliberately held (and the token source left undisposed) until
    /// the drain is reported. This method never reports it, however many times it is called: it is
    /// reachable straight from <c>POST /api/daemons/executions/{id}/cancel</c>, so a repeat means only
    /// that an operator asked twice. <see cref="CompleteAsync"/> and <see cref="FailAsync"/> carry the
    /// report implicitly, and <see cref="ReportDrainedAsync"/> carries it for a run that ended cancelled.
    /// </summary>
    Task<DaemonExecutionSummary> CancelAsync(string executionId, CancellationToken ct);

    /// <summary>
    /// Reports that <c>job.RunAsync</c> has returned, releasing the per-daemon in-flight reservation and
    /// disposing the execution's token source without touching the recorded terminal status.
    /// </summary>
    /// <remarks>
    /// The runner is the only caller, because it is the only thing that knows the body has unwound. A run
    /// that ended cancelled terminates through <see cref="CancelAsync"/>, and overloading that method's
    /// re-entry as the drain signal cannot work: a second operator cancel produces the identical signal
    /// and would free this daemon's only single-flight reservation while the first body is still
    /// mid-turn, letting a second headless run start against the same target spell. Idempotent, and a
    /// no-op for an execution id that no longer exists.
    /// </remarks>
    Task ReportDrainedAsync(string executionId, CancellationToken ct);

    Task<bool> TryDeleteTerminalAsync(string executionId, CancellationToken ct);

    Task<bool> TryDeleteTerminalBeforeAsync(
        string executionId,
        DateTimeOffset completedAtCutoff,
        CancellationToken ct);

    bool HasRunningExecution(string daemonId);

    CancellationTokenSource? GetCancellationTokenSource(string executionId);

}

internal interface IDaemonExecutionMutationGate
{

    ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
        CancellationToken cancellationToken = default);

}
