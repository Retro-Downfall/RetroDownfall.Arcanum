using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// A turn that accepted Long Rest carries its Campaign's registered root, so its snapshot revalidates the Campaign through the same
/// enrichment; a snapshot that accepted no Long Rest compares the resolver's answer as it always did and never consults the enricher.
/// </summary>
public sealed class LongRestSnapshotRootEnrichmentTests
{
    private static readonly Guid CampaignId = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    private static readonly CovenantDigest Digest = new(Enumerable.Repeat((byte)0x4A, 32).ToArray());

    private static readonly CanonicalCampaignContext Unobserved = CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(CampaignId), 12, CampaignPathIdentityPolicy.Version, null, null);

    private static readonly CanonicalCampaignContext Enriched = CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(CampaignId), 12, 3, 7, Digest);

    private static readonly CanonicalCampaignContext Moved = CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(CampaignId), 12, 3, 8, Digest);

    private static CampaignRollupTurnSnapshot Snapshot(CanonicalCampaignContext campaign, ICanonicalCampaignRootEnricher? enricher, bool stamped = true)
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = stamped;

        IOptionsMonitor<ArcanumSettings> options = new TestOptionsMonitor<ArcanumSettings>(settings);

        return new(null, campaign, new PingRequest("Continue.", SessionId: Guid.NewGuid()), new CampaignRollupTurnSnapshotTests.ProbeStore(),
            options, new FixedResolver(Unobserved), null, requiresRollups: false,
            longRestStamp: stamped ? new(Guid.NewGuid(), 1, Digest) : null,
            longRestConfiguration: stamped ? new CurrentConfiguration() : null,
            rootEnricher: enricher);
    }

    [Fact]
    public async Task A_stamped_snapshot_revalidates_the_campaign_it_began_with_through_the_enricher()
    {
        ScriptedEnricher enricher = new(Enriched);

        await using CampaignRollupTurnSnapshot snapshot = Snapshot(Enriched, enricher);

        Result result = await snapshot.ValidateAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        // The enricher is handed exactly what the resolver answered.
        Assert.Equal([Unobserved], enricher.Seen);
    }

    [Fact]
    public async Task A_root_that_moved_since_the_turn_began_makes_the_snapshot_stale()
    {
        await using CampaignRollupTurnSnapshot snapshot = Snapshot(Enriched, new ScriptedEnricher(Moved));

        Result result = await snapshot.ValidateAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);
    }

    [Fact]
    public async Task An_enrichment_failure_is_the_revalidation_failure()
    {
        Error failure = new(ErrorCodes.Covenant.IntegrityFailure, "malformed");

        await using CampaignRollupTurnSnapshot snapshot = Snapshot(Enriched, new ScriptedEnricher(failure));

        Result result = await snapshot.ValidateAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);
    }

    [Fact]
    public async Task A_stamped_snapshot_without_an_enricher_cannot_prove_its_campaign_and_is_stale()
    {
        await using CampaignRollupTurnSnapshot snapshot = Snapshot(Enriched, enricher: null);

        Result result = await snapshot.ValidateAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);
    }

    [Fact]
    public async Task A_snapshot_that_accepted_no_Long_Rest_compares_the_resolvers_answer_as_it_is_and_never_consults_the_enricher()
    {
        ScriptedEnricher enricher = new(Moved);

        await using CampaignRollupTurnSnapshot snapshot = Snapshot(Unobserved, enricher, stamped: false);

        Result result = await snapshot.ValidateAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Empty(enricher.Seen);
    }

    private sealed class FixedResolver(CanonicalCampaignContext answer) : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(answer));
    }

    private sealed class ScriptedEnricher : ICanonicalCampaignRootEnricher
    {
        private readonly Result<CanonicalCampaignContext> _answer;

        internal ScriptedEnricher(CanonicalCampaignContext answer) => _answer = answer;

        internal ScriptedEnricher(Error failure) => _answer = Result<CanonicalCampaignContext>.Failure(failure);

        internal List<CanonicalCampaignContext> Seen { get; } = [];

        public ValueTask<Result<CanonicalCampaignContext>> EnrichAsync(CanonicalCampaignContext resolved, CancellationToken cancellationToken)
        {
            Seen.Add(resolved);

            return ValueTask.FromResult(_answer);
        }
    }

    private sealed class CurrentConfiguration : ILongRestConfigurationProvider
    {
        public LongRestSweepConfiguration Capture() => throw new InvalidOperationException("Revalidation reads currentness only.");

        public bool IsCurrent(LongRestConfigurationStamp stamp) => true;
    }
}
