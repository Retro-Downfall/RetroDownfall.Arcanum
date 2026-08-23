using System.Runtime.Versioning;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// The fixed host-tools marker slot on Linux, which blocks rather than deleting by attributes.
/// </summary>
/// <remarks>
/// The ordinary Linux credential store reaches the Secret Service through
/// <c>secret_password_lookup_sync</c> and <c>secret_password_clear_sync</c>, which take a schema and
/// a set of attributes and act on whatever currently matches them. That is correct for an ordinary
/// credential and disqualifying here: a clear-by-attributes performed after this capability was
/// opened would remove whichever item now matches, and a byte-identical item written in between
/// matches exactly as well as the one that was compared. Retaining a stable item identity needs the
/// <c>SecretItem</c> API family instead — search, load, and delete against one retained object —
/// which is a different set of entry points from the ones this project has proven against a real
/// Secret Service.
///
/// <para>So this arm reports <see cref="HostProcessToolsMarkerCredentialOpenStatus.Unavailable"/>
/// and refuses to prove absence, which is the fail-closed answer: a reset on this platform stops
/// with both markers intact and recoverable by hand, rather than performing a delete it cannot
/// prove acted on the item it compared. Absence in particular is never reported — an unproven
/// absence is the one answer that would let a reset continue past a marker that is still there.</para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxHostProcessToolsMarkerSlot
    : IHostProcessToolsMarkerCredentialCapabilitySource
{

    public HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot() =>
        HostProcessToolsMarkerCredentialOpenResult.Unavailable();

    public HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent() =>
        HostProcessToolsMarkerCredentialAbsenceResult.Unavailable();

}
