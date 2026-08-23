using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one arm that reopens a Campaign root by name, for a process that retained none.
/// </summary>
/// <remarks>
/// Separate from the retained-root half on purpose, and the separation is the invariant rather than a
/// filing preference: the in-process path physically cannot resolve a display path, because the code
/// that can lives here and here is reached only when no root was retained.
/// <c>CampaignPathMarkerRootProofCallSiteTests</c> asserts exactly that over production source.
///
/// <para>Reopening by name is normally the thing the marker protocol refuses, because a name the
/// operating system resolves is not authority (§10.12). What makes it admissible here is that the name
/// grants nothing on its own: the reopened directory is believed only after the marker living inside it
/// derives that directory's exact identity from the volume and file identifiers the marker binds itself
/// to. A marker copied into a second directory therefore proves nothing there — its own contents
/// contradict where it now lives — and a directory that swapped underneath the recorded path answers
/// with an identity no authentic marker can name.</para>
/// </remarks>
internal sealed partial class CampaignPathMarkerLifecycle
{

    /// <summary>
    /// Reopens the recorded display path and proves it is the root this child's marker was written
    /// into, or keeps admission closed.
    /// </summary>
    /// <remarks>
    /// Retains the proven root on success, so the phases that follow run through exactly the same
    /// capability the in-process arm uses and the one release at finalization covers both arms.
    /// </remarks>
    private async Task<Result<RetainedRoot>> ReopenAndProveRootAsync(
        CovenantExclusiveRecoveryOwner owner,
        CampaignPathMarkerIntentRow row,
        CancellationToken cancellationToken)
    {

        // The column is nullable only for a full installation reset cleanup child, and this arm is
        // reached by the legacy kinds alone. Proving it here rather than trusting the table means a
        // malformed row cannot reach filesystem authority with a manufactured path, and the narrowed
        // local below is the only value the seams ever see.
        if (row.TargetDisplayPath is not { } recordedDisplayPath)
        {

            return UnprovenRestartRoot(
                "The Campaign path marker intent records no display path to reopen.");

        }

        // The single no-follow resolution of the recorded path. It answers "what is there", never "may
        // this be worked on": a symlink, a file, an absent directory, and an unavailable identity key
        // all report nothing at all rather than a candidate to be argued with.
        if (_rootOpener.IdentifyExact(recordedDisplayPath) is not { } reopenedIdentity)
        {

            return UnprovenRestartRoot(
                "The Campaign root this restore recorded could not be reopened and identified.");

        }

        Result<CampaignPathMarkerRootAuthority> opened =
            await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                _rootOpener,
                row.CampaignId,
                row.PriorRevision,
                reopenedIdentity,
                recordedDisplayPath,
                cancellationToken).ConfigureAwait(false);

        // The open re-derives the identity from the handle it actually obtained and refuses anything
        // that disagrees, so a directory replaced between the resolution above and this line fails here
        // rather than being adopted. It is the ordinary producer, which means it also creates the fixed
        // private marker leaf when that directory has none — an owner-only empty directory, in the one
        // case where the proof below then refuses for want of a marker to read.
        if (opened.IsFailure)
        {

            return UnprovenRestartRoot(
                "The Campaign root this restore recorded could not be opened under its own identity.");

        }

        CampaignPathMarkerRootAuthority authority = opened.Value;

        Result proven = await ProveMarkerBindsThisRootAsync(authority, row, cancellationToken)
            .ConfigureAwait(false);

        if (proven.IsFailure)
        {

            await authority.DisposeAsync().ConfigureAwait(false);

            return proven.Error;

        }

        RetainedRoot retained = new(owner.OperationId, authority);

        // Anything already parked under this child belongs to an owner the row has just disproved, and
        // dropping it without disposal would leak the descriptors it holds.
        if (!TryRetainRoot(row.IntentId, retained, out RetainedRoot? displaced))
        {

            await authority.DisposeAsync().ConfigureAwait(false);

            return UnprovenRestartRoot(
                "Campaign marker lifecycle authority is no longer available.");

        }

        if (displaced is not null)
        {

            await CampaignPathRetainedRootRelease.DisposeAllAsync(
                [displaced.Authority]).ConfigureAwait(false);

        }

        return retained;

    }

    /// <summary>
    /// Requires the marker inside the reopened root to name this child and to derive this root.
    /// </summary>
    /// <remarks>
    /// Reads the marker for evidence only. The byte-for-byte comparison against the committed digest
    /// stays where it was — in the effect that deletes the file — so there is exactly one place that
    /// decides a marker may be removed, and this one decides only whose directory it is.
    /// </remarks>
    private async Task<Result> ProveMarkerBindsThisRootAsync(
        CampaignPathMarkerRootAuthority authority,
        CampaignPathMarkerIntentRow row,
        CancellationToken cancellationToken)
    {

        Result<MarkerOwnershipEvidence> proof = await ProveMarkerOwnershipAsync(
            authority,
            row.CampaignId,
            row.PriorRevision,
            cancellationToken).ConfigureAwait(false);

        return proof.IsSuccess
            ? Result.Success()
            : UnprovenRestartRoot(
                "The marker inside the reopened Campaign root did not prove its ownership.");

    }

    /// <summary>
    /// One typed blocker for every way the restart proof can fail to identify a root.
    /// </summary>
    /// <remarks>
    /// The same code for all of them, so nothing downstream can branch on which check refused and
    /// treat one reason as softer than another: an unproven root is not authority to delete a file
    /// whichever way it went unproven, and admission stays closed in every case. The message differs
    /// because the reader is the operator who now has to look at the directory, and it names no path,
    /// identity, or Campaign — only what could not be established.
    /// </remarks>
    private static Error UnprovenRestartRoot(string message) =>
        new(ErrorCodes.Covenant.ManualRecoveryRequired, message);

}
