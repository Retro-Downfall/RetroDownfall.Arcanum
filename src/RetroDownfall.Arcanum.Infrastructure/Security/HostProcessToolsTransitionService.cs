using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The single owner of the offline, permanently tainting host-process-tools transition.
/// </summary>
/// <remarks>
/// Enabling the unsandboxed escape hatch gives model-driven code the operator's operating-system
/// identity, so it is not a setting. It is a one-way installation transition that must leave durable
/// evidence in two places that fail independently: the core authority row, which a restore or reset
/// can replace, and a dedicated operating-system secret slot, which they cannot. Neither marker
/// alone may re-open Covenant afterwards (§10.12).
///
/// <para>The ordering is chosen so that every crash boundary is recoverable and no boundary can be
/// resolved in the clean direction. The database is committed to <c>PendingHostToolsTaint</c>
/// <i>before</i> the operating-system slot is touched, so an unattributable marker can never exist;
/// the slot is read back and joined before the row advances to <c>HostToolsTainted</c>, so a tainted
/// row always has a marker behind it. Retrying the same transition identity resumes from whichever
/// phase the durable evidence proves.</para>
///
/// <para>Compensation is available exactly once: when the slot <i>proves</i> it refused the write
/// and a readback proves the slot is absent, this call may return its own pending row to clean. Any
/// uncertainty — a faulted write, an unreadable slot, a lost compare-and-swap — leaves the
/// installation pending and blocked, because a clean answer after a possible escape is the one
/// answer that can never be corrected.</para>
/// </remarks>
internal sealed class HostProcessToolsTransitionService(
    IHostProcessToolsAuthorityStore authority,
    IHostProcessToolsMarkerStore markers,
    IHostProcessToolsEnvironmentProbe environment,
    IHostProcessToolsInstallationLockSource installationLock,
    IHostProcessToolsMarkerPairJoiner joiner,
    HostProcessToolsMarkerMutationGate markerMutationGate) : IHostProcessToolsTransitionService
{

    public async Task<Result<HostProcessToolsTransitionResult>> EnableAsync(
        HostProcessToolsTransitionRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (request.TransitionId == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Validation.InvalidBody,
                "The host-process-tools transition requires a random transition identity.");

        }

        HostProcessToolsTransitionEnvironment probe = environment.Read();

        if (probe.Edition is not ArcanumEdition.Development || !probe.EscapeHatchOptIn)
        {

            return Refused(request, HostProcessToolsTransitionBlocker.EditionOrOptInMissing);

        }

        if (probe.CovenantOpenedInThisProcess)
        {

            // A process that has already decrypted Covenant pages cannot be the one that authorizes
            // arbitrary same-process code: the residue is exactly what the taint exists to prevent.
            return Refused(request, HostProcessToolsTransitionBlocker.CovenantAlreadyOpened);

        }

        using IDisposable? held = installationLock.TryAcquire();

        if (held is null)
        {

            return Refused(request, HostProcessToolsTransitionBlocker.HostRunning);

        }

        // The cross-process installation lock keeps another Arcanum out; this keeps the other
        // in-process mutator of the same slot out. A full installation reset deleting the marker
        // while this transition is between its write and its readback would leave the readback
        // describing a slot neither operation owns, and both would then be reasoning about the
        // other's effect. Held from before the first marker access through the last one, because a
        // gate taken only around the write would still let a delete land inside the readback.
        await using IAsyncDisposable markerLease = await markerMutationGate
            .AcquireExclusiveAsync(cancellationToken)
            .ConfigureAwait(false);

        return await RunUnderInstallationLockAsync(request, cancellationToken).ConfigureAwait(false);

    }

    private async Task<Result<HostProcessToolsTransitionResult>> RunUnderInstallationLockAsync(
        HostProcessToolsTransitionRequest request,
        CancellationToken cancellationToken)
    {

        Result<HostProcessToolsAuthorityRow> read = await authority.ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {

            return Refused(request, HostProcessToolsTransitionBlocker.AuthorityUnreadable);

        }

        HostProcessToolsAuthorityRow row = read.Value;

        return row.State switch
        {
            CovenantHostToolsState.HostToolsTainted =>
                ClassifyCompleted(request, row),

            CovenantHostToolsState.PendingHostToolsTaint =>
                await ResumePendingAsync(request, row, cancellationToken).ConfigureAwait(false),

            _ => await BeginFromCleanAsync(request, row, cancellationToken).ConfigureAwait(false),
        };

    }

    /// <summary>
    /// Resolves a row that already claims the taint.
    /// </summary>
    /// <remarks>
    /// Only the transition that owns the row may see <c>AlreadyCompleted</c>, and only when its
    /// marker still joins. A foreign identity is refused rather than told the state of somebody
    /// else's transition, and a missing or mismatched marker is remediation rather than success.
    /// </remarks>
    private Result<HostProcessToolsTransitionResult> ClassifyCompleted(
        HostProcessToolsTransitionRequest request,
        HostProcessToolsAuthorityRow row)
    {

        if (row.TransitionId != request.TransitionId)
        {

            return Refused(request, HostProcessToolsTransitionBlocker.ForeignTransitionIdentity);

        }

        HostProcessToolsMarkerPairJoinResult join = joiner.Join(row.ToEvidence(), ReadMarker());

        return join.Disposition is HostProcessToolsMarkerPairDisposition.TaintedMatched
            ? Result<HostProcessToolsTransitionResult>.Success(new HostProcessToolsTransitionResult(
                request.TransitionId,
                HostProcessToolsTransitionOutcome.AlreadyCompleted,
                RestartRequired: true))
            : Blocked(request, HostProcessToolsTransitionBlocker.MarkerPairMismatch);

    }

    /// <summary>
    /// Continues a transition whose pending row is already durable.
    /// </summary>
    /// <remarks>
    /// The pending row is the operation's claim on the installation, so a different identity can
    /// never adopt it. From here the marker decides the phase: absent means the write never landed
    /// and may be retried; matching means the write landed and only the final compare-and-swap
    /// remains; anything else is remediation.
    /// </remarks>
    private async Task<Result<HostProcessToolsTransitionResult>> ResumePendingAsync(
        HostProcessToolsTransitionRequest request,
        HostProcessToolsAuthorityRow row,
        CancellationToken cancellationToken)
    {

        if (row.TransitionId != request.TransitionId)
        {

            return Refused(request, HostProcessToolsTransitionBlocker.ForeignTransitionIdentity);

        }

        HostProcessToolsMarkerReadResult existing = markers.Read();

        if (existing.Status is HostProcessToolsMarkerReadStatus.Present)
        {

            HostProcessToolsMarkerPairJoinResult join = joiner.Join(row.ToEvidence(), existing.Marker);

            return join.Disposition is HostProcessToolsMarkerPairDisposition.PendingBlocked
                ? await CommitTaintedAsync(request, row, cancellationToken).ConfigureAwait(false)
                : Blocked(request, HostProcessToolsTransitionBlocker.MarkerPairMismatch);

        }

        if (existing.Status is not HostProcessToolsMarkerReadStatus.Absent)
        {

            return Blocked(request, HostProcessToolsTransitionBlocker.MarkerReadbackMismatch);

        }

        return await WriteMarkerAndCommitAsync(
            request,
            row,
            compensationPermitted: false,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Starts a transition from a clean installation.
    /// </summary>
    /// <remarks>
    /// The inventory is the substantive precondition: the escape hatch must not be enabled while any
    /// Covenant canonical row or protected artifact remains, because that content would then be
    /// readable by arbitrary same-identity code. A stray marker beside a clean row is remediation,
    /// never a fresh start — it is precisely the shape a restore into a clean installation produces.
    /// </remarks>
    private async Task<Result<HostProcessToolsTransitionResult>> BeginFromCleanAsync(
        HostProcessToolsTransitionRequest request,
        HostProcessToolsAuthorityRow row,
        CancellationToken cancellationToken)
    {

        HostProcessToolsMarkerReadResult existing = markers.Read();

        if (existing.Status is not HostProcessToolsMarkerReadStatus.Absent)
        {

            return Blocked(request, HostProcessToolsTransitionBlocker.MarkerPairMismatch);

        }

        Result<HostProcessToolsProtectedInventory> inventory = await authority
            .InventoryProtectedStateAsync(cancellationToken)
            .ConfigureAwait(false);

        if (inventory.IsFailure)
        {

            return Refused(request, HostProcessToolsTransitionBlocker.AuthorityUnreadable);

        }

        if (!inventory.Value.IsEmpty)
        {

            return Refused(request, HostProcessToolsTransitionBlocker.ProtectedStatePresent);

        }

        Result pending = await authority
            .CommitPendingAsync(row, request.TransitionId, cancellationToken)
            .ConfigureAwait(false);

        if (pending.IsFailure)
        {

            return Refused(request, HostProcessToolsTransitionBlocker.AuthorityCommitFailed);

        }

        Result<HostProcessToolsAuthorityRow> reread = await authority.ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reread.IsFailure)
        {

            return Blocked(request, HostProcessToolsTransitionBlocker.AuthorityUnreadable);

        }

        return await WriteMarkerAndCommitAsync(
            request,
            reread.Value,
            compensationPermitted: true,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Writes the operating-system marker, proves it, and advances the durable row.
    /// </summary>
    /// <param name="compensationPermitted">
    /// True only for the call that created the pending row and has not yet attempted a write, which
    /// is the one situation where returning to clean discards no evidence.
    /// </param>
    private async Task<Result<HostProcessToolsTransitionResult>> WriteMarkerAndCommitAsync(
        HostProcessToolsTransitionRequest request,
        HostProcessToolsAuthorityRow row,
        bool compensationPermitted,
        CancellationToken cancellationToken)
    {

        if (row.TaintMasterKeyVersion is not { } taintVersion || row.TaintFingerprint is not { } taintFingerprint)
        {

            return Blocked(request, HostProcessToolsTransitionBlocker.AuthorityUnreadable);

        }

        HostProcessToolsMarkerWriteStatus written = markers.Write(
            row.InstallationIdentity,
            request.TransitionId,
            taintVersion,
            taintFingerprint);

        if (written is HostProcessToolsMarkerWriteStatus.Uncertain)
        {

            Log.Error(
                "The host-process-tools marker write did not prove its outcome; the installation stays pending and blocked.");

            return Blocked(request, HostProcessToolsTransitionBlocker.MarkerWriteUncertain);

        }

        if (written is HostProcessToolsMarkerWriteStatus.Refused)
        {

            return await CompensateRefusedWriteAsync(
                request,
                row,
                compensationPermitted,
                cancellationToken).ConfigureAwait(false);

        }

        HostProcessToolsMarkerReadResult readback = markers.Read();

        if (readback.Status is not HostProcessToolsMarkerReadStatus.Present)
        {

            return Blocked(request, HostProcessToolsTransitionBlocker.MarkerReadbackMismatch);

        }

        HostProcessToolsMarkerPairJoinResult join = joiner.Join(row.ToEvidence(), readback.Marker);

        return join.Disposition is HostProcessToolsMarkerPairDisposition.PendingBlocked
            ? await CommitTaintedAsync(request, row, cancellationToken).ConfigureAwait(false)
            : Blocked(request, HostProcessToolsTransitionBlocker.MarkerReadbackMismatch);

    }

    /// <summary>
    /// Returns a just-created pending row to clean, but only against a slot proven empty.
    /// </summary>
    private async Task<Result<HostProcessToolsTransitionResult>> CompensateRefusedWriteAsync(
        HostProcessToolsTransitionRequest request,
        HostProcessToolsAuthorityRow row,
        bool compensationPermitted,
        CancellationToken cancellationToken)
    {

        if (!compensationPermitted || markers.Read().Status is not HostProcessToolsMarkerReadStatus.Absent)
        {

            return Blocked(request, HostProcessToolsTransitionBlocker.MarkerWriteRefused);

        }

        Result compensated = await authority
            .CompensateToCleanAsync(row, request.TransitionId, cancellationToken)
            .ConfigureAwait(false);

        return compensated.IsSuccess
            ? Refused(request, HostProcessToolsTransitionBlocker.MarkerWriteRefused)
            : Blocked(request, HostProcessToolsTransitionBlocker.MarkerWriteRefused);

    }

    private async Task<Result<HostProcessToolsTransitionResult>> CommitTaintedAsync(
        HostProcessToolsTransitionRequest request,
        HostProcessToolsAuthorityRow row,
        CancellationToken cancellationToken)
    {

        Result committed = await authority
            .CommitTaintedAsync(row, request.TransitionId, cancellationToken)
            .ConfigureAwait(false);

        if (committed.IsFailure)
        {

            Log.Error(
                "The host-process-tools taint could not be committed after its marker was written; the installation stays pending and blocked.");

            return Blocked(request, HostProcessToolsTransitionBlocker.AuthorityCommitFailed);

        }

        Log.Warning(
            "This installation is now permanently marked as host-process-tools tainted; Covenant authority will never be available again on it.");

        return Result<HostProcessToolsTransitionResult>.Success(new HostProcessToolsTransitionResult(
            request.TransitionId,
            HostProcessToolsTransitionOutcome.Completed,
            RestartRequired: true));

    }

    private HostProcessToolsOsMarkerEvidence? ReadMarker() =>
        markers.Read() is { Status: HostProcessToolsMarkerReadStatus.Present, Marker: { } marker }
            ? marker
            : null;

    private static Result<HostProcessToolsTransitionResult> Refused(
        HostProcessToolsTransitionRequest request,
        HostProcessToolsTransitionBlocker blocker) =>
        Result<HostProcessToolsTransitionResult>.Success(new HostProcessToolsTransitionResult(
            request.TransitionId,
            HostProcessToolsTransitionOutcome.Refused,
            RestartRequired: false,
            blocker));

    private static Result<HostProcessToolsTransitionResult> Blocked(
        HostProcessToolsTransitionRequest request,
        HostProcessToolsTransitionBlocker blocker) =>
        Result<HostProcessToolsTransitionResult>.Success(new HostProcessToolsTransitionResult(
            request.TransitionId,
            HostProcessToolsTransitionOutcome.PendingManualRemediation,
            RestartRequired: false,
            blocker));

}
