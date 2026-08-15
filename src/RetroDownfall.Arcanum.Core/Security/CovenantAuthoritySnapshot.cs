using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Whether host development tooling has been observed against this installation's credentials.
/// </summary>
/// <remarks>
/// The three states are one-way: an installation may leave <see cref="Clean"/>, but nothing may
/// return it there. A taint records that same-identity code could once have recovered this
/// installation's credentials, and no later rotation, reinstall, or restart unasks that question, so
/// a converging state machine would be a way to forget evidence rather than a way to recover.
///
/// <para>No member is zero. A zero would be indistinguishable from a default-initialized field, and
/// "accidentally clean" is the one wrong answer this type must never give.</para>
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantHostToolsState>))]
public enum CovenantHostToolsState : byte
{

    Clean = 1,

    PendingHostToolsTaint = 2,

    HostToolsTainted = 3,

}

/// <summary>
/// One immutable view of who this installation is and which authority generation is in force.
/// </summary>
/// <remarks>
/// Deliberately carries no secret and nothing secret-derived: no key material, no master-key
/// fingerprint bytes, no Grimoire or installation path. It is published to startup gates,
/// diagnostics, and operator surfaces, so every field here has to survive being logged.
///
/// <para>The counters are the whole point. <see cref="MasterKeyVersion"/>,
/// <see cref="AuthorityEpoch"/>, and <see cref="RecoveryEnvelopeEpoch"/> only ever advance, so a
/// reader comparing them to what it last saw learns that the master key changed without ever being
/// shown the key. Rotating back to a previously used key advances them again rather than restoring
/// an earlier value, which is what stops a rotate-back from replaying as "nothing happened".</para>
///
/// <para>Published as a whole record swapped at once, so no reader can observe a new epoch beside a
/// stale host-tools state. <see cref="TransitionId"/> is present exactly when
/// <see cref="HostToolsState"/> is <see cref="CovenantHostToolsState.PendingHostToolsTaint"/> or
/// <see cref="CovenantHostToolsState.HostToolsTainted"/>; it names the one escape being remediated
/// so two separate escapes cannot be collapsed into a single record.</para>
/// </remarks>
public sealed record CovenantAuthoritySnapshot(
    string InstallationIdentity,
    long AuthorityEpoch,
    uint MasterKeyVersion,
    long RecoveryEnvelopeEpoch,
    CovenantHostToolsState HostToolsState,
    string? TransitionId);
