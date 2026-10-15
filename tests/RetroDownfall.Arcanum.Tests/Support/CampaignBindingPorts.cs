using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A canonical Campaign resolver that never binds a request: it answers every question with a refusal, which says nothing about which
/// Campaign governs a turn, so a consumer falls through to the registered root that contains the working directory, then to the explicit
/// identifier. It is what a test composes when the scenario is not about the canonical binding.
/// </summary>
internal sealed class RefusingCampaignResolver : ICanonicalCampaignContextResolver
{
    internal static RefusingCampaignResolver Instance { get; } = new();

    public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
        CanonicalCampaignResolutionRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            Result<CanonicalCampaignContext>.Failure(
                new Error(ErrorCodes.Covenant.CampaignBindingConflict, "The test resolver binds no request.")));
}

/// <summary>An installation that has registered no Campaign root: no directory resolves to a physical identity.</summary>
internal sealed class UnregisteredCampaignRoots : ICampaignPathIdentityReader
{
    internal static UnregisteredCampaignRoots Instance { get; } = new();

    public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(
        string? workingDirectory,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(null));

    public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(
        Guid campaignId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(null));
}
