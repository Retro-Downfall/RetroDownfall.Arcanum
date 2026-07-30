namespace RetroDownfall.Arcanum.Cli.Services;

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

}
