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
    public void A_new_agent_retirement_uses_no_ward_authorization()
    {
        CovenantMutationIntent retirement = Intent(
            CovenantMutationKind.AgentRetire,
            CovenantOrigin.AgentApproved);

        Assert.Equal(CovenantAuthorizationMode.None, retirement.Authorization.Mode);
        Assert.Null(retirement.Authorization.WardReceiptDigest);
        Assert.Equal(CovenantTask6Fixture.D(22), retirement.Authorization.PreflightBodyDigest);
    }

    [Theory]
    [InlineData(CovenantAuthorizationMode.WardInteractive)]
    [InlineData(CovenantAuthorizationMode.WardConfiguredAutoApproval)]
    public void Complete_historical_ward_pairs_remain_representable(CovenantAuthorizationMode mode)
    {
        CovenantDigest digest = CovenantTask6Fixture.D(21);

        CovenantMutationIntent retirement = Intent(
            CovenantMutationKind.AgentRetire,
            CovenantOrigin.AgentApproved,
            wardReceiptDigest: digest,
            mode: mode);

        Assert.Equal(mode, retirement.Authorization.Mode);
        Assert.Equal(digest, retirement.Authorization.WardReceiptDigest);
    }

    [Fact]
    public void An_agent_retirement_rejects_partial_or_unrelated_authorization_shapes()
    {
        _ = Assert.Throws<ArgumentException>(() => Intent(
            CovenantMutationKind.AgentRetire,
            CovenantOrigin.AgentApproved,
            wardReceiptDigest: CovenantTask6Fixture.D(21),
            mode: CovenantAuthorizationMode.None));

        _ = Assert.Throws<ArgumentException>(() => Intent(
            CovenantMutationKind.AgentRetire,
            CovenantOrigin.AgentApproved,
            wardReceiptDigest: null,
            mode: CovenantAuthorizationMode.WardInteractive));

        _ = Assert.Throws<ArgumentException>(() => Intent(
            CovenantMutationKind.AgentRetire,
            CovenantOrigin.AgentApproved,
            wardReceiptDigest: null,
            mode: CovenantAuthorizationMode.WardConfiguredAutoApproval));

        _ = Assert.Throws<ArgumentException>(() => Intent(
            CovenantMutationKind.AgentRetire,
            CovenantOrigin.AgentApproved,
            mode: CovenantAuthorizationMode.ApiMasterKey));
    }

    [Fact]
    public void Every_other_mutation_rejects_ward_evidence_and_ward_modes()
    {
        (CovenantMutationKind Kind, CovenantOrigin Origin)[] shapes =
        [
            (CovenantMutationKind.OperatorSet, CovenantOrigin.Operator),
            (CovenantMutationKind.OperatorRetire, CovenantOrigin.Operator),
            (CovenantMutationKind.AgentPropose, CovenantOrigin.AgentProposed),
        ];

        foreach ((CovenantMutationKind kind, CovenantOrigin origin) in shapes)
        {
            _ = Assert.Throws<ArgumentException>(() => Intent(
                kind,
                origin,
                wardReceiptDigest: CovenantTask6Fixture.D(21)));

            foreach (CovenantAuthorizationMode wardMode in new[]
            {
                CovenantAuthorizationMode.WardInteractive,
                CovenantAuthorizationMode.WardConfiguredAutoApproval,
            })
            {
                _ = Assert.Throws<ArgumentException>(() => Intent(
                    kind,
                    origin,
                    mode: wardMode));
            }
        }
    }

    [Fact]
    public void Operator_mutations_keep_master_key_authorization_and_agent_proposals_keep_none()
    {
        Assert.Equal(
            CovenantAuthorizationMode.ApiMasterKey,
            Intent(CovenantMutationKind.OperatorSet, CovenantOrigin.Operator).Authorization.Mode);

        Assert.Equal(
            CovenantAuthorizationMode.ApiMasterKey,
            Intent(CovenantMutationKind.OperatorRetire, CovenantOrigin.Operator).Authorization.Mode);

        Assert.Equal(
            CovenantAuthorizationMode.None,
            Intent(CovenantMutationKind.AgentPropose, CovenantOrigin.AgentProposed).Authorization.Mode);
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
        bool agentRetirement = kind == CovenantMutationKind.AgentRetire;

        CovenantAuthorizationMode defaultMode = kind switch
        {
            CovenantMutationKind.OperatorSet or CovenantMutationKind.OperatorRetire =>
                CovenantAuthorizationMode.ApiMasterKey,
            _ => CovenantAuthorizationMode.None,
        };

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
                mode ?? defaultMode,
                wardReceiptDigest,
                agentRetirement ? CovenantTask6Fixture.D(22) : null),
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
