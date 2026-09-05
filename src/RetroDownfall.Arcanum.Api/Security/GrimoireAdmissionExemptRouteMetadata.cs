namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Marks one route that must keep answering while maintenance owns Grimoire connection admission.
/// </summary>
/// <remarks>
/// It is a marker of its own rather than a reuse of
/// <see cref="InstallationResetRecoveryApiRouteMetadata"/>, which admits three routes on a recovery
/// host. Two of those three belong here and the third emphatically does not: the factory-reset route
/// is the request maintenance runs <i>for</i>, and it has to hold a lease so it can be promoted out
/// of its own drain. Sharing one marker between "admitted during recovery" and "exempt from
/// admission" would make that difference invisible at the point where it matters most.
///
/// <para>The set is exactly two, and both are exempt because gating them makes the host less
/// answerable rather than more. Health already reports a closed gate as an Unhealthy component naming
/// only an exception type, inside its documented success envelope; replacing that with a bare refusal
/// would hide the readiness snapshot in the one window an operator most needs it, and would invert the
/// envelope that <c>arcanum doctor</c>, <c>arcanum watch health</c> and auto-launch all read. Quit is
/// the shutdown step of the factory-reset sequence and opens nothing; refusing it would strand a reset
/// between its host-apply proof and its offline continuation.</para>
///
/// <para>Neither route is a way past the gate. They are refused by the database itself if they touch
/// it, through the same enrolment interceptor every other caller meets.</para>
/// </remarks>
internal sealed class GrimoireAdmissionExemptRouteMetadata
{

    internal static GrimoireAdmissionExemptRouteMetadata Instance { get; } = new();

    private GrimoireAdmissionExemptRouteMetadata()
    {
    }

}
