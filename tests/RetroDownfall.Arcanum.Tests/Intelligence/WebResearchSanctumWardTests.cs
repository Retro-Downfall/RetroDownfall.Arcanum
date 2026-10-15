using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The per-hop egress ward research publishes for every citation it fetches follows the same Sanctum rule as the read_url tool: a target
/// outside the allowed domains is a recorded breach in both modes, and is refused only in Strict mode.
/// </summary>
public sealed class WebResearchSanctumWardTests : IDisposable
{
    private const string Citation = "https://off-list.test/page";

    private const string AllowedCitation = "https://allowed.test/page";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanum-research-ward-" + Guid.NewGuid().ToString("N"));

    private SanctumPipelineHarness Harness(SanctumMode mode, bool enabled = true)
    {
        SanctumPipelineHarness harness = SanctumPipelineHarness.Create(_root, ["allowed.test"]);

        CampaignRepository.SetSanctumConfig(harness.Campaign, new SanctumConfig
        {
            Enabled = enabled,
            Mode = mode,
            EnforcePathBoundary = true,
            NetworkPolicy = NetworkPolicy.AllowList,
            AllowedDomains = ["allowed.test"],
        });

        return harness;
    }

    private static WebResearchWorkflowService Service(SanctumPipelineHarness harness) => new(
        providers: null!,
        new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
        intelligence: null!,
        sessions: null!,
        attachments: null!,
        new SingleCampaignRepository(harness.Campaign),
        harness.Guard);

    [Theory]
    [InlineData(SanctumMode.Strict, false)]
    [InlineData(SanctumMode.AuditOnly, true)]
    public async Task A_citation_outside_the_allowed_domains_is_recorded_in_both_modes_and_fetched_only_in_audit_only(SanctumMode mode, bool fetched)
    {
        SanctumPipelineHarness harness = Harness(mode);

        Func<Uri, CancellationToken, ValueTask<bool>> ward =
            (await Service(harness).BuildCampaignEgressWardAsync(harness.Campaign.Id, CancellationToken.None))!;

        Assert.Equal(fetched, await ward(new Uri(Citation), CancellationToken.None));

        SanctumBreachRecord breach = Assert.Single(harness.Breaches.Records);

        Assert.Equal("NetworkEgress", breach.BreachType);

        Assert.Equal("web_research", breach.ToolName);

        Assert.Equal(harness.CampaignId, breach.CampaignId);

        Assert.Equal(Citation, breach.Details!.RequestedUrl);
    }

    [Theory]
    [InlineData(SanctumMode.Strict)]
    [InlineData(SanctumMode.AuditOnly)]
    public async Task A_citation_inside_the_allowed_domains_is_fetched_without_a_breach_in_either_mode(SanctumMode mode)
    {
        SanctumPipelineHarness harness = Harness(mode);

        Func<Uri, CancellationToken, ValueTask<bool>> ward =
            (await Service(harness).BuildCampaignEgressWardAsync(harness.Campaign.Id, CancellationToken.None))!;

        Assert.True(await ward(new Uri(AllowedCitation), CancellationToken.None));

        Assert.Empty(harness.Breaches.Records);
    }

    [Fact]
    public async Task A_campaign_whose_sanctum_is_off_records_nothing_and_a_run_without_a_campaign_has_no_ward()
    {
        SanctumPipelineHarness harness = Harness(SanctumMode.Strict, enabled: false);

        WebResearchWorkflowService service = Service(harness);

        Func<Uri, CancellationToken, ValueTask<bool>> ward = (await service.BuildCampaignEgressWardAsync(harness.Campaign.Id, CancellationToken.None))!;

        Assert.True(await ward(new Uri(Citation), CancellationToken.None));

        Assert.Empty(harness.Breaches.Records);

        // An uncontained run has no ward at all: redirects are bounded by the address guard alone, as before.
        Assert.Null(await service.BuildCampaignEgressWardAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task A_campaign_that_cannot_be_read_stays_strict_whatever_mode_it_would_have_had()
    {
        SanctumPipelineHarness harness = Harness(SanctumMode.AuditOnly);

        Func<Uri, CancellationToken, ValueTask<bool>> ward =
            (await Service(harness).BuildCampaignEgressWardAsync(Guid.NewGuid(), CancellationToken.None))!;

        // The guard cannot resolve the campaign and denies; no audit-only decision can be honoured for it.
        Assert.False(await ward(new Uri(Citation), CancellationToken.None));
    }

    [Theory]
    [InlineData(true, SanctumMode.Strict, true)]
    [InlineData(true, SanctumMode.AuditOnly, true)]
    [InlineData(false, SanctumMode.Strict, false)]
    [InlineData(false, SanctumMode.AuditOnly, true)]
    public void The_one_rule_lets_an_operation_continue_when_it_was_allowed_or_the_sanctum_only_audits(bool allowed, SanctumMode mode, bool continues) =>
        Assert.Equal(continues, SanctumEnforcement.AllowsContinuation(new SanctumResult { Allowed = allowed }, mode));

    public void Dispose() =>
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(WebResearchSanctumWardTests));
}
