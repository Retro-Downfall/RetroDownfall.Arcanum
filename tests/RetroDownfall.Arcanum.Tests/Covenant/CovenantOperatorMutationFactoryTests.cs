using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The operator-authored half of the mutation protocol: what only a person may write, and what the
/// receipt an operator later reads is bound to.
/// </summary>
public sealed class CovenantOperatorMutationFactoryTests
{

    private static readonly Guid MutationId = Guid.Parse("9a1c0d4e-1111-4222-8333-444455556666");

    private static readonly Guid CampaignId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public void A_set_authors_the_Confirmed_lane_as_the_operator()
    {

        CovenantMutationIntent intent = Set(CovenantOperationScope.Global).Value;

        Assert.Equal(CovenantMutationKind.OperatorSet, intent.Kind);

        Assert.Equal(CovenantOrigin.Operator, intent.Origin);

        Assert.Equal(CovenantLane.Confirmed, intent.Target.Lane);

        Assert.Equal(CovenantOperation.Set, intent.Operation);

        Assert.Equal(CovenantAuthorizationMode.ApiMasterKey, intent.Authorization.Mode);

    }

    [Fact]
    public void An_operator_mutation_carries_no_turn_admission_lineage()
    {

        CovenantMutationIntent intent = Set(CovenantOperationScope.Global).Value;

        // The agent path is authorized by a turn's admission receipt; this one is authorized by
        // operator authority. Carrying both would let an operator write claim a turn's lineage.
        Assert.Null(intent.SourceTurnId);

        Assert.Null(intent.SourceToolCallId);

        Assert.Null(intent.BasePlanDigest);

        Assert.Null(intent.AdmissionReceiptDigest);

        Assert.Empty(intent.Provenance);

    }

    [Fact]
    public void The_authority_epoch_is_part_of_what_a_set_is_authorized_against()
    {

        CovenantDigest first = Set(CovenantOperationScope.Global, authorityEpoch: 7)
            .Value.Authorization.AuthorizationDigest;

        CovenantDigest second = Set(CovenantOperationScope.Global, authorityEpoch: 8)
            .Value.Authorization.AuthorizationDigest;

        Assert.NotEqual(first, second);

    }

    [Fact]
    public void A_global_and_a_campaign_set_of_the_same_key_are_different_requests()
    {

        CovenantDigest global = Set(CovenantOperationScope.Global)
            .Value.Authorization.RequestIdempotencyDigest;

        CovenantDigest campaign = Set(CovenantOperationScope.ForCampaign(CampaignId))
            .Value.Authorization.RequestIdempotencyDigest;

        Assert.NotEqual(global, campaign);

    }

    [Fact]
    public void Reactivation_changes_the_request_it_is_replaying_against()
    {

        CovenantDigest plain = Set(CovenantOperationScope.Global)
            .Value.Authorization.RequestIdempotencyDigest;

        CovenantDigest reactivating = Set(CovenantOperationScope.Global, reactivate: true)
            .Value.Authorization.RequestIdempotencyDigest;

        // Otherwise an operator who declined to reactivate and one who insisted would replay onto
        // each other's receipt.
        Assert.NotEqual(plain, reactivating);

    }

    [Fact]
    public void A_retirement_of_revision_zero_is_refused_before_anything_is_digested()
    {

        Result<CovenantMutationIntent> retired = CovenantOperatorMutationFactory.Retire(
            MutationId,
            CovenantOperationScope.Global,
            "preference.builds",
            "preference.builds",
            CovenantLane.Confirmed,
            expectedLaneRevision: 0,
            Binding(),
            CovenantTask6Fixture.D(9));

        Assert.True(retired.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.RevisionConflict, retired.Error.Code);

    }

    [Fact]
    public void An_operator_may_retire_the_Proposed_lane_the_agent_wrote()
    {

        Result<CovenantMutationIntent> retired = CovenantOperatorMutationFactory.Retire(
            MutationId,
            CovenantOperationScope.ForCampaign(CampaignId),
            "preference.builds",
            "preference.builds",
            CovenantLane.Proposed,
            expectedLaneRevision: 3,
            Binding(),
            CovenantTask6Fixture.D(9));

        Assert.True(retired.IsSuccess, retired.IsFailure ? retired.Error.Message : string.Empty);

        Assert.Equal(CovenantLane.Proposed, retired.Value.Target.Lane);

        Assert.Equal(CovenantMutationKind.OperatorRetire, retired.Value.Kind);

    }

    [Fact]
    public void The_preflight_body_is_bound_into_the_authorization()
    {

        CovenantDigest first = Set(CovenantOperationScope.Global, preflight: CovenantTask6Fixture.D(11))
            .Value.Authorization.AuthorizationDigest;

        CovenantDigest second = Set(CovenantOperationScope.Global, preflight: CovenantTask6Fixture.D(12))
            .Value.Authorization.AuthorizationDigest;

        Assert.NotEqual(first, second);

    }

    private static Result<CovenantMutationIntent> Set(
        CovenantOperationScope scope,
        ulong authorityEpoch = 7,
        bool reactivate = false,
        CovenantDigest? preflight = null) =>
        CovenantOperatorMutationFactory.Set(
            MutationId,
            scope,
            Compile("preference.builds", "Run build commands from the repository root."),
            expectedLaneRevision: 0,
            reactivate,
            Binding(authorityEpoch),
            preflight ?? CovenantTask6Fixture.D(9));

    private static CovenantOperatorMutationBinding Binding(ulong authorityEpoch = 7) =>
        new(
            CovenantTask6Fixture.DatasetGeneration,
            authorityEpoch,
            ExpectedKeyEpoch: 1,
            CampaignRegistryEpoch: 1);

    private static CovenantCompiledContent Compile(string key, string content)
    {

        Result<CovenantCompiledContent> compiled = new CovenantCompiler().Compile(key, content);

        Assert.True(compiled.IsSuccess, compiled.IsFailure ? compiled.Error.Message : string.Empty);

        return compiled.Value;

    }

}
