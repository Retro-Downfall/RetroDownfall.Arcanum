using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// A discovery lane is a streaming bounded ranking. Feeding a stream through a LongRestLaneBuffer and merging what it kept must select
// exactly what merging the whole stream through the public policy selects, in any arrival order, and the hierarchy lane's order is the
// interleave that produced it.
public sealed class LongRestLaneBufferTests
{
    private static readonly LongRestTarget Anchor = new("anchor", "version-anchor", Hash(0));

    private static string Hash(int value) => value.ToString("X64");

    private static ImmutableArray<LongRestCandidate> Merge(IEnumerable<LongRestCandidate> stream)
    {
        var merged = LongRestDiscoveryPolicy.MergeCandidates(Anchor, stream);

        Assert.True(merged.IsSuccess, merged.IsFailure ? merged.Error.ToString() : "");

        return merged.Value;
    }

    private static LongRestTarget Target(int memory) => new($"memory-{memory:D3}", $"version-{memory:D3}", Hash(memory + 1));

    // Scores come from a small set so ties at every cut are common, and memory ids from a small pool so a memory recurs in a lane
    // with different scores.
    private static LongRestCandidate[] Stream(Random random, int count)
    {
        double[] cosines = [0.92, 0.921, 0.93, 0.95, 0.95, 0.99, 1, 0.5, 0.919];

        List<LongRestCandidate> stream = [];

        for (int index = 0; index < count; index++)
        {
            LongRestDiscoveryLane lane = (LongRestDiscoveryLane)random.Next(1, 5);

            LongRestTarget target = Target(random.Next(0, 90));

            stream.Add(lane switch
            {
                LongRestDiscoveryLane.ExactHash => new(target, lane, 0),

                LongRestDiscoveryLane.Lexical => new(target, lane, -random.Next(12, 40)),

                LongRestDiscoveryLane.Vector => new(target, lane, cosines[random.Next(cosines.Length)]),

                _ => new(target, lane, cosines[random.Next(cosines.Length)], random.Next(0, 8)),
            });
        }

        // One memory is only ever one exact version in a stream, as it is in a discovery scan.
        return [.. stream];
    }

