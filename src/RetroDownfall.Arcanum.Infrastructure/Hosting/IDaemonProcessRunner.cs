namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Runs the OS service-manager helper binary (<c>launchctl</c>, <c>systemctl</c>, <c>sc.exe</c>) on behalf
/// of a daemon manager. It is the seam that lets a test drive a manager without touching the host's
/// real service manager.
/// </summary>
internal interface IDaemonProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> to completion. A helper that cannot be started, or that outlives
    /// the runner's bound and is killed, is reported as <see cref="DaemonProcessOutcome.FatalError"/>
    /// rather than thrown. Cancelling <paramref name="cancellationToken"/> kills the helper's process tree
    /// and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<DaemonProcessOutcome> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}
