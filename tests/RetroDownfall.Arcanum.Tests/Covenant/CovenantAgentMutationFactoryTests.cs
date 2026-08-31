using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The two agent-authored intents the factory derives from a live capability: what they claim as
/// their origin, and what that origin binds in the request preimage (§10.14).
/// </summary>
public sealed class CovenantAgentMutationFactoryTests
{

    private static readonly Guid TurnId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public void A_proposal_claims_the_agent_proposed_origin()
    {
        using FactoryFixture fixture = new(CovenantToolNames.ProposeCovenant);

        CovenantCompiledContent compiled = new CovenantCompiler().Compile("campaign.a", "concise and direct");

        Result<CovenantMutationIntent> intent = CovenantAgentMutationFactory.Propose(
            fixture.Context,
            compiled,
            expectedLaneRevision: 0,
            expectedKeyEpoch: 0,
            CovenantTask6Fixture.D(90));

        Assert.True(intent.IsSuccess, intent.Error.Message);
        Assert.Equal(CovenantMutationKind.AgentPropose, intent.Value.Kind);
        Assert.Equal(CovenantOrigin.AgentProposed, intent.Value.Origin);
        Assert.Null(intent.Value.Authorization.WardReceiptDigest);
    }

    [Fact]
    public void An_ungated_retirement_keeps_the_frozen_agent_approved_origin_without_ward_evidence()
    {
        using FactoryFixture fixture = new(CovenantToolNames.RetireCovenant);

        Result<CovenantMutationIntent> intent = CovenantAgentMutationFactory.Retire(
            fixture.Context,
            CovenantTask6Fixture.D(90));

        Assert.True(intent.IsSuccess, intent.Error.Message);
        Assert.Equal(CovenantMutationKind.AgentRetire, intent.Value.Kind);

        CovenantRetirementPreflight preflight = fixture.Context.RetirementPreflight!;

        Assert.Equal(CovenantOrigin.AgentApproved, intent.Value.Origin);
        Assert.Equal(CovenantAuthorizationMode.None, intent.Value.Authorization.Mode);
        Assert.Null(intent.Value.Authorization.WardReceiptDigest);
        Assert.Equal(preflight.PreflightBodyDigest, intent.Value.Authorization.PreflightBodyDigest);
    }

    [Fact]
    public void A_retirement_binds_its_approved_origin_into_the_request_preimage()
    {
        using FactoryFixture fixture = new(CovenantToolNames.RetireCovenant);

        CovenantRetirementPreflight preflight = fixture.Context.RetirementPreflight!;

        Result<CovenantMutationIntent> intent = CovenantAgentMutationFactory.Retire(
            fixture.Context,
            CovenantTask6Fixture.D(90));

        CovenantDigest expected = CovenantDigests.MutationRequest(new MutationRequestDigestInput(
            CovenantMutationKind.AgentRetire,
            fixture.Context.MutationId,
            CovenantScope.Campaign,
            CovenantTask6Fixture.CampaignId,
            new CovenantKey(preflight.NormalizedKey),
            preflight.Lane,
            CovenantOperation.Retire,
            checked((ulong)preflight.TargetLaneRevision),
            Reactivation: false,
            CovenantOrigin.AgentApproved,
            null,
            null,
            (uint)CovenantCompiler.CompilerPolicyVersion,
            fixture.Context.BasePlanDigest,
            fixture.Context.ProducingAdmission.Digest,
            ImmutableArray<CovenantDigest>.Empty));

        Assert.True(intent.IsSuccess, intent.Error.Message);
        Assert.Equal(expected, intent.Value.Target.IdentityDigest);
        Assert.Equal(expected, intent.Value.Authorization.RequestIdempotencyDigest);
    }

    private sealed class FactoryFixture : IDisposable
    {

        private readonly CancellationTokenSource _turn = new();

        public FactoryFixture(string toolName)
        {
            bool isRetirement = string.Equals(toolName, CovenantToolNames.RetireCovenant, StringComparison.Ordinal);

            CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

            Collector = new CovenantMutationCollector(TurnId, plan.Digest, CovenantTask6Fixture.BranchId);

            Nonce = CovenantToolCapabilityNonce.Create();

            Context = new CovenantToolInvocationContext(
                Collector,
                CovenantCapabilityFixtures.Campaign(),
                CovenantCapabilityFixtures.Admission(plan),
                CovenantCapabilityFixtures.Materialization(sourceCount: 0),
                new CovenantCapabilityFixtures.StubHeadProbe(),
                Nonce,
                toolName,
                "call-1",
                isRetirement ? CovenantCapabilityFixtures.RetirementPreflight() : null,
                _turn.Token);
        }

        public CovenantMutationCollector Collector { get; }

        public CovenantToolInvocationContext Context { get; }

        public CovenantToolCapabilityNonce Nonce { get; }

        public void Dispose()
        {
            Context.DisposeAsync().AsTask().GetAwaiter().GetResult();

            _turn.Dispose();
        }

    }

}
