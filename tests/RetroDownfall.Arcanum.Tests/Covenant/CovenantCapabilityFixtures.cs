using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The shared values every Covenant MCP capability, Ward, and egress suite builds against.
/// </summary>
internal static class CovenantCapabilityFixtures
{

    public static CanonicalCampaignContext Campaign() =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(CovenantTask6Fixture.CampaignId),
            campaignAvailabilityGeneration: 7,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: 3,
            rootIdentityDigest: CovenantTask6Fixture.D(41));

    public static CovenantAdmissionReceipt Admission(
        CovenantTurnPlan plan,
        Guid? branchId = null,
        ulong branchOrdinal = 1) =>
        new(
            plan,
            branchOrdinal,
            branchId ?? CovenantTask6Fixture.BranchId,
            branchOrdinal,
            null,
            CovenantTask6Fixture.ProviderCall(),
            10_000,
            [
                .. plan.EligibleDecisions.Select(static decision => new CovenantAdmissionCandidateDecision(
                    decision.Candidate.EntryId,
                    decision.Candidate.VersionId,
                    CovenantAdmissionDecision.Admitted,
                    1))
            ]);

    public static ProviderCallMaterializationSnapshot Materialization(
        bool unprovenanced = false,
        int sourceCount = 1) =>
        new(
            unprovenanced,
            Enumerable
                .Range(0, sourceCount)
                .Select(static index => new MaterializationSourceDigestInput(
                    Guid.Parse($"cccccccc-cccc-cccc-cccc-{index:X12}"),
                    Guid.Parse($"eeeeeeee-eeee-eeee-eeee-{index:X12}"),
                    $"attachment-{index}",
                    CovenantTask6Fixture.D((byte)(50 + (index % 100))),
                    CovenantMaterializationSourceRange.WholeSource,
                    null,
                    null,
                    ImmutableArray<MaterializationOccurrenceDigestInput>.Empty)));

    public static CovenantRetirementPreflight RetirementPreflight(
        string normalizedKey = "campaign.a",
        long targetLaneRevision = 4,
        long keyEpoch = 0) =>
        new(
            CovenantTask6Fixture.G5,
            CovenantTask6Fixture.G6,
            CovenantLane.Proposed,
            targetLaneRevision,
            normalizedKey,
            "- campaign.a: \"the operator prefers repo-root builds\"",
            CovenantTask6Fixture.D(77),
            globalFallbackApplies: false,
            keyEpoch,
            CovenantTask6Fixture.D(78));

    /// <summary>
    /// A head probe that answers from a fixed table, so a suite can choose "never created",
    /// "present at revision n", or "retired" without opening a database.
    /// </summary>
    public sealed class StubHeadProbe : ICovenantTurnHeadProbe
    {

        public Dictionary<string, CovenantLaneHeadProbe> Heads { get; } = new(StringComparer.Ordinal);

        public Error? Failure { get; set; }

        public int ProbeCount { get; private set; }

        public ValueTask<Result<CovenantLaneHeadProbe>> ProbeAsync(
            CovenantLane lane,
            string normalizedKey,
            CancellationToken cancellationToken)
        {
            ProbeCount++;

            if (Failure is { } failure)
            {
                return ValueTask.FromResult(Result<CovenantLaneHeadProbe>.Failure(failure));
            }

            CovenantOperationScope scope = CovenantOperationScope.ForCampaign(CovenantTask6Fixture.CampaignId);

            return ValueTask.FromResult(Result<CovenantLaneHeadProbe>.Success(
                Heads.TryGetValue(normalizedKey, out CovenantLaneHeadProbe? head)
                    ? head
                    : CovenantLaneHeadProbe.NotFound(scope, lane, normalizedKey, keyEpoch: 1)));
        }

        public void SetPresent(string normalizedKey, long revision) =>
            Heads[normalizedKey] = new CovenantLaneHeadProbe(
                CovenantOperationScope.ForCampaign(CovenantTask6Fixture.CampaignId),
                CovenantLane.Proposed,
                normalizedKey,
                CovenantLaneHeadPresence.Present,
                CovenantTask6Fixture.G5,
                CovenantTask6Fixture.G6,
                revision,
                CovenantOrigin.AgentProposed,
                CompiledByteCost: 32,
                KeyEpoch: 1);

        public void SetRetired(string normalizedKey, long revision) =>
            Heads[normalizedKey] = new CovenantLaneHeadProbe(
                CovenantOperationScope.ForCampaign(CovenantTask6Fixture.CampaignId),
                CovenantLane.Proposed,
                normalizedKey,
                CovenantLaneHeadPresence.Retired,
                CovenantTask6Fixture.G5,
                CovenantTask6Fixture.G6,
                revision,
                CovenantOrigin.AgentProposed,
                CompiledByteCost: 0,
                KeyEpoch: 1);

    }

    public static CovenantToolWardReceipt WardReceipt(
        CovenantWardDecision decision,
        CovenantAuthorizationMode mode = CovenantAuthorizationMode.WardInteractive) =>
        new(
            new WardEvidenceDigestInput(
                CovenantTask6Fixture.D(80),
                CovenantTask6Fixture.D(81),
                CovenantToolRiskIdentity.CovenantSensitiveEgress,
                CovenantTask6Fixture.D(82),
                CovenantEgressDestination.Provider,
                CovenantTask6Fixture.D(83),
                OperatorAuthorityEpoch: 9,
                decision),
            mode);

}
