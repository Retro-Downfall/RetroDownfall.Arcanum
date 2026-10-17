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

/// <summary>Sessions with a known immutable Campaign binding; any other Session does not exist.</summary>
internal sealed class StubSessionBindings : ISessionCampaignBindingReader
{
    private readonly Dictionary<Guid, SessionCampaignBinding> _bindings = [];

    internal StubSessionBindings Bind(Guid sessionId, Guid campaignId)
    {
        _bindings[sessionId] = SessionCampaignBinding.ForCampaign(campaignId);

        return this;
    }

    public ValueTask<Result<SessionCampaignBindingRecord?>> FindAsync(Guid? sessionId, CancellationToken cancellationToken)
    {
        if (sessionId is not { } id)
        {
            return ValueTask.FromResult(Result<SessionCampaignBindingRecord?>.Success(null));
        }

        return ValueTask.FromResult(
            _bindings.TryGetValue(id, out SessionCampaignBinding binding)
                ? Result<SessionCampaignBindingRecord?>.Success(new SessionCampaignBindingRecord(id, binding))
                : Result<SessionCampaignBindingRecord?>.Failure(new Error(ErrorCodes.Session.NotFound, "Session not found.")));
    }
}

/// <summary>Campaigns that exist, at availability generation 1.</summary>
internal sealed class AvailableCampaigns(params Guid[] campaignIds) : ICampaignAvailabilityReader
{
    public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(Guid campaignId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<long?>.Success(campaignIds.Contains(campaignId) ? 1L : null));
}

/// <summary>The production canonical resolver over fake readers, for a test whose scenario is the resolver's own refusal rules.</summary>
internal static class ProductionCampaignResolver
{
    internal static ICanonicalCampaignContextResolver Over(
        StubSessionBindings sessions,
        AvailableCampaigns campaigns,
        ICampaignPathIdentityReader? paths = null) =>
        new RetroDownfall.Arcanum.Api.Tower.CanonicalCampaignContextResolver(
            sessions,
            paths ?? UnregisteredCampaignRoots.Instance,
            campaigns);
}
