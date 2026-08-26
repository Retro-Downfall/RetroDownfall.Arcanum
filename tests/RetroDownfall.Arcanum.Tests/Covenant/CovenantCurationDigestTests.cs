using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The request digest one curation change is authorized under.
/// </summary>
/// <remarks>
/// A curation change carries no compiled artifact and no lane revision of the entry's own, so it
/// cannot borrow the mutation request preimage: two different requests that hashed alike would let a
/// token issued for one authorize the other.
/// </remarks>
public sealed class CovenantCurationDigestTests
{

    private static readonly Guid SampleMutation = Guid.Parse("0195a0f0-0000-7000-8000-0000000000aa");

    private static readonly Guid SampleCampaign = Guid.Parse("0195a0f0-0000-7000-8000-0000000000bb");

    [Fact]
    public void Every_field_of_a_curation_request_changes_its_digest()
    {

        CurationRequestDigestInput baseline = Baseline();

        CovenantDigest reference = CovenantDigests.CurationRequest(baseline);

        Assert.Equal(reference, CovenantDigests.CurationRequest(Baseline()));

        Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { Kind = CovenantCurationKind.Unpin }));

        Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { MutationId = SampleCampaign }));

        Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { NormalizedKey = new CovenantKey("preference.tests") }));

        Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { Lane = CovenantLane.Proposed }));

        Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { KeyEpoch = 4 }));

        Assert.NotEqual(reference, CovenantDigests.CurationRequest(baseline with { ExpectedRevision = 2 }));

    }

    [Fact]
    public void A_Global_curation_request_does_not_hash_as_its_Campaign_namesake()
    {

        CovenantDigest campaignScoped = CovenantDigests.CurationRequest(Baseline());

        CovenantDigest globalScoped = CovenantDigests.CurationRequest(
            Baseline() with { Scope = CovenantScope.Global, CampaignId = null });

        Assert.NotEqual(campaignScoped, globalScoped);

    }

    [Fact]
    public void A_curation_request_carries_its_own_domain_tag()
    {

        // Domain separation is what stops a curation token authorizing a mutation. Sharing the tag
        // would make the two preimages members of one space, and a collision there is an escalation
        // rather than a coincidence.
        Assert.NotEqual(CovenantDomainTag.Request, CovenantDomainTag.CurationRequest);

        Assert.Contains(CovenantDomainTag.CurationRequest, CovenantPolicyV1Manifest.DomainTags);

        Assert.Equal(
            "Arcanum.Covenant.CurationRequest.v1",
            CovenantPolicyV1Manifest.GetDomainTag(CovenantDomainTag.CurationRequest));

    }

    [Fact]
    public void A_curation_request_naming_a_Campaign_it_is_not_scoped_to_is_refused()
    {

        Assert.Throws<ArgumentException>(
            () => CovenantDigests.CurationRequest(Baseline() with { Scope = CovenantScope.Global }));

        Assert.Throws<ArgumentException>(
            () => CovenantDigests.CurationRequest(Baseline() with { CampaignId = null }));

    }

    private static CurationRequestDigestInput Baseline() =>
        new(
            CovenantCurationKind.Pin,
            SampleMutation,
            CovenantScope.Campaign,
            SampleCampaign,
            new CovenantKey("preference.builds"),
            CovenantLane.Confirmed,
            KeyEpoch: 3,
            ExpectedRevision: 1);

}
