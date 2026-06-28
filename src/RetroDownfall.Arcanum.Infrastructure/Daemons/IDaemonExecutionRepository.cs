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

    Task<DaemonExecutionSummary> CancelAsync(string executionId, CancellationToken ct);

    bool HasRunningExecution(string daemonId);

    CancellationTokenSource? GetCancellationTokenSource(string executionId);

}
