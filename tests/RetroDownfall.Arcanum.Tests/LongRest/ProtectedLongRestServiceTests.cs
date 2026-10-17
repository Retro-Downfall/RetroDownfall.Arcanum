using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class ProtectedLongRestServiceTests
{
    [Fact]
    public void Protected_work_companion_matches_independent_literal_grammar()
    {
        Assert.Equal("D52FE534EA4458DC4D34508E2880827392D11ACC3C47F9614F3BF9E5F7E64E35", RetroDownfall.Arcanum.Infrastructure.Data.LongRest.SagaLongRestService.ProtectedEvidenceDigest(
            new CovenantDigest(Convert.FromHexString(new string('1', 64))), new string('2', 64)).ToString());
    }

    private static readonly DateTimeOffset At = new(638950464000000000, TimeSpan.Zero);

    [Fact]
    public void V2_grammar_matches_independently_frozen_vectors()
    {
        LongRestSnapshot a = Snapshot("a", AnnalOrigin.OperatorStated) with { ContentHash = new string('1', 64) };

        LongRestSnapshot b = Snapshot("b", AnnalOrigin.AgentAsserted) with { ContentHash = new string('2', 64) };

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.Parse("10000000-0000-0000-0000-000000000001")]);

        CovenantDigest first = new(Enumerable.Repeat((byte)0x33, 32).ToArray());

        CovenantDigest second = new(Enumerable.Repeat((byte)0x44, 32).ToArray());

        LongRestProtectedEvidence evidence = new(new(Enumerable.Repeat((byte)0x55, 32).ToArray()), provenance, [first, second]);

        (string MemoryId, Guid LabelId, CovenantDigest LabelDigest)[] manifests = [("a", Guid.Parse("20000000-0000-0000-0000-000000000001"), first), ("b", Guid.Parse("20000000-0000-0000-0000-000000000002"), second)];

        string input = ProtectedLongRestCanonicalHash.Input([a, b], evidence, manifests);

        Assert.Equal("D2291C800045E17A7AEAD3DAD9B9F79F9ED890609A8D141732B6B34C92F23099", input);

        Assert.Equal("1DA93693FD5FDA747AE12D6AA4EDA9B6AA4906D16A54207A930137372F4DF67C", ProtectedLongRestCanonicalHash.Receipt(LongRestTransformationKind.EquivalentObservations, input, null));

        Assert.Equal("24BD0DC4379C0860D40C23E9534DE4301DAF3283F396CCA9BB2F863374C4DAAB", ProtectedLongRestCanonicalHash.Output(LongRestTransformationKind.EquivalentObservations, LongRestOutcome.Applied, LongRestReason.None, a, [b]));

        manifests[0].LabelId = Guid.Parse("20000000-0000-0000-0000-000000000003");

        Assert.NotEqual(input, ProtectedLongRestCanonicalHash.Input([a, b], evidence, manifests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Authorized_partition_keeps_historical_sensitivity_and_operator_priority(bool campaign)
    {
        (LongRestSourceLeaf[] leaves, LongRestProtectedEvidence evidence) = Leaves(campaign);

        LongRestRequest request = Request(leaves);

        LongRestReceipt old = Require(LongRestPolicy.Evaluate(request, leaves.Select(leaf => leaf.Snapshot).ToArray()));

        Assert.Equal(LongRestReason.Protected, old.Reason);

        LongRestReceipt current = Require(ProtectedLongRestPolicy.Evaluate(request, leaves, evidence));

        Assert.Equal(LongRestOutcome.Applied, current.Outcome);

        Assert.Equal(2, current.PolicyVersion);

        Assert.Equal(leaves[1].Snapshot.VersionId, current.SurvivorVersionId);

        Assert.NotEqual(old.ReceiptId, current.ReceiptId);

        Assert.All(leaves, leaf => Assert.Equal(ContentSensitivity.None, leaf.Snapshot.Sensitivity));

        LongRestRequest supersession = request with { Kind = LongRestTransformationKind.Supersession, SurvivorVersionId = leaves[0].Snapshot.VersionId };

        Assert.Equal(leaves[0].Snapshot.VersionId, Require(ProtectedLongRestPolicy.Evaluate(supersession, leaves, evidence)).SurvivorVersionId);
    }

    [Theory]
    [InlineData("pinned", LongRestReason.Pinned)]
    [InlineData("retired", LongRestReason.Retired)]
    [InlineData("embedding", LongRestReason.EmbeddingMissing)]
    [InlineData("validity", LongRestReason.IncompatibleValidity)]
    [InlineData("dependency", LongRestReason.DependencyConflict)]
    [InlineData("incoming", LongRestReason.DependencyConflict)]
    [InlineData("output", LongRestReason.DependencyConflict)]
    [InlineData("suppressed", LongRestReason.AlreadyConsolidated)]
    public void Protected_policy_preserves_every_conservative_guard(string fault, LongRestReason expected)
    {
        (LongRestSourceLeaf[] leaves, LongRestProtectedEvidence evidence) = Leaves(false);

        int index = fault == "embedding" ? 1 : 0;

        LongRestSnapshot snapshot = leaves[index].Snapshot;

        snapshot = fault switch
        {
            "pinned" => snapshot with { PinnedAtUtc = At },
            "retired" => snapshot with { RetiredAtUtc = At },
            "embedding" => snapshot with { HasEmbedding = false },
            "validity" => snapshot with { ValidToUtc = At.AddDays(1) },
            "dependency" => snapshot with { Dependencies = [new("other", AnnalDependencyRelation.Corroborates, 1)] },
            "incoming" => snapshot with { IncomingDependencies = [new("other", AnnalDependencyRelation.DerivedFrom, true)] },
            "output" => snapshot with { IsTransformationOutput = true },
            "suppressed" => snapshot with { IsConsolidated = true },
            _ => throw new InvalidOperationException(),
        };

        leaves[index] = leaves[index] with { Snapshot = snapshot };

        LongRestReceipt result = Require(ProtectedLongRestPolicy.Evaluate(Request(leaves), leaves, evidence));

        Assert.Equal(LongRestOutcome.NoChange, result.Outcome);

        Assert.Equal(expected, result.Reason);

        Assert.Empty(result.SupersededVersionIds);

        Assert.All(leaves, leaf => Assert.NotNull(leaf.CanonicalLabel));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("swapped")]
    [InlineData("revision")]
    [InlineData("content")]
    [InlineData("partition")]
    [InlineData("provenance")]
    [InlineData("stale")]
    public void Invalid_retained_proof_or_stale_head_cannot_become_an_applied_receipt(string fault)
    {
        (LongRestSourceLeaf[] leaves, LongRestProtectedEvidence evidence) = Leaves(false);

        leaves[0] = fault switch
        {
            "missing" => leaves[0] with { CanonicalLabel = null },
            "swapped" => leaves[0] with { CanonicalLabel = leaves[1].CanonicalLabel },
            "revision" => leaves[0] with { SagaRevision = 99 },
            "content" => leaves[0] with { SagaContentDigest = new CovenantDigest(new byte[32]) },
            "provenance" => leaves[0] with { Provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]) },
            "stale" => leaves[0] with { Snapshot = leaves[0].Snapshot with { IsCurrent = false } },
            _ => leaves[0],
        };

        if (fault == "partition")
        {
            evidence = evidence with { PartitionDigest = new CovenantDigest(new byte[32]) };
        }

        Assert.True(ProtectedLongRestPolicy.Evaluate(Request(leaves), leaves, evidence).IsFailure);
    }

    private static LongRestRequest Request(IEnumerable<LongRestSourceLeaf> leaves) => new(LongRestTransformationKind.EquivalentObservations, leaves.Select(leaf => leaf.Target).ToArray());

    private static LongRestReceipt Require(RetroDownfall.Arcanum.Core.Primitives.Result<LongRestReceipt> result)
    {
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");

        return result.Value;
    }

    private static LongRestSnapshot Snapshot(string id, AnnalOrigin origin) => new(id, "claim-" + id, "version-" + id, 1,
        AnnalContentHashFormat.LegacyStoreDigest, Convert.ToHexString(AnnalContentDigest.ForSagaMemory("same")), origin, null,
        SagaMemoryScopeKind.Global, null, ContentSensitivity.None, At, null, At, null, null, true, false, true, true, false, [], []);

    private static (LongRestSourceLeaf[] Leaves, LongRestProtectedEvidence Evidence) Leaves(bool campaign)
    {
        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.Parse("10000000-0000-0000-0000-000000000001")]);

        Guid? campaignId = campaign ? Guid.Parse("30000000-0000-0000-0000-000000000001") : null;

        LongRestSourceLeaf[] leaves = Enumerable.Range(1, 2).Select(index =>
        {
            Guid id = Guid.Parse($"40000000-0000-0000-0000-{index:000000000000}");

            LongRestSnapshot snapshot = Snapshot(id.ToString("D"), index == 1 ? AnnalOrigin.AgentAsserted : AnnalOrigin.OperatorStated)
                with { ScopeKind = campaign ? SagaMemoryScopeKind.Campaign : SagaMemoryScopeKind.Global, CampaignId = campaignId };

            ArtifactSensitivityLabel label = new(Guid.NewGuid(), SensitiveArtifactKind.Saga, id, null, campaignId, null,
                index == 1 ? 0UL : 37UL, DerivedArtifactContentDigest.ForText("same"), ContentSensitivity.CovenantDerived, provenance, null, null, null, At);

            return new LongRestSourceLeaf(snapshot, index, 1, null, ContentSensitivity.CovenantDerived, provenance, [label.LabelDigest],
                CanonicalLabel: label, SagaContentDigest: label.ArtifactContentDigest, SagaRevision: label.ArtifactRevision);
        }).ToArray();

        return (leaves, new(leaves[0].CanonicalLabel!.SensitivityDigest, provenance, [.. leaves.Select(leaf => leaf.CanonicalLabel!.LabelDigest)]));
    }
}
