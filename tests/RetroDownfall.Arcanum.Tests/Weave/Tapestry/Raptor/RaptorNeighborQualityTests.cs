using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorNeighborQualityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("identical", 160)]
    [InlineData("proportional", 160)]
    [InlineData("proportional", 2304)]
    [InlineData("clustered", 2048)]
    [InlineData("clustered", 2049)]
    [InlineData("identical", 2304)]
    [InlineData("duplicate", 4096)]
    [InlineData("uniform", 2304)]
    [InlineData("clustered", 160)]
    [InlineData("manifold", 160)]
    [InlineData("duplicate", 160)]
    [InlineData("disconnected", 160)]
    [InlineData("clustered", 2304)]
    [InlineData("manifold", 2304)]
    [InlineData("duplicate", 2304)]
    [InlineData("disconnected", 2304)]
    public void ApproximateGraph_RecallAtLeastPoint95(string kind, int count)
    {
        var points = Points(kind, count);

        const int k = 16;

        long before = GC.GetAllocatedBytesForCurrentThread();

        var watch = Stopwatch.StartNew();

        var result = new RaptorNeighborGraphBuilder().Build(points, k, 751, default);

        watch.Stop();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result.IsSuccess, result.Error.Code);

        long hits = 0;

        for (int row = 0; row < count; row++)
        {
            int[] expected = Oracle(points, row, k - 1);

            var actual = result.Value.SourceIndices.Slice(row * k + 1, k - 1).ToArray();

            hits += actual.Intersect(expected).Count();

            Assert.Equal(row, result.Value.SourceIndices.Span[row * k]);

            Assert.Equal(k - 1, actual.Distinct().Count());

            Assert.DoesNotContain(row, actual);

            for (int neighbor = 1; neighbor < k; neighbor++)
            {
                int index = result.Value.SourceIndices.Span[row * k + neighbor];

                Assert.InRange(Math.Abs(Distance(points[row].Vector.Span, points[index].Vector.Span) - result.Value.Distances.Span[row * k + neighbor]), 0, 1e-12);
            }
        }

        double recall = (double)hits / (count * (k - 1));

        output.WriteLine($"{kind}: n={count}, d={points[0].Vector.Length}, k={k} including self; non-self recall={recall:F8}; elapsed_ms={watch.Elapsed.TotalMilliseconds:F2}; allocated_bytes={allocated}; evaluations={result.Value.DistanceEvaluations}; descent_iterations={result.Value.DescentIterations}");

        Assert.True(recall >= (count <= 2048 ? 1 : 0.95), $"Non-self recall {recall} below the exact/approximate gate for {kind}.");

        Assert.Equal(count > 2048, result.Value.DescentIterations > 0);
    }

    [Fact]
    public void LargeGraph_DoesNotAllocateSquareDistanceMatrix()
    {
        var points = Points("manifold", 6000);

        long before = GC.GetAllocatedBytesForCurrentThread();

        var result = new RaptorNeighborGraphBuilder().Build(points, 12, 1, default);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result.IsSuccess, result.Error.Code);

        Assert.Equal(6000 * 12, result.Value.SourceIndices.Length);

        Assert.InRange(result.Value.DistanceEvaluations, 1, (long)6000 * 5999 / 4);

        Assert.True(result.Value.DescentIterations > 0);

        output.WriteLine($"allocation: n=6000,d=16,k=12 bytes={allocated}, evaluations={result.Value.DistanceEvaluations}");

        Assert.True(allocated < 80_000_000, $"Allocated {allocated} bytes; a float square matrix alone is 144MB.");
    }

    internal static RaptorPoint[] Points(string kind, int count)
    {
        Random random = new(177);

        var centers = Enumerable.Range(0, 8).Select(_ => Enumerable.Range(0, 16).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray()).ToArray();

        return Enumerable.Range(0, count).Select(i =>
        {
            float[] vector = new float[kind == "uniform" ? 64 : 16];

            for (int d = 0; d < vector.Length; d++)
            {
                vector[d] = kind switch
                {
                    "proportional" => d / 2 == i % 8 ? (d % 2 == 0 ? 3 : 4) * (i % 7 + 1) : 0,
                    "identical" => d == 0 ? 1 : 0,
                    "uniform" => (float)(random.NextDouble() * 2 - 1),
                    "manifold" => (float)(Math.Sin((i + 0.1) * 2 * Math.PI / count * (d / 2 + 1) + (d % 2) * Math.PI / 2)),
                    "duplicate" => d == (i / 32) % 16 ? 1 : 0,
                    "disconnected" => d / 2 == i % 8 ? (float)(1 + random.NextDouble() * 0.2) : 0,
                    _ => centers[i % 8][d] + (float)(random.NextDouble() * 0.5 - 0.25),
                };
            }

            return new RaptorPoint($"sample-{i:D6}", vector);
        }).ToArray();
    }

    private static int[] Oracle(RaptorPoint[] points, int row, int k) => Enumerable.Range(0, points.Length).Where(i => i != row).Select(i => (Index: i, Distance: Distance(points[row].Vector.Span, points[i].Vector.Span))).OrderBy(pair => pair.Distance).ThenBy(pair => points[pair.Index].StableId, StringComparer.Ordinal).Take(k).Select(pair => pair.Index).ToArray();

    private static double Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        double dot = 0, a = 0, b = 0;

        for (int d = 0; d < left.Length; d++)
        {
            dot += (double)left[d] * right[d];

            a += (double)left[d] * left[d];

            b += (double)right[d] * right[d];
        }

        return Math.Clamp(1 - dot / Math.Sqrt(a * b), 0, 2);
    }
}
