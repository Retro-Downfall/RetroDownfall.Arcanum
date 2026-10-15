using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The order in which the Campaign whose Sanctum governs a turn is found: the canonical Campaign first, then the registered root that
/// contains the working directory, then, only when no canonical resolution answered, the explicit identifier.
/// </summary>
public sealed class SanctumCampaignResolutionTests
{
    private static readonly Guid Bound = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    private static readonly Guid Contained = Guid.Parse("1A2B3C4D-5E6F-4071-8293-A4B5C6D7E8F9");

    private static readonly Guid Named = Guid.Parse("3F2E1D0C-9B8A-4756-8493-A2B1C0D9E8F7");

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_known_canonical_campaign_is_the_answer_and_nothing_else_is_asked()
    {
        Counting campaigns = new(Contained);

        Resolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalFor(Named)));

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            Guid.NewGuid(), Named, "/work/app", CanonicalFor(Bound), resolver, campaigns, Token);

        Assert.Equal(Bound, resolved.CampaignId);

        Assert.Null(resolved.Loaded);

        Assert.Equal(0, resolver.Calls);

        Assert.Equal(0, campaigns.ContainedLookups);
    }

    [Fact]
    public async Task A_known_global_only_context_falls_to_the_registered_root_that_contains_the_directory_and_never_to_the_explicit_identifier()
    {
        Counting campaigns = new(Contained);

        SanctumCampaignResolution.Resolved containing = await SanctumCampaignResolution.ResolveAsync(
            null, Named, "/work/app/src", CanonicalCampaignContext.GlobalOnly, RefusingCampaignResolver.Instance, campaigns, Token);

        Assert.Equal(Contained, containing.CampaignId);

        Assert.NotNull(containing.Loaded);

        SanctumCampaignResolution.Resolved nothing = await SanctumCampaignResolution.ResolveAsync(
            null, Named, null, CanonicalCampaignContext.GlobalOnly, RefusingCampaignResolver.Instance, new Counting(null), Token);

        // The canonical answer was "no Campaign", so the named identifier is not substituted for it.
        Assert.Null(nothing.CampaignId);
    }

    [Fact]
    public async Task An_uninitialised_context_counts_as_no_answer()
    {
        Resolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalFor(Bound)));

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, null, "/work/app", default(CanonicalCampaignContext), resolver, new Counting(null), Token);

        Assert.Equal(Bound, resolved.CampaignId);

        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task A_resolver_answer_is_asked_with_exactly_the_sources_the_request_carries()
    {
        Resolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly));

        Guid session = Guid.NewGuid();

        _ = await SanctumCampaignResolution.ResolveAsync(session, Named, "  ", null, resolver, new Counting(null), Token);

        CanonicalCampaignResolutionRequest asked = Assert.IsType<CanonicalCampaignResolutionRequest>(resolver.LastRequest);

        Assert.Equal(session, asked.SessionId);

        Assert.Equal(Named, asked.ExplicitCampaignId);

        Assert.Null(asked.WorkingDirectory);
    }

    [Fact]
    public async Task A_refused_resolution_is_not_an_answer_so_the_contained_root_and_then_the_explicit_identifier_still_apply()
    {
        Resolver refused = new(Result<CanonicalCampaignContext>.Failure(new Error(ErrorCodes.Covenant.CampaignBindingConflict, "conflict")));

        SanctumCampaignResolution.Resolved containing = await SanctumCampaignResolution.ResolveAsync(
            null, Named, "/work/app", null, refused, new Counting(Contained), Token);

        Assert.Equal(Contained, containing.CampaignId);

        SanctumCampaignResolution.Resolved explicitOnly = await SanctumCampaignResolution.ResolveAsync(
            null, Named, null, null, refused, new Counting(null), Token);

        Assert.Equal(Named, explicitOnly.CampaignId);
    }

    [Fact]
    public async Task A_request_with_no_sources_consults_nothing()
    {
        Resolver resolver = new(Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly));

        Counting campaigns = new(Contained);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, null, " ", null, resolver, campaigns, Token);

        Assert.Null(resolved.CampaignId);

        Assert.Equal(0, resolver.Calls);

        Assert.Equal(0, campaigns.ContainedLookups);
    }

    private static CanonicalCampaignContext CanonicalFor(Guid campaignId) =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);

    private sealed class Resolver(Result<CanonicalCampaignContext> answer) : ICanonicalCampaignContextResolver
    {
        public int Calls { get; private set; }

        public CanonicalCampaignResolutionRequest? LastRequest { get; private set; }

        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
            CanonicalCampaignResolutionRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;

            LastRequest = request;

            return ValueTask.FromResult(answer);
        }
    }

    /// <summary>A store whose containing-root lookup answers one Campaign, or none, and counts how often it is asked.</summary>
    private sealed class Counting(Guid? contained) : ICampaignRepository
    {
        public int ContainedLookups { get; private set; }

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default)
        {
            ContainedLookups++;

            return Task.FromResult<Campaign?>(
                contained is { } id
                    ? new Campaign { Id = id, Name = "contained", NameLower = "contained", Path = path, Type = WorkspaceType.Campaign }
                    : null);
        }

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
