using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

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

        var forward = LongRestHierarchy.PlanPage(inputs, 1, recipe, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, CancellationToken.None).Value;

        var backward = LongRestHierarchy.PlanPage([.. inputs.Reverse()], 1, recipe, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, CancellationToken.None).Value;

        Assert.Equal(forward.Select(group => LongRestHierarchy.Membership(group, recipe)), backward.Select(group => LongRestHierarchy.Membership(group, recipe)));

        Assert.Equal(forward.SelectMany(group => group.Select(child => child.NodeId)), backward.SelectMany(group => group.Select(child => child.NodeId)));

        Assert.All(forward, group => Assert.InRange(group.Length, 2, 16));

        LongRestTarget anchor = Child(200).ExpandedTargets[0];

        LongRestCandidate[] candidates = inputs.Select(input => new LongRestCandidate(input.Child.ExpandedTargets[0], LongRestDiscoveryLane.Vector, 1)).ToArray();

        var groups = LongRestDiscoveryPolicy.GroupCandidates(anchor, LongRestDiscoveryPolicy.MergeCandidates(anchor, candidates).Value, 920).Value;

        var reversed = LongRestDiscoveryPolicy.GroupCandidates(anchor, LongRestDiscoveryPolicy.MergeCandidates(anchor, candidates.Reverse()).Value, 920).Value;

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

        Assert.True(LongRestHierarchy.PlanPage([new(Child(1), [float.NaN, 0]), new(Child(2), [1, 0])], 1, recipe, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, CancellationToken.None).IsFailure);

        // A single child no request can hold is not an error: it is carried on its own, like any child without a companion.
        Assert.Equal([1], LongRestHierarchy.PlanPage([new(Child(1) with { Content = new string('é', 16385) }, [])], 1, recipe, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, CancellationToken.None).Value.Select(group => group.Length));

        Assert.True(LongRestHierarchy.PlanPage([new(Child(1) with { Content = "\ud800" }, [])], 1, recipe, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, CancellationToken.None).IsFailure);
    }

    [Fact]
    public void Children_whose_summary_request_exceeds_the_budget_are_not_planned_into_one_fold()
    {
        CovenantDigest recipe = new(new byte[32]);

        // 2 x 16,300 raw bytes is under the 32,768 raw ceiling, but the request (334 fixed bytes plus two 17-byte observation heads and tails) is not.
        ImmutableArray<LongRestHierarchyInput> page = [new(LongRestPackingData.Child(1, new string('x', 16300)), [1, 0]), new(LongRestPackingData.Child(2, new string('y', 16300)), [1, 0])];

        var planned = LongRestHierarchy.PlanPage(page, 1, recipe, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, CancellationToken.None).Value;

        Assert.Equal([1, 1], planned.Select(group => group.Length));

        // Two children that do fit the same budget are one fold.
        ImmutableArray<LongRestHierarchyInput> smaller = [new(LongRestPackingData.Child(1, new string('x', 15000)), [1, 0]), new(LongRestPackingData.Child(2, new string('y', 15000)), [1, 0])];

        Assert.Equal([2], LongRestHierarchy.PlanPage(smaller, 1, recipe, LongRestDiscoveryLimits.ProviderInputUtf8Bytes, CancellationToken.None).Value.Select(group => group.Length));

        // And the same pair is split by a smaller budget: the budget comes from the recipe, not from a constant.
        Assert.Equal([1, 1], LongRestHierarchy.PlanPage(smaller, 1, recipe, 16000, CancellationToken.None).Value.Select(group => group.Length).Order());
    }

    [Fact]
    public void A_child_the_budget_cannot_hold_alone_is_carried_on_its_own_and_never_holds_back_the_others_and_a_blank_one_stays_invalid()
    {
        CovenantDigest recipe = new(new byte[32]);

        LongRestHierarchyChild big = LongRestPackingData.Child(1, new string('x', 5000));

        ImmutableArray<LongRestHierarchyInput> page = [new(big, [1, 0]), new(LongRestPackingData.Child(2, "small"), [1, 0]), new(LongRestPackingData.Child(3, "other"), [1, 0])];

        // At 8192 the three fold together.
        Assert.Equal([3], LongRestHierarchy.PlanPage(page, 1, recipe, 8192, CancellationToken.None).Value.Select(group => group.Length));

        // At the smallest budget the 5 KB child fits no request at all: the other two fold and it is carried, exactly once, in a group of its own.
        var planned = LongRestHierarchy.PlanPage(page, 1, recipe, LongRestPromptLayout.MinimumInputUtf8Bytes, CancellationToken.None);

        Assert.True(planned.IsSuccess);

        Assert.Equal([[2, 3], [1]], planned.Value.Select(group => group.Select(child => child.Group).Order().Select(value => (int)value).ToArray()).OrderByDescending(static group => group.Length));

        Assert.Equal(3, planned.Value.Sum(static group => group.Length));

        Assert.True(LongRestHierarchy.IsOversize(big, LongRestPromptLayout.MinimumInputUtf8Bytes));

        Assert.False(LongRestHierarchy.IsOversize(big, 8192));

        var blank = LongRestHierarchy.PlanPage([new(LongRestPackingData.Child(1, " \n "), []), new(LongRestPackingData.Child(2, "small"), [])], 1, recipe, 8192, CancellationToken.None);

        Assert.Equal(ErrorCodes.LongRest.InvalidRequest, blank.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void A_child_is_oversize_exactly_when_a_request_holding_only_it_is_one_byte_over_the_budget(int overBy)
    {
        long fixedBytes = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Summary);

        // One 'x' costs one byte beyond the head, the ordinal and the tail, so the observation grows byte for byte with the content.
        long overhead = LongRestPromptLayout.SummaryObservationBytes("x") - 1;

        int budget = LongRestPromptLayout.MinimumInputUtf8Bytes + 1000;

        int content = checked((int)(budget - fixedBytes - overhead) + overBy);

        LongRestHierarchyChild child = LongRestPackingData.Child(1, new string('x', content));

        Assert.Equal(overBy > 0, LongRestHierarchy.IsOversize(child, budget));

        Assert.Equal(fixedBytes + LongRestPromptLayout.SummaryObservationBytes(child.Content), budget + overBy);
    }

    [Fact]
    public void A_summary_too_large_for_a_request_is_carried_at_every_layer_and_a_leaf_carried_from_below_stays_a_singleton()
    {
        CovenantDigest recipe = new(new byte[32]);

        LongRestHierarchyChild big = LongRestPackingData.Child(1, new string('x', 5000)) with { Layer = 1 };

        ImmutableArray<LongRestHierarchyInput> page =
        [
            new(big, [1, 0]),
            new(LongRestPackingData.Child(2, "small") with { Layer = 1 }, [1, 0]),
            new(LongRestPackingData.Child(3, "other") with { Layer = 1 }, [1, 0]),
            new(LongRestPackingData.Child(4, "a leaf carried up from layer one"), [1, 0]),
        ];

        var planned = LongRestHierarchy.PlanPage(page, 2, recipe, LongRestPromptLayout.MinimumInputUtf8Bytes, CancellationToken.None).Value;

        Assert.Equal(4, planned.Sum(static group => group.Length));

        Assert.Equal([1, 1, 2], planned.Select(static group => group.Length).Order());

        Assert.Contains(planned, group => group.Length == 1 && group[0].NodeId == big.NodeId);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(32769)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Planning_requires_a_budget_within_the_recipe_bounds(int budget) =>
        Assert.Equal(ErrorCodes.LongRest.InvalidRequest, LongRestHierarchy.PlanPage([new(Child(1), []), new(Child(2), [])], 1, new(new byte[32]), budget, CancellationToken.None).Error.Code);

    private static LongRestHierarchyChild Child(int index)
    {
        CovenantDigest digest = new(Enumerable.Repeat((byte)index, 32).ToArray());

        return new(Guid.Parse($"{index:D8}-0000-4000-8000-000000000000"), 0, index, digest, digest,
            "source" + index, ContentSensitivity.None, GenerationProvenance.CreateExact([]), [],
            [new($"m-{index:D3}", $"v-{index:D3}", new string('0', 64))]);
    }
}
