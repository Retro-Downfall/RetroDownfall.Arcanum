using RetroDownfall.Arcanum.Core.Daemons;

namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

public interface IDaemonExecutionRepository
{

    Task<DaemonExecutionSummary[]> GetHistoryAsync(string? daemonId, CancellationToken ct);

    Task<DaemonExecutionDetail?> GetAsync(string executionId, CancellationToken ct);

    Task<string> StartAsync(string daemonId, string daemonName, CancellationToken ct);

    Task<DaemonExecutionSummary> CompleteAsync(string executionId, CancellationToken ct);

    Task<DaemonExecutionSummary> FailAsync(string executionId, string errorMessage, CancellationToken ct);

    Task<DaemonExecutionSummary> CancelAsync(string executionId, CancellationToken ct);

    bool HasRunningExecution(string daemonId);

    CancellationTokenSource? GetCancellationTokenSource(string executionId);

}
