using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

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

        Result<CampaignPathMarkerContent> parsed = _codec.Parse(lease.Bytes.Span);

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
            new CovenantDigest(SHA256.HashData(lease.Bytes.Span)),
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
