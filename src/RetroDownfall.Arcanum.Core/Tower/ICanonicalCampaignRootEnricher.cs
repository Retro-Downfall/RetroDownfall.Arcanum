using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>
/// Adds the registered physical root of a Campaign to a context that
/// <see cref="ICanonicalCampaignContextResolver"/> already resolved, for Long Rest requests and their fences only.
/// </summary>
/// <remarks>
/// Long Rest compares the live registered root of a Campaign with the root facts the context it was asked about carries, so a context
/// that resolved without a working directory (a Session bound to a Campaign, or the sweep endpoint, which names only a Campaign) has to
/// gain those facts before Long Rest can use it. Every other caller keeps exactly what the resolver returned. Enrichment therefore
/// happens only while Long Rest is enabled: with the feature off an implementation returns the context unchanged and reads no registry.
/// </remarks>
public interface ICanonicalCampaignRootEnricher
{
    /// <summary>
    /// The same context carrying its Campaign's registered root, or the context unchanged when Long Rest is off, the context is
    /// Global-only, or the Campaign has no registered root and none was observed. A root that moved or vanished since the resolver
    /// observed it, or that cannot be trusted, is a stale snapshot.
    /// </summary>
    ValueTask<Result<CanonicalCampaignContext>> EnrichAsync(
        CanonicalCampaignContext resolved,
        CancellationToken cancellationToken);
}
