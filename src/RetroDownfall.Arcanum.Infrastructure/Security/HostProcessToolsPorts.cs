using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>The validated <c>covenant_authority_state</c> singleton.</summary>
/// <remarks>
/// Carries the current key facts as well as the taint facts, because the transition pins the taint
/// to the key in force when it happened and every later rotation must leave that pin alone.
/// </remarks>
internal sealed record HostProcessToolsAuthorityRow(
    string InstallationIdentity,
    long AuthorityEpoch,
    uint CurrentMasterKeyVersion,
    CovenantDigest CurrentMasterKeyFingerprint,
    long RecoveryEnvelopeEpoch,
    CovenantHostToolsState State,
    Guid? TransitionId,
    ulong? TaintMasterKeyVersion,
    CovenantDigest? TaintFingerprint)
{

    /// <summary>The row as the pure joiner is allowed to see it.</summary>
    internal HostProcessToolsDatabaseMarkerEvidence ToEvidence() =>
        new(InstallationIdentity, State, TransitionId, TaintMasterKeyVersion, TaintFingerprint);

}

/// <summary>How much Covenant-owned or protected state the installation still holds.</summary>
internal sealed record HostProcessToolsProtectedInventory(
    long CanonicalRowCount,
    long ProtectedArtifactCount)
{

    internal bool IsEmpty => CanonicalRowCount == 0 && ProtectedArtifactCount == 0;

}

/// <summary>The durable authority side of the transition.</summary>
/// <remarks>
/// Every mutation is a compare-and-swap against the exact row the caller read, so a concurrent
/// writer cannot be overwritten and a resumed transition cannot advance a state it did not observe.
/// </remarks>
internal interface IHostProcessToolsAuthorityStore
{

    Task<Result<HostProcessToolsAuthorityRow>> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the row, or reports its absence as success with a null value.
    /// </summary>
    /// <remarks>
    /// A database that has never been installed has no authority row, and startup has to tell that
    /// apart from a row it failed to read. The transition service uses <see cref="ReadAsync"/>
    /// instead, because an installation with no authority row is not one it may taint.
    /// </remarks>
    Task<Result<HostProcessToolsAuthorityRow?>> TryReadAsync(CancellationToken cancellationToken);

    Task<Result<HostProcessToolsProtectedInventory>> InventoryProtectedStateAsync(
        CancellationToken cancellationToken);

    /// <summary>Clean to <c>PendingHostToolsTaint</c>, pinning the taint to the current key.</summary>
    Task<Result> CommitPendingAsync(
        HostProcessToolsAuthorityRow expected,
        Guid transitionId,
        CancellationToken cancellationToken);

    /// <summary><c>PendingHostToolsTaint</c> to <c>HostToolsTainted</c>, advancing both epochs.</summary>
    Task<Result> CommitTaintedAsync(
        HostProcessToolsAuthorityRow expected,
        Guid transitionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>PendingHostToolsTaint</c> back to Clean, permitted only when no operating-system write was
    /// ever attempted for this transition.
    /// </summary>
    Task<Result> CompensateToCleanAsync(
        HostProcessToolsAuthorityRow expected,
        Guid transitionId,
        CancellationToken cancellationToken);

}

/// <summary>What one attempt to write the dedicated operating-system slot proved.</summary>
/// <remarks>
/// The three values are about evidence, not severity. <see cref="Uncertain"/> is the important one:
/// a credential backend that faulted mid-write may or may not have stored the marker, and treating
/// that as a failure would let compensation delete a marker that is the only record of an escape.
/// </remarks>
internal enum HostProcessToolsMarkerWriteStatus : byte
{

    Written = 1,

    Uncertain = 2,

    Refused = 3,

}

/// <summary>Why a marker read did not produce evidence.</summary>
internal enum HostProcessToolsMarkerReadStatus : byte
{

    Present = 1,

    Absent = 2,

    Unavailable = 3,

    Malformed = 4,

}

/// <summary>One marker read, with evidence exactly when the status is present.</summary>
internal sealed record HostProcessToolsMarkerReadResult(
    HostProcessToolsMarkerReadStatus Status,
    HostProcessToolsOsMarkerEvidence? Marker);

/// <summary>The dedicated operating-system taint slot.</summary>
/// <remarks>
/// Its own slot rather than a field beside an existing secret: a database reset, a restore, and an
/// API-key rotation all leave it alone, which is the entire reason a second marker exists. Delete is
/// compare-and-delete only, so no operation can remove evidence it does not own.
/// </remarks>
internal interface IHostProcessToolsMarkerStore
{

    HostProcessToolsMarkerReadResult Read();

    HostProcessToolsMarkerWriteStatus Write(
        string installationIdentity,
        Guid transitionId,
        ulong taintMasterKeyVersion,
        CovenantDigest taintFingerprint);

}

/// <summary>The trusted process facts the transition validates before doing anything.</summary>
internal sealed record HostProcessToolsTransitionEnvironment(
    ArcanumEdition Edition,
    bool EscapeHatchOptIn,
    bool CovenantOpenedInThisProcess);

/// <summary>Reads the edition, opt-in, and Covenant-residue facts from trusted process state.</summary>
/// <remarks>
/// A seam so the transition can be tested without mutating real process environment variables, and
/// so the values can only ever come from configuration this process already resolved rather than
/// from a request.
/// </remarks>
internal interface IHostProcessToolsEnvironmentProbe
{

    HostProcessToolsTransitionEnvironment Read();

}

/// <summary>Proves the host is stopped by taking the installation lock for the whole transition.</summary>
internal interface IHostProcessToolsInstallationLockSource
{

    IDisposable? TryAcquire();

}
