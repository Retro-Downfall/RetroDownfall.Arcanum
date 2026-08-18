using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The cross-field guards that make an impossible mutation shape unrepresentable in Core rather than
/// caught by a SQLite CHECK at commit time (§10.14).
/// </summary>
public sealed class CovenantMutationIntentTests
{

    private static readonly Guid TurnId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Theory]
    [InlineData(CovenantMutationKind.OperatorSet, CovenantOrigin.Operator)]
    [InlineData(CovenantMutationKind.OperatorRetire, CovenantOrigin.Operator)]
    [InlineData(CovenantMutationKind.AgentPropose, CovenantOrigin.AgentProposed)]
    [InlineData(CovenantMutationKind.AgentRetire, CovenantOrigin.AgentApproved)]
    public void Each_mutation_kind_admits_exactly_one_origin(CovenantMutationKind kind, CovenantOrigin origin)
    {
        CovenantMutationIntent intent = Intent(kind, origin);

        Assert.Equal(kind, intent.Kind);
        Assert.Equal(origin, intent.Origin);

        foreach (CovenantOrigin other in (CovenantOrigin[])
            [CovenantOrigin.Operator, CovenantOrigin.AgentProposed, CovenantOrigin.AgentApproved])
        {
            if (other == origin)
            {
                continue;
            }

            _ = Assert.Throws<ArgumentException>(() => Intent(kind, other));
        }
    }

    [Fact]
    public void Only_an_approved_agent_retirement_may_carry_ward_evidence()
    {
        // covenant_versions enforces exactly this pairing, and a CHECK failure at commit time would
        // abort the whole turn transaction rather than surface a typed Covenant error.
        _ = Assert.Throws<ArgumentException>(() => Intent(
            CovenantMutationKind.AgentPropose,
            CovenantOrigin.AgentProposed,
            wardReceiptDigest: CovenantTask6Fixture.D(21),
            mode: CovenantAuthorizationMode.WardInteractive));

        _ = Assert.Throws<ArgumentException>(() => Intent(
            CovenantMutationKind.OperatorRetire,
            CovenantOrigin.Operator,
            wardReceiptDigest: CovenantTask6Fixture.D(21),
            mode: CovenantAuthorizationMode.WardInteractive));

        _ = Assert.Throws<ArgumentException>(() => Intent(
            CovenantMutationKind.AgentRetire,
            CovenantOrigin.AgentApproved,
            wardReceiptDigest: null,
            mode: CovenantAuthorizationMode.None));

        _ = Assert.Throws<ArgumentException>(() => Intent(
            CovenantMutationKind.AgentRetire,
            CovenantOrigin.AgentApproved,
            wardReceiptDigest: CovenantTask6Fixture.D(21),
            mode: CovenantAuthorizationMode.ApiMasterKey));
    }

    [Fact]
    public void An_agent_proposal_cannot_claim_the_confirmed_lane()
    {
        // CovenantSnapshotCandidate refuses to project a Confirmed head whose origin is AgentProposed,
        // so an intent that could write one is a durable row nothing can read back.
        _ = Assert.Throws<ArgumentException>(() => Intent(
            CovenantMutationKind.AgentPropose,
            CovenantOrigin.AgentProposed,
            lane: CovenantLane.Confirmed));

        CovenantMutationIntent confirmedRetirement = Intent(
            CovenantMutationKind.AgentRetire,
            CovenantOrigin.AgentApproved,
            lane: CovenantLane.Confirmed);

        Assert.Equal(CovenantLane.Confirmed, confirmedRetirement.Target.Lane);
    }

    private static CovenantMutationIntent Intent(
        CovenantMutationKind kind,
        CovenantOrigin origin,
        CovenantLane? lane = null,
        CovenantDigest? wardReceiptDigest = null,
        CovenantAuthorizationMode? mode = null)
    {
        bool retirement = kind is CovenantMutationKind.OperatorRetire or CovenantMutationKind.AgentRetire;
        bool agentAuthored = kind is CovenantMutationKind.AgentPropose or CovenantMutationKind.AgentRetire;
        bool approved = kind == CovenantMutationKind.AgentRetire;

        CovenantDigest? ward = wardReceiptDigest ?? (approved ? CovenantTask6Fixture.D(21) : null);

        return new CovenantMutationIntent(
            Guid.NewGuid(),
            kind,
            retirement ? CovenantOperation.Retire : CovenantOperation.Set,
            origin,
            new CovenantMutationTarget(
                CovenantOperationScope.ForCampaign(CovenantTask6Fixture.CampaignId),
                new CovenantKey("campaign.a"),
                "campaign.a",
                lane ?? (agentAuthored ? CovenantLane.Proposed : CovenantLane.Confirmed),
                CovenantTask6Fixture.D(11)),
            expectedLaneRevision: 0,
            reactivate: false,
            expectedKeyEpoch: 0,
            retirement ? null : Artifact(),
            ImmutableArray<CovenantMutationProvenanceLeaf>.Empty,
            new CovenantMutationAuthorization(
                CovenantTask6Fixture.D(14),
                CovenantTask6Fixture.D(15),
                CovenantTask6Fixture.D(16),
                CovenantTask6Fixture.D(17),
                mode ?? (approved ? CovenantAuthorizationMode.WardInteractive : CovenantAuthorizationMode.None),
                ward,
                approved ? CovenantTask6Fixture.D(22) : null),
            agentAuthored ? TurnId : null,
            agentAuthored ? "call-1" : null,
            agentAuthored ? CovenantTask6Fixture.D(70) : null,
            agentAuthored ? CovenantTask6Fixture.D(71) : null);
    }

    private static CovenantMutationArtifact Artifact()
    {
        CovenantCompiledContent compiled = new CovenantCompiler().Compile("campaign.a", "concise");

        return new CovenantMutationArtifact(
            compiled.AuthoredContent,
            compiled.Fragment,
            compiled.AuthoredHash,
            compiled.FragmentHash,
            compiled.FragmentUtf8ByteCount,
            compiled.RequiredFenceLength,
            compiled.CompilerPolicyVersion,
            compiled.RendererPolicyVersion);
    }

}
