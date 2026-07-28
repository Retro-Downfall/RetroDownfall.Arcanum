namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Resolves effective <c>PingRequest.UnattendedMode</c> for operator-facing CLI surfaces.
/// </summary>
public static class OperatorFacingUnattendedMode
{
    /// <summary>
    /// <paramref name="cliUnattendedFlag"/> (<c>--unattended</c>) wins when true; otherwise the host
    /// <see cref="WardSettings.UnattendedMode"/> default applies.
    /// </summary>
    public static bool Resolve(bool cliUnattendedFlag, WardSettings? ward) =>
        cliUnattendedFlag || (ward?.UnattendedMode ?? false);
}
