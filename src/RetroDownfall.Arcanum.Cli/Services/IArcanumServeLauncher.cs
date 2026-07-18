namespace RetroDownfall.Arcanum.Cli.Services;

public enum ServeLaunchStatus
{

    AlreadyRunning,

    Started,

    AuthFailed,

    LaunchDisabled,

    Failed,

}

public sealed record ServeLaunchResult(
    ServeLaunchStatus Status,
    HealthProbeState Health,
    TimeSpan Elapsed,
    string? LogPath,
    string? Guidance);

/// <summary>
/// Ensures a local <c>arcanum serve</c> host is reachable for interactive CLI sessions.
/// CLI-only process orchestration — not a Core concern.
/// </summary>
public interface IArcanumServeLauncher
{

    Task<ServeLaunchResult> EnsureRunningAsync(CancellationToken cancellationToken);

}
