using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A Campaign root enricher that returns the context it is given. For fixtures whose scripted resolver already answers the exact context
/// the turn runs under, so the registered root is not a variable of the test.
/// </summary>
internal sealed class PassThroughRootEnricher : ICanonicalCampaignRootEnricher
{
    public ValueTask<Result<CanonicalCampaignContext>> EnrichAsync(CanonicalCampaignContext resolved, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(resolved));
}
