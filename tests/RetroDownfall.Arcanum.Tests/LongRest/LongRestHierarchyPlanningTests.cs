using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestHierarchyPlanningTests
{
    [Fact]
    public void Membership_seed_uses_a_literal_versioned_length_framed_digest()
    {
        CovenantDigest recipe = new(Enumerable.Repeat((byte)3, 32).ToArray());

        LongRestHierarchyChild a = Child(1);

        LongRestHierarchyChild b = Child(2);

        // Independently assembled 156-byte preimage with Python struct.pack and hashlib.sha256.
        const string expected = "E36B185CA46166F3102C2D3372C0522B73CE06235FA9C161EA127B7E41A5EE42";

        Assert.Equal(expected, LongRestHierarchy.Membership([a, b], recipe).ToString());

        Assert.Equal(expected, LongRestHierarchy.Membership([b, a], recipe).ToString());

        Assert.NotEqual(expected, LongRestHierarchy.Membership([a], recipe).ToString());

        Assert.NotEqual(expected, LongRestHierarchy.Membership([a, b], a.MembershipDigest).ToString());
    }

    [Fact]
    public void Synthetic_input_permutations_preserve_page_memberships_and_candidate_groups()
    {
        CovenantDigest recipe = new(new byte[32]);

        ImmutableArray<LongRestHierarchyInput> inputs = [.. Enumerable.Range(1, 128).Select(index => new LongRestHierarchyInput(Child(index), [1, 0]))];

        var forward = LongRestHierarchy.PlanPage(inputs, 1, recipe, CancellationToken.None).Value;

        var backward = LongRestHierarchy.PlanPage([.. inputs.Reverse()], 1, recipe, CancellationToken.None).Value;

        Assert.Equal(forward.Select(group => LongRestHierarchy.Membership(group, recipe)), backward.Select(group => LongRestHierarchy.Membership(group, recipe)));

        Assert.Equal(forward.SelectMany(group => group.Select(child => child.NodeId)), backward.SelectMany(group => group.Select(child => child.NodeId)));

        Assert.All(forward, group => Assert.InRange(group.Length, 2, 16));

        LongRestTarget anchor = Child(200).ExpandedTargets[0];

        LongRestCandidate[] candidates = inputs.Select(input => new LongRestCandidate(input.Child.ExpandedTargets[0], LongRestDiscoveryLane.Vector, 1)).ToArray();

        var groups = LongRestDiscoveryPolicy.GroupCandidates(anchor, LongRestDiscoveryPolicy.MergeCandidates(anchor, candidates).Value).Value;

        var reversed = LongRestDiscoveryPolicy.GroupCandidates(anchor, LongRestDiscoveryPolicy.MergeCandidates(anchor, candidates.Reverse()).Value).Value;

        Assert.Equal(groups.SelectMany(group => group.Targets), reversed.SelectMany(group => group.Targets));

        foreach (var pair in groups.Zip(reversed))
        {
            LongRestSnapshot[] snapshots = pair.First.Targets.Select(target => new LongRestSnapshot(target.MemoryId, "claim-" + target.MemoryId,
                target.ExpectedVersionId, 1, AnnalContentHashFormat.LegacyStoreDigest, target.ExpectedContentHash,
                AnnalOrigin.OperatorStated, null, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
                DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch, null, null, true, false, true, false, false, [], [])).ToArray();

            var firstReceipt = LongRestPolicy.Identify(new(LongRestTransformationKind.ExactDuplicates, [.. pair.First.Targets]), snapshots);

            var reversedReceipt = LongRestPolicy.Identify(new(LongRestTransformationKind.ExactDuplicates, [.. pair.Second.Targets.Reverse()]), [.. snapshots.Reverse()]);

            Assert.True(firstReceipt.IsSuccess);

            Assert.Equal(firstReceipt.Value, reversedReceipt.Value);
        }
    }

    [Fact]
    public void Planning_refuses_nonfinite_vectors_and_keeps_oversized_sources_out_of_folds()
    {
        CovenantDigest recipe = new(new byte[32]);

        Assert.True(LongRestHierarchy.PlanPage([new(Child(1), [float.NaN, 0]), new(Child(2), [1, 0])], 1, recipe, CancellationToken.None).IsFailure);

        Assert.True(LongRestHierarchy.PlanPage([new(Child(1) with { Content = new string('é', 16385) }, [])], 1, recipe, CancellationToken.None).IsFailure);

        Assert.True(LongRestHierarchy.PlanPage([new(Child(1) with { Content = "\ud800" }, [])], 1, recipe, CancellationToken.None).IsFailure);
    }

    private static LongRestHierarchyChild Child(int index)
    {
        CovenantDigest digest = new(Enumerable.Repeat((byte)index, 32).ToArray());

        return new(Guid.Parse($"{index:D8}-0000-4000-8000-000000000000"), 0, index, digest, digest,
            "source" + index, ContentSensitivity.None, GenerationProvenance.CreateExact([]), [],
            [new($"m-{index:D3}", $"v-{index:D3}", new string('0', 64))]);
    }
}
