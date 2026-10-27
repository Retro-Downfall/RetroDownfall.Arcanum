using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorNeighborRoundStateTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("clustered")]
    [InlineData("manifold")]
    [InlineData("disconnected")]
    [InlineData("duplicate")]
    [InlineData("identical")]
    [InlineData("proportional")]
    [InlineData("uniform")]
    [InlineData("extreme-ties")]
    public void Every_round_matches_the_original_visitation_with_shuffled_inputs(string kind)
    {
        const int count = 2304;

        const int k = 47;

        RaptorPoint[] points = Points(kind, count);

        List<RoundState> referenceStates = [];

        RaptorNeighborGraphBuilder reference = new()
        {
            PairOrderForTesting = OriginalPairs,
            RoundStateForTesting = (round, candidates, offsets, indices, distances, fresh) =>
                referenceStates.Add(State(round, candidates, offsets, indices, distances, fresh, count, k)),
        };

        var expected = reference.Build(points, k, 751, CancellationToken.None);

        Assert.True(expected.IsSuccess, expected.IsFailure ? expected.Error.Code : "");

        List<RoundState> actualStates = [];

        long initializationRequests = -1;

        RaptorNeighborGraphBuilder actual = new(new CapturingProgress(value =>
        {
            if (value.Phase == "descent-allocation") initializationRequests = value.WorkSteps;
        }))
        {
            RoundStateForTesting = (round, candidates, offsets, indices, distances, fresh) =>
                actualStates.Add(State(round, candidates, offsets, indices, distances, fresh, count, k)),
        };

        RaptorPoint[] reversed = points.Reverse().ToArray();

        var built = actual.Build(reversed, k, 751, CancellationToken.None);

        Assert.True(built.IsSuccess, built.IsFailure ? built.Error.Code : "");

        Assert.Equal(expected.Value.DescentIterations, built.Value.DescentIterations);

        Assert.Equal(referenceStates, actualStates);

        Assert.Equal(2 * built.Value.DescentIterations, actualStates.Count);

        Assert.True(initializationRequests >= 0);

        Assert.Equal(actualStates.Sum(state => state.EligiblePairs), built.Value.DistanceRequests - initializationRequests);

        foreach (RoundState state in actualStates)
        {
            output.WriteLine($"round_state case={kind} round={state.Round} candidates={state.Candidates} entries={state.Entries} new_entries={state.NewEntries} eligible_pairs={state.EligiblePairs} hash={state.Hash}");
        }

        for (int row = 0; row < count; row++)
        {
            int other = count - row - 1;

            Assert.Equal(expected.Value.SourceIndices.Slice(row * k, k).ToArray().Select(index => points[index].StableId), built.Value.SourceIndices.Slice(other * k, k).ToArray().Select(index => reversed[index].StableId));

            Assert.Equal(expected.Value.Distances.Slice(row * k, k).ToArray().Select(BitConverter.DoubleToInt64Bits), built.Value.Distances.Slice(other * k, k).ToArray().Select(BitConverter.DoubleToInt64Bits));
        }

        if (kind is "identical" or "proportional" or "extreme-ties")
        {
            Assert.Equal(1, built.Value.DescentIterations);

            Assert.Equal(0, actualStates[^1].NewEntries);
        }
    }

    // Retained reference: the exact frozen382d loop over each candidate pool in its original order.
    // It intentionally repeats pairs across pools, and performs no clustering or distance arithmetic.
    private static IEnumerable<(int Left, int Right)> OriginalPairs(
        ReadOnlyMemory<int> offsetsMemory,
        ReadOnlyMemory<int> indicesMemory,
        ReadOnlyMemory<bool> freshMemory,
        CancellationToken cancellationToken)
    {
        int[] offsets = offsetsMemory.ToArray();

        int[] indices = indicesMemory.ToArray();

        bool[] fresh = freshMemory.ToArray();

        for (int row = 0; row < offsets.Length - 1; row++)
        {
            for (int left = offsets[row]; left < offsets[row + 1]; left++)
            {
                for (int right = left + 1; right < offsets[row + 1]; right++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (fresh[left] || fresh[right]) yield return (indices[left], indices[right]);
                }
            }
        }
    }

    private static RoundState State(int round, bool candidates, ReadOnlyMemory<int> offsetsMemory,
        ReadOnlyMemory<int> indicesMemory, ReadOnlyMemory<double> distancesMemory, ReadOnlyMemory<bool> freshMemory, int count, int k)
    {
        int[] offsets = offsetsMemory.ToArray();

        int[] indices = indicesMemory.ToArray();

        double[] distances = distancesMemory.ToArray();

        bool[] fresh = freshMemory.ToArray();

        Assert.Equal(count + 1, offsets.Length);

        Assert.Equal(0, offsets[0]);

        Assert.Equal(indices.Length, offsets[^1]);

        Assert.Equal(indices.Length, distances.Length);

        Assert.Equal(indices.Length, fresh.Length);

        int width = 2 * (k - 1);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Append(hash, round);

        Append(hash, candidates ? 1 : 0);

        for (int row = 0; row < count; row++)
        {
            int length = offsets[row + 1] - offsets[row];

            Assert.InRange(length, width, candidates ? width * 2 : width);

            int[] positions = Enumerable.Range(offsets[row], length).OrderBy(position => indices[position]).ToArray();

            Assert.Equal(length, positions.Select(position => indices[position]).Distinct().Count());

            Append(hash, row);

            Append(hash, length);

            foreach (int position in positions)
            {
                Assert.InRange(indices[position], 0, count - 1);

                Assert.NotEqual(row, indices[position]);

                Assert.True(double.IsFinite(distances[position]));

                Append(hash, indices[position]);

                Append(hash, BitConverter.DoubleToInt64Bits(distances[position]));

                Append(hash, fresh[position] ? 1 : 0);
            }
        }

        long eligiblePairs = 0;

        if (candidates)
        {
            // Independent bounded test witness of the frozen eligible unordered-pair union.
            // It shares neither the production transpose nor its per-left marker traversal.
            BitArray seen = new(checked(count * count));

            for (int row = 0; row < count; row++)
            {
                for (int left = offsets[row]; left < offsets[row + 1]; left++)
                {
                    for (int right = left + 1; right < offsets[row + 1]; right++)
                    {
                        if (!(fresh[left] || fresh[right])) continue;

                        int a = Math.Min(indices[left], indices[right]);

                        int b = Math.Max(indices[left], indices[right]);

                        int pair = a * count + b;

                        if (!seen[pair]) eligiblePairs++;

                        seen[pair] = true;
                    }
                }
            }
        }

        return new(round, candidates, indices.Length, fresh.Count(value => value), eligiblePairs, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];

        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);

        hash.AppendData(bytes);
    }

    private static RaptorPoint[] Points(string kind, int count)
    {
        if (kind != "extreme-ties") return RaptorNeighborQualityTests.Points(kind, count);

        return Enumerable.Range(0, count).Select(index =>
        {
            float[] vector = new float[16];

            vector[index % 3] = index % 4 < 2 ? float.MaxValue : float.Epsilon;

            if (index % 2 != 0) vector[index % 3] = -vector[index % 3];

            return new RaptorPoint("extreme-" + index.ToString("D6", CultureInfo.InvariantCulture), vector);
        }).ToArray();
    }

    private sealed class CapturingProgress(Action<RaptorNeighborProgress> action) : IProgress<RaptorNeighborProgress>
    {
        public void Report(RaptorNeighborProgress value) => action(value);
    }

    private sealed record RoundState(int Round, bool Candidates, int Entries, int NewEntries, long EligiblePairs, string Hash);
}