    private static ImmutableArray<LongRestCandidate> ThroughBuffers(IEnumerable<LongRestCandidate> stream)
    {
        Dictionary<LongRestDiscoveryLane, LongRestLaneBuffer> buffers = Enum.GetValues<LongRestDiscoveryLane>().ToDictionary(static lane => lane, static lane => new LongRestLaneBuffer(lane));

        foreach (LongRestCandidate candidate in stream)
        {
            buffers[candidate.Lane].Add(candidate);
        }

        return Merge(buffers.Values.SelectMany(static buffer => buffer.Items));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Merging_what_the_buffers_kept_selects_exactly_what_merging_the_whole_stream_selects_in_any_arrival_order(int seed)
    {
        Random random = new(seed);

        LongRestCandidate[] stream = Stream(random, 700);

        ImmutableArray<LongRestCandidate> expected = Merge(stream);

        Assert.NotEmpty(expected);

        for (int shuffle = 0; shuffle < 12; shuffle++)
        {
            LongRestCandidate[] arrival = [.. stream.OrderBy(_ => random.Next())];

            ImmutableArray<LongRestCandidate> actual = ThroughBuffers(arrival);

            Assert.Equal(expected.ToArray(), actual.ToArray());
        }
    }

    [Fact]
    public void A_lane_buffer_holds_the_lanes_bound_best_first_and_keeps_the_better_of_a_repeated_memory()
    {
        LongRestLaneBuffer vector = new(LongRestDiscoveryLane.Vector);

        for (int memory = 0; memory < 12; memory++)
        {
            vector.Add(new(Target(memory), LongRestDiscoveryLane.Vector, 0.93 + memory * 0.001));
        }

        Assert.Equal(LongRestDiscoveryLimits.VectorTopK, vector.Count);

        Assert.Equal(Enumerable.Range(4, 8).Reverse().Select(static memory => Target(memory).MemoryId), vector.Items.Select(static candidate => candidate.Target.MemoryId));

        // A worse repeat changes nothing; a better one replaces the memory's earlier place instead of taking a second.
        vector.Add(new(Target(11), LongRestDiscoveryLane.Vector, 0.5));

        vector.Add(new(Target(4), LongRestDiscoveryLane.Vector, 0.99));

        Assert.Equal(8, vector.Items.Select(static candidate => candidate.Target.MemoryId).Distinct().Count());

        Assert.Equal(Target(4).MemoryId, vector.Items[0].Target.MemoryId);

        Assert.Equal(0.99, vector.Items[0].Score);

        // Equal scores order by memory id, then version.
        LongRestLaneBuffer ties = new(LongRestDiscoveryLane.Lexical);

        foreach (int memory in new[] { 5, 3, 9, 1 })
        {
            ties.Add(new(Target(memory), LongRestDiscoveryLane.Lexical, -20));
        }

        Assert.Equal(new[] { 1, 3, 5, 9 }.Select(static memory => Target(memory).MemoryId), ties.Items.Select(static candidate => candidate.Target.MemoryId));
    }

    [Fact]
    public void A_lane_buffer_refuses_a_candidate_of_another_lane_or_an_unknown_lane()
    {
        LongRestLaneBuffer lexical = new(LongRestDiscoveryLane.Lexical);

        Assert.Throws<ArgumentException>(() => lexical.Add(new(Target(1), LongRestDiscoveryLane.Vector, 0.95)));

        Assert.Throws<ArgumentNullException>(() => lexical.Add(null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => new LongRestLaneBuffer((LongRestDiscoveryLane)0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new LongRestLaneBuffer((LongRestDiscoveryLane)5));
    }

    [Fact]
    public void The_hierarchy_lane_is_ordered_by_rank_then_score_then_memory()
    {
        // Unranked evidence (rank 0 for all) keeps the order it always had: best summary first, then memory id.
        ImmutableArray<LongRestCandidate> unranked = Merge(
        [
            new(Target(2), LongRestDiscoveryLane.Hierarchy, 0.95),
            new(Target(1), LongRestDiscoveryLane.Hierarchy, 0.95),
            new(Target(3), LongRestDiscoveryLane.Hierarchy, 0.99),
        ]);

        Assert.Equal(new[] { 3, 1, 2 }.Select(static memory => Target(memory).MemoryId), unranked.Select(static candidate => candidate.Target.MemoryId));

        // Ranked evidence is in rank order whatever its scores and ids say.
        ImmutableArray<LongRestCandidate> ranked = Merge(
        [
            new(Target(1), LongRestDiscoveryLane.Hierarchy, 0.99, 3),
            new(Target(2), LongRestDiscoveryLane.Hierarchy, 0.93, 0),
            new(Target(3), LongRestDiscoveryLane.Hierarchy, 0.95, 2),
            new(Target(4), LongRestDiscoveryLane.Hierarchy, 0.99, 1),
        ]);

        Assert.Equal(new[] { 2, 4, 3, 1 }.Select(static memory => Target(memory).MemoryId), ranked.Select(static candidate => candidate.Target.MemoryId));

        // The same memory offered twice keeps its better (lower) rank.
        ImmutableArray<LongRestCandidate> repeated = Merge(
        [
            new(Target(1), LongRestDiscoveryLane.Hierarchy, 0.99, 5),
            new(Target(1), LongRestDiscoveryLane.Hierarchy, 0.93, 2),
        ]);

        Assert.Equal(2, Assert.Single(repeated).Rank);
    }

    [Fact]
    public void Interleaving_takes_one_leaf_from_each_selected_summary_in_turn_and_skips_the_anchor_and_leaves_already_taken()
    {
        ImmutableArray<LongRestHierarchyExpansion> expansions =
        [
            new(0.99, [Target(1), Target(2), Target(3), Target(4)]),
            new(0.97, [Target(10), Target(2), Target(11)]),
            new(0.95, []),
            new(0.93, [Target(20), Target(21), Target(22), Target(23), Target(24)]),
        ];

        LongRestCandidate[] taken = [.. LongRestDiscoveryPolicy.InterleaveExpansions(Anchor, expansions)];

        // Round 0: 1, 10, (none), 20. Round 1: 2, (2 is taken), 21. Round 2: 3, 11, 22. Round 3: 4, 23. Round 4: 24.
        Assert.Equal(new[] { 1, 10, 20, 2, 21, 3, 11, 22, 4, 23, 24 }.Select(static memory => Target(memory).MemoryId), taken.Select(static candidate => candidate.Target.MemoryId));

        Assert.Equal(Enumerable.Range(0, taken.Length), taken.Select(static candidate => candidate.Rank));

        Assert.All(taken, static candidate => Assert.Equal(LongRestDiscoveryLane.Hierarchy, candidate.Lane));

        Assert.Equal([0.99, 0.97, 0.93, 0.99, 0.93, 0.99, 0.97, 0.93, 0.99, 0.93, 0.93], taken.Select(static candidate => candidate.Score));

        // The anchor is never its own neighbour.
        Assert.DoesNotContain(LongRestDiscoveryPolicy.InterleaveExpansions(Anchor, [new(0.99, [Anchor, Target(1)])]), static candidate => candidate.Target.MemoryId == "anchor");

        Assert.Empty(LongRestDiscoveryPolicy.InterleaveExpansions(Anchor, []));
    }

    [Fact]
    public void Interleaving_stops_at_the_merged_neighbor_limit_without_letting_one_summary_fill_the_lane()
    {
        ImmutableArray<LongRestHierarchyExpansion> expansions =
        [
            .. Enumerable.Range(0, 5).Select(static summary => new LongRestHierarchyExpansion(0.99 - summary * 0.01,
                [.. Enumerable.Range(0, LongRestDiscoveryLimits.ExpandedLeaves).Select(leaf => Target(summary * 100 + leaf))])),
        ];

        LongRestCandidate[] taken = [.. LongRestDiscoveryPolicy.InterleaveExpansions(Anchor, expansions)];

        Assert.Equal(LongRestDiscoveryLimits.MergedNeighbors, taken.Length);

        // 32 places over 5 summaries taken in turn: 7, 7, 6, 6, 6. A leaf's summary is its id's hundreds.
        Assert.Equal([7, 7, 6, 6, 6], Enumerable.Range(0, 5).Select(summary => taken.Count(candidate => int.Parse(candidate.Target.MemoryId[7..]) / 100 == summary)));

        Assert.Equal(Enumerable.Range(0, 32), taken.Select(static candidate => candidate.Rank));
    }

    [Fact]
    public void Interleaving_refuses_an_unusable_input()
    {
        Assert.Throws<ArgumentException>(() => LongRestDiscoveryPolicy.InterleaveExpansions(Anchor, default));

        Assert.Throws<ArgumentException>(() => LongRestDiscoveryPolicy.InterleaveExpansions(null!, []));

        Assert.Throws<ArgumentException>(() => LongRestDiscoveryPolicy.InterleaveExpansions(Anchor, [new(0.9, default)]));
    }
}
