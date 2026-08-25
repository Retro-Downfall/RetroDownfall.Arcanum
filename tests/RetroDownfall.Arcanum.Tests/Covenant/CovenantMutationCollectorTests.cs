using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The per-turn staging barrier: what may be staged, by whom, and what survives a branch change
/// (§10.13).
/// </summary>
public sealed class CovenantMutationCollectorTests
{

    private static readonly Guid TurnId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static readonly Guid BranchOne = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1");

    private static readonly Guid BranchTwo = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2");

    [Fact]
    public void Stage_BindsTheTurnPlanAndTheAdmissionThatProducedTheToolCall()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantAdmissionReceipt admission = Admission(plan, BranchOne, 1);
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);

        Result<CovenantStagedMutationReceipt> staged = collector.Stage(
            Intent("a.key", plan, admission),
            admission,
            Digest(90));

        Assert.True(staged.IsSuccess, staged.Error.Message);
        Assert.Equal(1, collector.StagedCount);
        Assert.Equal(CovenantScope.Campaign, staged.Value.ScopeKind);
        Assert.Equal(CovenantLane.Proposed, staged.Value.Lane);
    }

    [Fact]
    public void Stage_RefusesAnIntentBoundToAnotherTurnOrAnotherPlan()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantAdmissionReceipt admission = Admission(plan, BranchOne, 1);
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);

        Result<CovenantStagedMutationReceipt> foreignTurn = collector.Stage(
            Intent("a.key", plan, admission, turnId: Guid.NewGuid()),
            admission,
            Digest(90));
        Result<CovenantStagedMutationReceipt> foreignPlan = collector.Stage(
            Intent("a.key", plan, admission, basePlanDigest: Digest(3)),
            admission,
            Digest(90));

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, foreignTurn.Error.Code);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, foreignPlan.Error.Code);
        Assert.Equal(0, collector.StagedCount);
    }

    [Fact]
    public void Stage_ReturnsTheOriginalReceiptForAnExactToolReplayAndConsumesNoSlot()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantAdmissionReceipt admission = Admission(plan, BranchOne, 1);
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);
        CovenantMutationIntent intent = Intent("a.key", plan, admission);

        CovenantStagedMutationReceipt first = collector.Stage(intent, admission, Digest(90)).Value;
        Result<CovenantStagedMutationReceipt> replay = collector.Stage(intent, admission, Digest(90));

        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.Equal(first, replay.Value);
        Assert.Equal(1, collector.StagedCount);
    }

    [Fact]
    public void Stage_FailsClosedWhenOneToolCallIdentityIsReusedWithDifferentInput()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantAdmissionReceipt admission = Admission(plan, BranchOne, 1);
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);

        _ = collector.Stage(Intent("a.key", plan, admission), admission, Digest(90));

        Result<CovenantStagedMutationReceipt> conflict = collector.Stage(
            Intent("b.key", plan, admission),
            admission,
            Digest(91));

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, conflict.Error.Code);
    }

    [Fact]
    public void Stage_RefusesASecondMutationForOneTargetInTheActiveBranch()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantAdmissionReceipt admission = Admission(plan, BranchOne, 1);
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);

        _ = collector.Stage(Intent("a.key", plan, admission, toolCallId: "call-1"), admission, Digest(90));

        Result<CovenantStagedMutationReceipt> duplicate = collector.Stage(
            Intent("a.key", plan, admission, toolCallId: "call-2"),
            admission,
            Digest(91));

        Assert.Equal(ErrorCodes.Covenant.RevisionConflict, duplicate.Error.Code);
    }

    [Fact]
    public void Stage_StopsAtTheFourIntentCeiling()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantAdmissionReceipt admission = Admission(plan, BranchOne, 1);
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);

        for (int index = 0; index < CovenantLimits.MaxStagedMutationsPerTurn; index++)
        {
            Result<CovenantStagedMutationReceipt> staged = collector.Stage(
                Intent($"key.{index}", plan, admission, toolCallId: $"call-{index}"),
                admission,
                Digest((byte)(90 + index)));

            Assert.True(staged.IsSuccess, staged.Error.Message);
        }

        Result<CovenantStagedMutationReceipt> overflow = collector.Stage(
            Intent("key.overflow", plan, admission, toolCallId: "call-overflow"),
            admission,
            Digest(120));

        Assert.Equal(ErrorCodes.Covenant.CapacityExceeded, overflow.Error.Code);
        Assert.Equal(CovenantLimits.MaxStagedMutationsPerTurn, collector.StagedCount);
    }

    [Fact]
    public void OpenBranch_CarriesTheSharedPrefixAndDropsTheAbandonedSuffix()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);
        CovenantAdmissionReceipt first = Admission(plan, BranchOne, 1);
        CovenantAdmissionReceipt second = Admission(plan, BranchOne, 2);

        _ = collector.Stage(Intent("shared.key", plan, first, toolCallId: "call-1"), first, Digest(90));
        _ = collector.Stage(Intent("abandoned.key", plan, second, toolCallId: "call-2"), second, Digest(91));

        Result opened = collector.OpenBranch(BranchTwo, sharedPrefixOrdinal: 1);
        Result<ImmutableArray<CovenantMutationIntent>> sealedBatch = collector.Seal(BranchTwo, 8);

        Assert.True(opened.IsSuccess, opened.Error.Message);
        Assert.True(sealedBatch.IsSuccess, sealedBatch.Error.Message);
        Assert.Equal(
            ["shared.key"],
            sealedBatch.Value.Select(static intent => intent.Target.NormalizedKey.Value));
    }

    [Fact]
    public void OpenBranch_RefusesToResumeAnAbandonedBranch()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);

        _ = collector.OpenBranch(BranchTwo, 0);

        Result resumed = collector.OpenBranch(BranchOne, 0);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, resumed.Error.Code);
    }

    [Fact]
    public void Seal_RefusesWhileAToolCallIsStillInFlightAndThenRefusesLateStaging()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantAdmissionReceipt admission = Admission(plan, BranchOne, 1);
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);
        Result<IDisposable> use = collector.TryAcquireUse();

        Result<ImmutableArray<CovenantMutationIntent>> blocked = collector.Seal(BranchOne, 8);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, blocked.Error.Code);

        use.Value.Dispose();

        Result<ImmutableArray<CovenantMutationIntent>> sealedBatch = collector.Seal(BranchOne, 8);

        Assert.True(sealedBatch.IsSuccess, sealedBatch.Error.Message);
        Assert.Equal(CovenantCollectorState.Sealed, collector.State);
        Assert.Equal(
            ErrorCodes.Covenant.LifecycleConflict,
            collector.Stage(Intent("late.key", plan, admission), admission, Digest(95)).Error.Code);
        Assert.Equal(
            ErrorCodes.Covenant.LifecycleConflict,
            collector.TryAcquireUse().Error.Code);
    }

    [Fact]
    public void Seal_ExcludesIntentsPastTheCommittedBranchOrdinal()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);
        CovenantAdmissionReceipt early = Admission(plan, BranchOne, 1);
        CovenantAdmissionReceipt late = Admission(plan, BranchOne, 5);

        _ = collector.Stage(Intent("early.key", plan, early, toolCallId: "call-1"), early, Digest(90));
        _ = collector.Stage(Intent("late.key", plan, late, toolCallId: "call-2"), late, Digest(91));

        Result<ImmutableArray<CovenantMutationIntent>> sealedBatch = collector.Seal(BranchOne, 3);

        Assert.Equal(
            ["early.key"],
            sealedBatch.Value.Select(static intent => intent.Target.NormalizedKey.Value));
    }

    [Fact]
    public void Discard_IsTerminalAndPublishesNothing()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantAdmissionReceipt admission = Admission(plan, BranchOne, 1);
        CovenantMutationCollector collector = new(TurnId, plan.Digest, BranchOne);

        _ = collector.Stage(Intent("a.key", plan, admission), admission, Digest(90));

        collector.Discard();

        Assert.Equal(CovenantCollectorState.Discarded, collector.State);
        Assert.Equal(0, collector.StagedCount);
        Assert.Equal(
            ErrorCodes.Covenant.LifecycleConflict,
            collector.Seal(BranchOne, 8).Error.Code);
    }

    private static CovenantAdmissionReceipt Admission(
        CovenantTurnPlan plan,
        Guid branchId,
        ulong branchOrdinal) =>
        new(
            plan,
            branchOrdinal,
            branchId,
            branchOrdinal,
            null,
            CovenantTask6Fixture.ProviderCall(),
            10_000,
            (ulong)plan.EligibleDecisions.Length,
            [
                .. plan.EligibleDecisions.Select(static decision => new CovenantAdmissionCandidateDecision(
                    decision.Candidate.EntryId,
                    decision.Candidate.VersionId,
                    CovenantAdmissionDecision.Admitted,
                    1))
            ]);

    private static CovenantMutationIntent Intent(
        string key,
        CovenantTurnPlan plan,
        CovenantAdmissionReceipt admission,
        Guid? turnId = null,
        string toolCallId = "call-1",
        CovenantDigest? basePlanDigest = null) =>
        new(
            Guid.NewGuid(),
            CovenantMutationKind.AgentPropose,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            new CovenantMutationTarget(
                CovenantOperationScope.ForCampaign(CovenantTask6Fixture.CampaignId),
                new CovenantKey(key),
                key,
                CovenantLane.Proposed,
                Digest(11)),
            expectedLaneRevision: 0,
            reactivate: false,
            expectedKeyEpoch: 0,
            new CovenantMutationArtifact(
                key,
                $"- {key}: \"{key}\"\n",
                Digest(12),
                Digest(13),
                $"- {key}: \"{key}\"\n".Length,
                3,
                CovenantCompiler.CompilerPolicyVersion,
                CovenantCompiler.RendererPolicyVersion),
            [],
            new CovenantMutationAuthorization(
                Digest(14),
                Digest(15),
                Digest(16),
                Digest(17),
                CovenantAuthorizationMode.None,
                null,
                null),
            turnId ?? TurnId,
            toolCallId,
            basePlanDigest ?? plan.Digest,
            admission.Digest);

    private static CovenantDigest Digest(byte seed) =>
        CovenantTask6Fixture.D(seed);

}
