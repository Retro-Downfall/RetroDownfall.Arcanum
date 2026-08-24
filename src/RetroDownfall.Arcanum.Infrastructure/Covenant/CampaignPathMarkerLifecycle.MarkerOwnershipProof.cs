using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

internal sealed partial class CampaignPathMarkerLifecycle
{

    /// <summary>
    /// Authenticates one marker and proves that its root tuple binds the same retained root handle.
    /// </summary>
    private async Task<Result<MarkerOwnershipEvidence>> ProveMarkerOwnershipAsync(
        CampaignPathMarkerRootAuthority authority,
        Guid campaignId,
        long pathRevision,
        CancellationToken cancellationToken)
    {

        Result<PhysicalCampaignMarkerOpenResult> opened =
            await authority.OpenMarkerOrProveAbsentNoFollowAsync(cancellationToken)
                .ConfigureAwait(false);

        if (opened.IsFailure
            || opened.Value is not PhysicalCampaignMarkerOpenResult.Opened present)
        {

            return UnprovenMarkerOwnership();

        }

        await using PhysicalCampaignRootOpener.MarkerHandleCapability marker = present.Marker;

        Result<PhysicalCampaignRootOpener.MarkerCodecBytesLease> read =
            await marker.ReadAllBoundedAsync(
                _codec.MaximumMarkerByteCount,
                cancellationToken).ConfigureAwait(false);

        if (read.IsFailure)
        {

            return UnprovenMarkerOwnership();

        }

        using PhysicalCampaignRootOpener.MarkerCodecBytesLease lease = read.Value;

        return ProveOpenMarkerOwnership(
            authority,
            marker,
            lease.Bytes.Span,
            campaignId,
            pathRevision);

    }

    /// <summary>
    /// The same proof, from bytes already read through a handle the caller still holds.
    /// </summary>
    /// <remarks>
    /// Split out so a compare-delete can prove ownership and then delete <em>through the very handle
    /// it proved</em>. Reopening the marker between the proof and the delete would put a gap in the
    /// middle of a same-handle guarantee, and a byte-identical replacement landing in that gap is
    /// exactly the substitution the whole protocol is built to refuse.
    /// </remarks>
    private Result<MarkerOwnershipEvidence> ProveOpenMarkerOwnership(
        CampaignPathMarkerRootAuthority authority,
        PhysicalCampaignRootOpener.MarkerHandleCapability marker,
        ReadOnlySpan<byte> bytes,
        Guid campaignId,
        long pathRevision)
    {

        Result<CampaignPathMarkerContent> parsed = _codec.Parse(bytes);

        if (parsed.IsFailure
            || parsed.Value.CampaignId != campaignId
            || parsed.Value.PathRevision != pathRevision)
        {

            return UnprovenMarkerOwnership();

        }

        CovenantDigest? claimed = _rootOpener.DeriveClaimedRootIdentityDigest(
            parsed.Value.RootVolumeId,
            parsed.Value.RootFileId);

        if (claimed is not { } claimedIdentity
            || claimedIdentity != authority.PhysicalIdentityDigest)
        {

            return UnprovenMarkerOwnership();

        }

        return new MarkerOwnershipEvidence(
            new CovenantDigest(SHA256.HashData(bytes)),
            marker.PhysicalIdentityDigest,
            parsed.Value.RootVolumeId,
            parsed.Value.RootFileId);

    }

    private static Result<MarkerOwnershipEvidence> UnprovenMarkerOwnership() =>
        Result<MarkerOwnershipEvidence>.Failure(new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "A Campaign marker could not prove ownership of its retained root."));

    private sealed record MarkerOwnershipEvidence(
        CovenantDigest MarkerDigest,
        CovenantDigest MarkerPhysicalIdentityDigest,
        ulong RootVolumeId,
        ulong RootFileId);

}
