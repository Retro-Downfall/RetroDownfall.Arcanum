using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantOperationalDefaultTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Confirmed_scalar_produces_typed_default_with_exact_version_and_scope()
    {
        CovenantSnapshotCandidate candidate = Candidate("defaults.inference.temperature", "0.25");

        CovenantTurnPlan plan = Link(candidate);

        CovenantOperationalDefault value = Assert.Single(plan.OperationalDefaults);

        Assert.Equal(CovenantOperationalDefaultKind.Temperature, value.Kind);
        Assert.Equal(CovenantOperationalDefaultConsumer.InferenceSampling, value.Consumer);
        Assert.Equal(0.25f, value.Value);
        Assert.Equal(1u, value.PolicyVersion);
        Assert.Equal(candidate.EntryId, value.EntryId);
        Assert.Equal(candidate.VersionId, value.VersionId);
        Assert.Equal(candidate.Revision, value.Revision);
        Assert.Equal(CovenantScope.Global, value.Scope);
        Assert.Null(value.CampaignId);
    }

    [Fact]
    public void Campaign_confirmed_shadows_global_and_proposed_never_becomes_an_operational_default()
    {
        CovenantSnapshotCandidate global = Candidate("defaults.inference.temperature", "1");
        CovenantSnapshotCandidate campaign = Candidate("defaults.inference.temperature", "0.5", scope: CovenantScope.Campaign, index: 2);
        CovenantSnapshotCandidate proposed = Candidate("defaults.inference.top-p", "0.9", scope: CovenantScope.Campaign, lane: CovenantLane.Proposed, index: 3);

        CovenantTurnPlan plan = Link(global, campaign, proposed);

        CovenantOperationalDefault value = Assert.Single(plan.OperationalDefaults);

        Assert.Equal(campaign.VersionId, value.VersionId);
        Assert.Equal(CovenantScope.Campaign, value.Scope);
        Assert.Equal(CovenantTask6Fixture.CampaignId, value.CampaignId);
        Assert.Equal(0.5f, value.Value);
    }

    [Fact]
    public void Agent_approved_confirmed_default_keeps_operator_confirmation_semantics()
    {
        CovenantSnapshotCandidate candidate = Candidate("defaults.inference.top-p", "0.8", scope: CovenantScope.Campaign, origin: CovenantOrigin.AgentApproved);

        CovenantOperationalDefault value = Assert.Single(Link(candidate).OperationalDefaults);

        Assert.Equal(CovenantOperationalDefaultKind.TopP, value.Kind);
        Assert.Equal(0.8f, value.Value);
    }

    [Fact]
    public void Masked_global_and_another_campaign_cannot_supply_defaults()
    {
        CovenantSnapshotCandidate global = Candidate("defaults.inference.temperature", "1");
        CovenantSnapshotCandidate foreign = Candidate("defaults.inference.top-p", "0.9", scope: CovenantScope.Campaign, campaignId: Guid.NewGuid(), index: 2);
        CovenantTurnSnapshot snapshot = new(new CovenantGenerationId(CovenantTask6Fixture.DatasetGeneration), 1, CovenantTask6Fixture.CampaignId, 4, [global, foreign], ["defaults.inference.temperature"]);

        CovenantTurnPlan plan = new CovenantLinker().Link(snapshot).Value;

        Assert.Empty(plan.OperationalDefaults);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Malformed_in_scope_confirmed_default_fails_before_masking_or_shadowing(bool shadowed)
    {
        const string key = "defaults.inference.temperature";
        byte[] fragment = Encoding.UTF8.GetBytes("- defaults.inference.temperature: \"garbage\"\n");
        CovenantSnapshotCandidate malformed = new(
            1,
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            new CovenantKey(key),
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            1,
            null,
            1,
            1,
            CovenantCompiler.HashAuthored(key, "garbage"u8),
            CovenantCompiler.HashFragment(key, fragment),
            0,
            CovenantTask6Fixture.D(3),
            [.. fragment],
            CovenantSnapshotCandidateIntegrity.Verified);
        CovenantSnapshotCandidate campaign = Candidate(key, "0.5", scope: CovenantScope.Campaign, index: 2);
        CovenantTurnSnapshot snapshot = new(
            new CovenantGenerationId(CovenantTask6Fixture.DatasetGeneration),
            1,
            CovenantTask6Fixture.CampaignId,
            4,
            shadowed ? [malformed, campaign] : [malformed],
            shadowed ? [] : [key]);

        var result = new CovenantLinker().Link(snapshot);

        Assert.True(result.IsFailure);
        Assert.Equal("Covenant.IntegrityFailure", result.Error.Code);
    }

    [Theory]
    [InlineData(CovenantLane.Confirmed)]
    [InlineData(CovenantLane.Proposed)]
    public void Malformed_foreign_campaign_or_proposed_default_does_not_enter_authoritative_validation(CovenantLane lane)
    {
        Guid campaignId = lane == CovenantLane.Confirmed ? Guid.NewGuid() : CovenantTask6Fixture.CampaignId;
        CovenantSnapshotCandidate candidate = Candidate(
            "defaults.inference.temperature",
            "unused",
            scope: CovenantScope.Campaign,
            lane: lane,
            campaignId: campaignId,
            fragment: "- defaults.inference.temperature: \"garbage\"\n");

        CovenantTurnPlan plan = Link(candidate);

        Assert.Empty(plan.OperationalDefaults);
    }

    [Theory]
    [InlineData("defaults.inference.temperature", "- defaults.inference.temperature: \"0.5\" extra\n")]
    [InlineData("defaults.inference.temperature", "- defaults.inference.top-p: \"0.5\"\n")]
    [InlineData("defaults.inference.temperature", "- defaults.inference.temperature: 0.5\n")]
    [InlineData("defaults.inference.temperature", "- defaults.inference.temperature: \" 0.5\"\n")]
    [InlineData("defaults.inference.temperature", "- defaults.inference.temperature: \"2.0000000000000000000001\"\n")]
    [InlineData("defaults.inference.temperature", "- defaults.inference.temperature: \"NaN\"\n")]
    [InlineData("defaults.egress", "- defaults.egress: \"1\"\n")]
    public void Confirmed_reserved_fragment_is_validated_as_a_complete_canonical_scalar(string key, string fragment)
    {
        CovenantSnapshotCandidate candidate = Candidate(key, "unused", fragment: fragment);

        var result = new CovenantLinker().Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, candidate));

        Assert.True(result.IsFailure);
        Assert.Equal("Covenant.IntegrityFailure", result.Error.Code);
    }

    [Fact]
    public void Invalid_proposed_default_remains_data_without_operational_authority()
    {
        CovenantSnapshotCandidate candidate = Candidate("defaults.inference.temperature", "unused", scope: CovenantScope.Campaign, lane: CovenantLane.Proposed, fragment: "- defaults.inference.temperature: \"garbage\"\n");

        Assert.Empty(Link(candidate).OperationalDefaults);
    }

    [Fact]
    public void Retired_heads_cannot_enter_the_snapshot_of_operational_defaults()
    {
        CovenantSnapshotCandidate retired = Candidate("defaults.inference.temperature", "1", operation: CovenantOperation.Retire);

        Assert.Throws<ArgumentException>(() => CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, retired));
    }

    [Theory]
    [InlineData(CovenantSnapshotCandidateIntegrity.Invalid, 1u, 1u)]
    [InlineData(CovenantSnapshotCandidateIntegrity.Quarantined, 1u, 1u)]
    [InlineData(CovenantSnapshotCandidateIntegrity.Verified, 2u, 1u)]
    [InlineData(CovenantSnapshotCandidateIntegrity.Verified, 1u, 2u)]
    public void Damaged_or_unsupported_confirmed_defaults_fail_closed(CovenantSnapshotCandidateIntegrity integrity, uint compilerPolicy, uint rendererPolicy)
    {
        CovenantSnapshotCandidate candidate = Candidate("defaults.inference.temperature", "1", integrity: integrity, compilerPolicy: compilerPolicy, rendererPolicy: rendererPolicy);

        Assert.True(new CovenantLinker().Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, candidate)).IsFailure);
    }

    [Fact]
    public void Sampling_defaults_are_selected_only_when_confirmed_candidates_are_admitted()
    {
        CovenantTurnPlan plan = Link(Candidate("defaults.inference.temperature", "0.5"), Candidate("defaults.inference.top-p", "0.9", index: 2));
        CovenantAdmissionPlan fits = Admission(plan, 10);
        CovenantAdmissionPlan noFit = Admission(plan, 0);

        Assert.Equal(2, CovenantOperationalDefaultCatalog.SelectAdmitted(plan, fits).Length);
        Assert.Empty(CovenantOperationalDefaultCatalog.SelectAdmitted(plan, noFit));
    }

    [Fact]
    public void Admission_from_a_different_exact_version_cannot_apply_defaults()
    {
        CovenantTurnPlan plan = Link(Candidate("defaults.inference.temperature", "0.5"));
        CovenantAdmissionPlan admitted = Admission(plan, 10);
        CovenantAdmissionPlan wrongVersion = admitted with { Candidates = [admitted.Candidates[0] with { VersionId = Guid.NewGuid() }] };

        Assert.Throws<ArgumentException>(() => CovenantOperationalDefaultCatalog.SelectAdmitted(plan, wrongVersion));
        Assert.Throws<ArgumentException>(() => CovenantOperationalDefaultCatalog.SelectAdmitted(plan, admitted with { Candidates = [] }));
        Assert.Throws<ArgumentException>(() => CovenantOperationalDefaultCatalog.SelectAdmitted(plan, admitted with { ConfirmedAdmitted = false }));
    }

    [Fact]
    public void Receipt_carries_only_defaults_whose_exact_sources_were_admitted()
    {
        CovenantTurnPlan plan = Link(Candidate("defaults.inference.temperature", "0.5"));
        CovenantAdmissionPlan fits = Admission(plan, 10);
        CovenantAdmissionPlan noFit = Admission(plan, 0);

        CovenantAdmissionReceipt receipt = Receipt(plan, fits);
        CovenantAdmissionReceipt refused = Receipt(plan, noFit);

        Assert.Equal(plan.OperationalDefaults, receipt.OperationalDefaults);
        Assert.Empty(refused.OperationalDefaults);
        Assert.NotEqual(receipt.Digest, refused.Digest);
    }

    [Fact]
    public void Typed_plan_extension_binds_policy_kind_consumer_value_and_exact_provenance()
    {
        CovenantTurnPlan plan = Link(Candidate("defaults.inference.temperature", "0.5"));
        CovenantOperationalDefault original = Assert.Single(plan.OperationalDefaults);
        PlanDigestInput input = plan.ToDigestInput();
        CovenantDigest withoutDefaults = CovenantDigests.Plan(input with { OperationalDefaults = [] });

        Assert.NotEqual(withoutDefaults, plan.Digest);
        Assert.Equal(plan.Digest, CovenantDigests.Plan(input));

        CovenantOperationalDefault[] changes =
        [
            Copy(original, policyVersion: 2),
            Copy(original, value: 0.75f),
            Copy(original, kind: CovenantOperationalDefaultKind.TopP),
            Copy(original, entryId: Guid.NewGuid()),
            Copy(original, versionId: Guid.NewGuid()),
            Copy(original, revision: original.Revision + 1),
            Copy(original, scope: CovenantScope.Campaign, campaignId: CovenantTask6Fixture.CampaignId)
        ];

        Assert.All(changes, changed => Assert.NotEqual(plan.Digest, CovenantDigests.Plan(input with { OperationalDefaults = [changed] })));
    }

    [Fact]
    public void Native_digest_corpus_covers_the_typed_plan_extension_with_a_literal_vector()
    {
        CovenantDigestCorpusResult result = CovenantDigestCorpus.Run();

        Assert.True(result.Succeeded, result.FirstFailureCaseId);
        Assert.Equal(36, result.CategoryCounts.DomainVectors);

        output.WriteLine($"Manifest={result.CaseManifestDigest}; Results={result.ResultAggregateDigest}; Aggregate={result.Aggregate}");
    }

    [Fact]
    public void Ordinary_plan_has_no_extension_and_preserves_the_original_digest_corpus()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

        Assert.Empty(plan.OperationalDefaults);
        Assert.Equal(CovenantDigests.Plan(plan.ToDigestInput() with { OperationalDefaults = default }), plan.Digest);
        Assert.True(CovenantDigestCorpus.Run().Succeeded);
    }

    private static CovenantOperationalDefault Copy(CovenantOperationalDefault source, uint? policyVersion = null, float? value = null, CovenantOperationalDefaultKind? kind = null, Guid? entryId = null, Guid? versionId = null, ulong? revision = null, CovenantScope? scope = null, Guid? campaignId = null) =>
        new(kind ?? source.Kind, source.Consumer, value ?? source.Value, policyVersion ?? source.PolicyVersion, entryId ?? source.EntryId, versionId ?? source.VersionId, revision ?? source.Revision, scope ?? source.Scope, campaignId ?? source.CampaignId);

    private static CovenantAdmissionPlan Admission(CovenantTurnPlan plan, ulong budget) =>
        CovenantAdmissionPlanner.Plan(plan, budget, static content => content.HasConfirmed ? 1ul : 0ul, static _ => 1ul);

    private static CovenantAdmissionReceipt Receipt(CovenantTurnPlan plan, CovenantAdmissionPlan admission) =>
        new(plan, 1, CovenantTask6Fixture.BranchId, 1, null, CovenantTask6Fixture.ProviderCall(), admission.ConfirmedAdmitted ? 10ul : 0ul, admission.EstimatedAdmittedTokens, admission.Candidates);

    private static CovenantTurnPlan Link(params CovenantSnapshotCandidate[] candidates) =>
        new CovenantLinker().Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, candidates)).Value;

    private static CovenantSnapshotCandidate Candidate(string key, string value, CovenantScope scope = CovenantScope.Global, CovenantLane lane = CovenantLane.Confirmed, int index = 1, Guid? campaignId = null, CovenantOrigin? origin = null, string? fragment = null, CovenantOperation operation = CovenantOperation.Set, CovenantSnapshotCandidateIntegrity integrity = CovenantSnapshotCandidateIntegrity.Verified, uint compilerPolicy = 1, uint rendererPolicy = 1) =>
        CovenantTask6Fixture.CreateCandidate(key, Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"), Guid.Parse($"10000000-0000-0000-0000-{index:000000000000}"), (ulong)index, scope, scope == CovenantScope.Campaign ? campaignId ?? CovenantTask6Fixture.CampaignId : null, lane, operation, origin ?? (lane == CovenantLane.Confirmed ? CovenantOrigin.Operator : CovenantOrigin.AgentProposed), compilerPolicy, 0, integrity, rendererPolicy: rendererPolicy, compiledFragment: [.. Encoding.UTF8.GetBytes(fragment ?? new CovenantCompiler().Compile(key, value).Fragment)]);
}
