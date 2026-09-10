namespace RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.Infrastructure;

/// <summary>
/// Decides whether an interactive CLI session owns the Arcanum host it is talking to, and may
/// therefore stop it on exit.
/// </summary>
internal static class ServeOwnershipPolicy
{
    /// <summary>
    /// True only when this process started the host. A host that was already running belongs to
    /// whoever started it — leaving it up is the whole point, since the operator may be using it from
    /// another terminal, The Forge, or an editor. They can stop it with <c>arcanum serve quit</c>.
    /// A failed or disabled launch owns nothing.
    /// </summary>
    public static bool OwnsHost(ServeLaunchResult? launch) =>
        launch?.Status == ServeLaunchStatus.Started;

    /// <summary>
    /// A disabled auto-launch is not a failed connection attempt: noninteractive callers must
    /// still be allowed to reach a host managed by another process. Only an observed launch or
    /// authentication failure stops downstream work.
    /// </summary>
    public static bool CanProceed(ServeLaunchResult launch) =>
        launch.Status is ServeLaunchStatus.AlreadyRunning
            or ServeLaunchStatus.Started
            or ServeLaunchStatus.LaunchDisabled;

    public static CliExitCode FailureExitCode(ServeLaunchResult launch) =>
        launch.Status switch
        {
            ServeLaunchStatus.AuthFailed => CliExitCode.ConfigurationError,
            ServeLaunchStatus.Failed => CliExitCode.NetworkError,
            _ => throw new ArgumentException(
                "A successful or disabled launch has no failure exit code.",
                nameof(launch)),
        };
}
