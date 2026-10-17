using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>
/// Long Rest-scoped enrichment: adds a Campaign's registered root to an already-resolved context, and only while Long Rest is enabled.
/// </summary>
/// <remarks>
/// The shared <see cref="CanonicalCampaignContextResolver"/> answers the fill-and-verify table and nothing else, so a caller that has no
/// use for the registered root (every caller with Long Rest off) gets exactly the context, and costs exactly the reads, it always did.
/// Long Rest compares its fence against the live registered root, so on its request and fence paths (the Session turn boundary, the
/// sweep endpoint and the turn snapshot's revalidation) this seam supplies what the resolver does not.
///
/// <para>The context itself says whether a working directory was observed: the pure policy puts a workspace's policy version, revision and
/// identity digest into the context only when one resolved to the Campaign, and leaves the revision and digest empty otherwise. So an
/// observed workspace must still equal the registered root, a vanished root is stale only when a workspace claimed it, and a context
/// that was already enriched enriches to itself.</para>
/// </remarks>
internal sealed class CanonicalCampaignRootEnricher(
    ICampaignPathIdentityReader paths,
    IOptionsMonitor<ArcanumSettings> settings) : ICanonicalCampaignRootEnricher
{
    public async ValueTask<Result<CanonicalCampaignContext>> EnrichAsync(
        CanonicalCampaignContext resolved,
        CancellationToken cancellationToken)
    {
        if (!settings.CurrentValue.Features.LongRest || !resolved.IsCampaignBound)
        {
            return resolved;
        }

        Guid campaignId = resolved.CampaignId!.Value;

        Result<RegisteredCampaignIdentity?> registered = await paths.FindByCampaignAsync(
            campaignId, cancellationToken).ConfigureAwait(false);

        if (registered.IsFailure)
        {
            return registered.Error;
        }

        bool workspaceObserved = resolved.PathIdentityRevision is not null || resolved.RootIdentityDigest is not null;

        if (registered.Value is { } root)
        {
            if (root.CampaignId != campaignId || root.PolicyVersion == 0
                || root.Revision <= 0 || !root.PhysicalIdentityDigest.IsValid
                || (workspaceObserved && (resolved.PathIdentityPolicyVersion != root.PolicyVersion
                    || resolved.PathIdentityRevision != root.Revision
                    || resolved.RootIdentityDigest != root.PhysicalIdentityDigest)))
            {
                return new Error(ErrorCodes.Covenant.StaleSnapshot, "The registered Campaign root changed during resolution.");
            }

            return CanonicalCampaignContext.Create(resolved.Binding, resolved.CampaignAvailabilityGeneration,
                root.PolicyVersion, root.Revision, root.PhysicalIdentityDigest);
        }

        return workspaceObserved
            ? new Error(ErrorCodes.Covenant.StaleSnapshot, "The resolved Campaign root is no longer registered.")
            : resolved;
    }
}
