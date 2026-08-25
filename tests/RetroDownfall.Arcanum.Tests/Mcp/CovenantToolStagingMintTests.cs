using System.Collections.Immutable;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Covenant;

using ArcanumJsonRpcRequest = RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol.JsonRpcRequest;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// Minting the single-use capability a Covenant staging tool call needs.
/// </summary>
/// <remarks>
/// The registry has had a <c>TryTake</c> caller since the tools shipped and never had a
/// <c>TryRegister</c> one, so every live <c>propose_covenant</c> call refused with
/// <c>Covenant.IneligibleTurn</c>. These tests are about which calls now get a capability and,
/// more importantly, which still do not.
/// </remarks>
public sealed class CovenantToolStagingMintTests
{

    private const string ConnectionKey = "connection-1";

    [Fact]
    public void A_proposal_on_a_staging_turn_receives_its_capability()
    {

        CovenantToolCapabilityRegistry registry = new();

        using IDisposable staging = CovenantToolStagingAmbient.Push(Staging(registry));

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(
            ConnectionKey,
            ToolsCall("7", CovenantToolNames.ProposeCovenant));

        Result<CovenantToolCapabilityGrant> taken = registry.TryTake(ConnectionKey, "7");

        Assert.True(taken.IsSuccess, taken.IsFailure ? taken.Error.Message : string.Empty);

        Assert.Equal(CovenantToolNames.ProposeCovenant, taken.Value.Capability.ToolName);

        Assert.Equal("7", taken.Value.Capability.ToolCallId);

    }

    [Fact]
    public void A_turn_that_published_no_staging_mints_nothing()
    {

        CovenantToolCapabilityRegistry registry = new();

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(
            ConnectionKey,
            ToolsCall("7", CovenantToolNames.ProposeCovenant));

        // The handler's own refusal path then reports Covenant.IneligibleTurn, which is the honest
        // answer for a turn with no plan behind it.
        Assert.True(registry.TryTake(ConnectionKey, "7").IsFailure);

    }

    [Fact]
    public void An_ordinary_tool_call_mints_nothing()
    {

        CovenantToolCapabilityRegistry registry = new();

        using IDisposable staging = CovenantToolStagingAmbient.Push(Staging(registry));

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(
            ConnectionKey,
            ToolsCall("7", "search_workspace"));

        Assert.Equal(0, registry.CountForTests);

    }

    /// <summary>
    /// A retirement receives no capability, and a proposal into the same registry still does.
    /// </summary>
    /// <remarks>
    /// Two independent guards produce this outcome: the binder mints only for the proposal name, and
    /// <c>CovenantToolInvocationContext</c>'s own constructor refuses a retirement carrying neither
    /// preflight nor Ward receipt. This test cannot tell them apart — removing the name check leaves it
    /// green — so it claims only the outcome. The constructor's refusal is pinned separately by
    /// <c>CovenantToolInvocationContextTests</c>; the second half here rules out the boring
    /// explanation that the registry was simply not working.
    /// </remarks>
    [Fact]
    public void A_retirement_mints_nothing_while_a_proposal_still_does()
    {

        CovenantToolCapabilityRegistry registry = new();

        using IDisposable staging = CovenantToolStagingAmbient.Push(Staging(registry));

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(
            ConnectionKey,
            ToolsCall("7", CovenantToolNames.RetireCovenant));

        Assert.Equal(0, registry.CountForTests);

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(
            ConnectionKey,
            ToolsCall("8", CovenantToolNames.ProposeCovenant));

        Assert.Equal(1, registry.CountForTests);

    }

    [Fact]
    public void One_request_identity_receives_exactly_one_capability()
    {

        CovenantToolCapabilityRegistry registry = new();

        using IDisposable staging = CovenantToolStagingAmbient.Push(Staging(registry));

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(
            ConnectionKey,
            ToolsCall("7", CovenantToolNames.ProposeCovenant));

        _ = SessionAttachmentAmbientSend.ApplyAmbientBinding(
            ConnectionKey,
            ToolsCall("7", CovenantToolNames.ProposeCovenant));

        Assert.Equal(1, registry.CountForTests);

        // The first take succeeds and the capability is one-shot, so a second call reusing the same
        // request id cannot borrow it.
        Assert.True(registry.TryTake(ConnectionKey, "7").IsSuccess);

        Assert.True(registry.TryTake(ConnectionKey, "7").IsFailure);

    }

    [Fact]
    public void The_ambient_is_restored_when_its_scope_ends()
    {

        CovenantToolCapabilityRegistry registry = new();

        Assert.Null(CovenantToolStagingAmbient.Current);

        using (CovenantToolStagingAmbient.Push(Staging(registry)))
        {

            Assert.NotNull(CovenantToolStagingAmbient.Current);

        }

        // One turn's staging must not outlive its dispatch and authorize the next turn's tool call.
        Assert.Null(CovenantToolStagingAmbient.Current);

    }

    private static CovenantToolStagingContext Staging(CovenantToolCapabilityRegistry registry)
    {

        CovenantTurnPlan plan = Plan();

        return new CovenantToolStagingContext(
            new CovenantMutationCollector(
                CovenantTask6Fixture.GuidFor(9),
                plan.Digest,
                CovenantTask6Fixture.BranchId),
            CovenantCapabilityFixtures.Campaign(),
            CovenantCapabilityFixtures.Admission(plan),
            CovenantCapabilityFixtures.Materialization(),
            new StubProbe(),
            registry,
            CancellationToken.None);

    }

    private static CovenantTurnPlan Plan() =>
        new CovenantLinker()
            .Link(CovenantTask6Fixture.Snapshot(
                CovenantTask6Fixture.CampaignId,
                [
                    CovenantTask6Fixture.CampaignProposed(
                        "proposed.a",
                        CovenantTask6Fixture.GuidFor(301),
                        CovenantTask6Fixture.GuidFor(401),
                        1,
                        51,
                        CovenantTask6Fixture.CampaignId),
                ]))
            .Value;

    private static ArcanumJsonRpcRequest ToolsCall(string id, string toolName) =>
        new()
        {
            Method = "tools/call",
            Id = JsonDocument.Parse($"\"{id}\"").RootElement.Clone(),
            Params = JsonDocument
                .Parse($$$"""{"name":"{{{toolName}}}","arguments":{}}""")
                .RootElement.Clone(),
        };

    private sealed class StubProbe : ICovenantTurnHeadProbe
    {

        public ValueTask<Result<CovenantLaneHeadProbe>> ProbeAsync(
            CovenantLane lane,
            string normalizedKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantLaneHeadProbe>.Success(
                CovenantLaneHeadProbe.NotFound(CovenantOperationScope.Global, lane, normalizedKey, 0)));

        public ValueTask<Result<CovenantSectionOccupancy>> ProbeSectionAsync(
            CovenantLane lane,
            ImmutableArray<string> excludedKeys,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantSectionOccupancy>.Success(CovenantSectionOccupancy.Empty));

    }

}
