using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

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

        // The single no-follow resolution of the recorded path. It answers "what is there", never "may
        // this be worked on": a symlink, a file, an absent directory, and an unavailable identity key
        // all report nothing at all rather than a candidate to be argued with.
        if (_rootOpener.IdentifyExact(row.TargetDisplayPath) is not { } reopenedIdentity)
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
                row.TargetDisplayPath,
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
        if (_retainedRoots.TryRemove(row.IntentId, out RetainedRoot? displaced))
        {

            await displaced.Authority.DisposeAsync().ConfigureAwait(false);

        }

        _retainedRoots[row.IntentId] = retained;

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

        Result<PhysicalCampaignMarkerOpenResult> opened =
            await authority.OpenMarkerOrProveAbsentNoFollowAsync(cancellationToken)
                .ConfigureAwait(false);

        if (opened.IsFailure)
        {

            return UnprovenRestartRoot(
                "The marker inside the reopened Campaign root could not be opened.");

        }

        // Absence is a completed delete only for the process that still holds the root it opened.
        // Here it is the absence of the one piece of evidence that could identify this directory, and
        // recording a cleanup against a root nobody identified is not a cheaper answer than an
        // operator looking at it.
        if (opened.Value is PhysicalCampaignMarkerOpenResult.Absent)
        {

            return UnprovenRestartRoot(
                "The reopened Campaign root carries no marker, so nothing proves it is this child's root.");

        }

        PhysicalCampaignRootOpener.MarkerHandleCapability marker =
            ((PhysicalCampaignMarkerOpenResult.Opened)opened.Value).Marker;

        await using (marker.ConfigureAwait(false))
        {

            Result<PhysicalCampaignRootOpener.MarkerCodecBytesLease> read =
                await marker.ReadAllBoundedAsync(
                    _codec.MaximumMarkerByteCount,
                    cancellationToken).ConfigureAwait(false);

            if (read.IsFailure)
            {

                return UnprovenRestartRoot(
                    "The marker inside the reopened Campaign root could not be read within its bound.");

            }

            using PhysicalCampaignRootOpener.MarkerCodecBytesLease lease = read.Value;

            // The shared codec, so this arm accepts exactly what registration produces and authenticates
            // the tag before a single field is believed.
            Result<CampaignPathMarkerContent> parsed = _codec.Parse(lease.Bytes.Span);

            if (parsed.IsFailure)
            {

                return UnprovenRestartRoot(
                    "The marker inside the reopened Campaign root did not authenticate.");

            }

            if (parsed.Value.CampaignId != row.CampaignId
                || parsed.Value.PathRevision != row.PriorRevision)
            {

                return UnprovenRestartRoot(
                    "The reopened Campaign root carries a marker that names different ownership.");

            }

            CovenantDigest? claimed = _rootOpener.DeriveClaimedRootIdentityDigest(
                parsed.Value.RootVolumeId,
                parsed.Value.RootFileId);

            // The whole proof, in one equality: the marker asserting it still lives in the directory
            // it was written into, checked against what that directory says it is.
            return claimed is { } claimedIdentity
                && claimedIdentity == authority.PhysicalIdentityDigest
                    ? Result.Success()
                    : UnprovenRestartRoot(
                        "The reopened Campaign root is not the root its own marker binds itself to.");

        }

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
