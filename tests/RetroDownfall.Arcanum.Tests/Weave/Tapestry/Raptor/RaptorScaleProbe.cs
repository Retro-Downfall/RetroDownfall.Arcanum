using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

/// <summary>Explicit offline numerical measurements; never discovers or calls an embedding provider.</summary>
[Trait("Category", "RaptorScaleProbe")]
public sealed class RaptorScaleProbe(ITestOutputHelper output)
{
    private const string OptIn = "ARCANUM_RAPTOR_SCALE_PROBE";

    [SkippableFact]
    public void Complete_global_pipeline_at_one_thousand()
    {
        RequireOptIn();

        RaptorPoint[] points = Corpus(1000);

        int k = Neighbors(points.Length);

        Describe(points, k);

        long evaluations = 0;

        var graph = Measure("neighbors", 30, token => new RaptorNeighborGraphBuilder(new Progress<RaptorNeighborProgress>(value => evaluations = value.DistanceEvaluations)).Build(points, k, 751, token));

        Assert.True(graph.IsSuccess, graph.IsFailure ? graph.Error.Code : "");

        Assert.Equal(499500, graph.Value.DistanceEvaluations);

        output.WriteLine($"graph edges={graph.Value.SourceIndices.Length} evaluations={graph.Value.DistanceEvaluations} iterations={graph.Value.DescentIterations} requests={graph.Value.DistanceRequests} memo_lookups={graph.Value.MemoLookups} memo_hits={graph.Value.MemoHits} memo_evictions={graph.Value.MemoEvictions} memo_bytes={graph.Value.MemoAllocatedBytes} observer_evaluations={evaluations} fingerprint={GraphHash(graph.Value)}");

        AssertRecall(points, graph.Value, 32);

        var fuzzy = Measure("fuzzy", 30, token => new RaptorFuzzyGraphBuilder().Build(graph.Value, token));

        Assert.True(fuzzy.IsSuccess, fuzzy.IsFailure ? fuzzy.Error.Code : "");

        output.WriteLine($"fuzzy edges={fuzzy.Value.SourceIndices.Length}");

        RaptorUmapProgress optimized = default;

        var layout = Measure("umap", 60, token => new RaptorUmapOptimizer(new Progress<RaptorUmapProgress>(value => optimized = value)).Optimize(fuzzy.Value, 10, 752, token), () => $"optimizer_progress epoch={optimized.Epoch} sampled_edges={optimized.SampledEdges} negative_samples={optimized.NegativeSamples}");

        Assert.True(layout.IsSuccess, layout.IsFailure ? layout.Error.Code : "");

        output.WriteLine($"umap rows={layout.Value.Count} epoch={optimized.Epoch} sampled_edges={optimized.SampledEdges} negative_samples={optimized.NegativeSamples}");

        double[][] reduced = layout.Value.OrderBy(point => point.SourceIndex).Select(point => point.Coordinates.ToArray()).ToArray();

        Assert.Equal(Enumerable.Range(0, points.Length), layout.Value.Select(point => point.SourceIndex).Order());

        Assert.All(reduced, row => Assert.Equal(10, row.Length));

        Assert.All(reduced.SelectMany(row => row), value => Assert.True(double.IsFinite(value)));

        output.WriteLine($"layout fingerprint={DoubleHash(reduced.SelectMany(row => row))}");

        List<RaptorGmmCandidate> candidates = [];

        RaptorGmmProgress fitted = default;

        var fit = Measure("gmm", 120, token => new RaptorGaussianMixture(new Progress<RaptorGmmProgress>(value => fitted = value), new Progress<RaptorGmmCandidate>(candidate =>
        {
            candidates.Add(candidate);

            output.WriteLine(FormattableString.Invariant($"candidate components={candidate.Components} refusal={candidate.RefusalCode ?? "none"} bic={candidate.Bic:R}"));
        })).SelectByBic(reduced, 753, token), () => $"gmm_progress attempted={candidates.Count} components={fitted.Components} iteration={fitted.Iteration} samples={fitted.Samples}");

        Assert.True(fit.IsSuccess, fit.IsFailure ? fit.Error.Code : "");

        Assert.Equal(Enumerable.Range(1, 49), candidates.Select(candidate => candidate.Components));

        output.WriteLine($"gmm attempted={candidates.Count} converged={candidates.Count(candidate => candidate.RefusalCode is null)} selected={fit.Value.Weights.Length} selected_iterations={fit.Value.Iterations} last_component={fitted.Components} last_iteration={fitted.Iteration}");

        output.WriteLine($"fit fingerprint={DoubleHash(fit.Value.Weights.Concat(fit.Value.Means.SelectMany(row => row)).Concat(fit.Value.Covariances.SelectMany(matrix => matrix.SelectMany(row => row))))}");
    }

    [SkippableFact]
    public void Complete_real_clusterer_at_one_thousand()
    {
        RequireOptIn();

        RaptorPoint[] points = Corpus(1000);

        Describe(points, Neighbors(points.Length));

        TapestryClusterIdentity identity = new(new(TapestryScopeKind.Workspace, "/synthetic/raptor-scale"), 1, RaptorStage.GlobalUmap, "", points.Select(point => point.StableId));

        var result = Measure("clusterer", 120, token => new RaptorClusterer().Cluster(points, identity, token));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "");

        Assert.Empty(result.Value.Unassigned);

        Assert.Equal(Enumerable.Range(0, points.Length), result.Value.Clusters.SelectMany(cluster => cluster.Members).Select(member => member.PointIndex).Distinct().Order());

        output.WriteLine($"clusterer completed=true clusters={result.Value.Clusters.Length} memberships={result.Value.Clusters.Sum(cluster => cluster.Members.Length)} covered={points.Length} fingerprint={DoubleHash(result.Value.Clusters.SelectMany(cluster => cluster.Members.SelectMany(member => new double[] { member.PointIndex, member.GlobalComponent, member.LocalComponent, member.GlobalPosterior, member.LocalPosterior, (int)member.Repair })))}");
    }

    [SkippableFact]
    public void Complete_approximate_graph_at_ten_thousand_uses_less_work_than_exact()
    {
        RequireOptIn();

        const int count = 10000;

        const long exactEvaluations = (long)count * (count - 1) / 2;

        RaptorPoint[] points = Corpus(count);

        int k = Neighbors(count);

        Describe(points, k);

        using var budget = new Budget(240);

        RaptorNeighborProgress observed = default;

        var observer = new Progress<RaptorNeighborProgress>(value =>
        {
            observed = value;

            if (value.DistanceEvaluations >= exactEvaluations) budget.Cancel("work-limit");
        });

        Stopwatch watch = Stopwatch.StartNew();

        long before = GC.GetTotalAllocatedBytes(true);

        bool completed = false;

        try
        {
            var graph = new RaptorNeighborGraphBuilder(observer).Build(points, k, 751, budget.Token);

            Assert.True(graph.IsSuccess, graph.IsFailure ? graph.Error.Code : "");

            completed = true;

            output.WriteLine($"graph edges={graph.Value.SourceIndices.Length} evaluations={graph.Value.DistanceEvaluations} iterations={graph.Value.DescentIterations} requests={graph.Value.DistanceRequests} memo_lookups={graph.Value.MemoLookups} memo_hits={graph.Value.MemoHits} memo_evictions={graph.Value.MemoEvictions} memo_bytes={graph.Value.MemoAllocatedBytes} fingerprint={GraphHash(graph.Value)}");

            Assert.True(graph.Value.DistanceEvaluations < exactEvaluations, $"Actual distance evaluations {graph.Value.DistanceEvaluations} must be strictly below exact all-pairs {exactEvaluations}.");

            AssertMemoCounters(graph.Value);

            AssertRecall(points, graph.Value, 32);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(budget.Reason == "work-limit"
                ? $"Work acceptance failed: approximate search performed {observed.DistanceEvaluations} actual distances during {observed.Phase}, reaching/exceeding exact all-pairs {exactEvaluations} before completion."
                : $"INCOMPLETE: containment cancellation ({budget.Reason}) during {observed.Phase} after {observed.DistanceEvaluations} actual distances; this does not prove a work-ceiling defect.");
        }
        finally
        {
            output.WriteLine(FormattableString.Invariant($"work_acceptance completed={completed} n={count} phase={observed.Phase ?? "validation"} work_steps={observed.WorkSteps} memo_hits={observed.MemoHits} memo_evictions={observed.MemoEvictions} initialized_rows={observed.InitializedRows} processed_rows={observed.ProcessedRows} pass={observed.Pass} actual_reported_evaluations={observed.DistanceEvaluations} exact_limit={exactEvaluations} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} allocated_bytes={GC.GetTotalAllocatedBytes(true) - before} start_working_set={budget.StartWorkingSet} peak_working_set={budget.PeakWorkingSet} reason={budget.Reason} cancel_latency_ms={budget.CancelLatencyMilliseconds:F3}"));
        }
    }

    [SkippableFact]
    public void Cancellation_at_ten_thousand_occurs_in_actual_descent_with_the_supplied_token()
    {
        RequireOptIn();

        RaptorPoint[] points = Corpus(10000);

        using var budget = new Budget(60);

        RaptorNeighborProgress observed = default;

        var observer = new Progress<RaptorNeighborProgress>(value =>
        {
            observed = value;

            if (value.Phase == "descent") budget.Cancel("descent-probe");
        });

        Stopwatch watch = Stopwatch.StartNew();

        try
        {
            OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() => new RaptorNeighborGraphBuilder(observer).Build(points, Neighbors(points.Length), 751, budget.Token));

            Assert.Equal("descent-probe", budget.Reason);

            Assert.Equal("descent", observed.Phase);

            Assert.Equal(budget.Token, canceled.CancellationToken);

            Assert.Equal(points.Length, observed.InitializedRows);
        }
        finally
        {
            output.WriteLine(FormattableString.Invariant($"descent_cancel phase={observed.Phase} work_steps={observed.WorkSteps} evaluations={observed.DistanceEvaluations} memo_hits={observed.MemoHits} memo_evictions={observed.MemoEvictions} initialized_rows={observed.InitializedRows} processed_rows={observed.ProcessedRows} pass={observed.Pass} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} reason={budget.Reason} cancel_latency_ms={budget.CancelLatencyMilliseconds:F3} start_working_set={budget.StartWorkingSet} peak_working_set={budget.PeakWorkingSet}"));
        }
    }

    [SkippableFact]
    public void Partial_neighbor_pilot_at_ten_thousand() => PartialNeighborPilot(10000);

    [SkippableFact]
    public void Partial_neighbor_pilot_at_fifty_thousand() => PartialNeighborPilot(50000);

    [SkippableFact]
    public void Phase_reaching_neighbor_pilot_at_fifty_thousand() => PartialNeighborPilot(50000, 180, 50_000_000);

    [SkippableFact]
    public void First_descent_neighbor_pilot_at_fifty_thousand() => PartialNeighborPilot(50000, 600, 150_000_000);

    private bool PartialNeighborPilot(int count, int seconds = 15, long evaluationLimit = 2_000_000)
    {
        if (count != 100) RequireOptIn();

        RaptorPoint[] points = Corpus(count);

        int k = Neighbors(count);

        Describe(points, k);

        using var budget = new Budget(seconds);

        Stopwatch watch = Stopwatch.StartNew();

        RaptorNeighborProgress observed = default;

        string? markedPhase = null;

        int markedPass = -1;

        int markedWindow = -1;

        int checkpoints = 0;

        long canceledAt = 0;

        var observer = new Progress<RaptorNeighborProgress>(value =>
        {
            observed = value;

            int window = value.ProcessedRows / 4096;

            if (checkpoints < 1024 && (value.Phase != markedPhase || value.Pass != markedPass || window != markedWindow))
            {
                output.WriteLine(FormattableString.Invariant($"checkpoint elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} phase={value.Phase} pass={value.Pass} initialized_rows={value.InitializedRows} processed_rows={value.ProcessedRows} requests={value.WorkSteps} evaluations={value.DistanceEvaluations} hits={value.MemoHits} evictions={value.MemoEvictions}"));

                markedPhase = value.Phase;

                markedPass = value.Pass;

                markedWindow = window;

                checkpoints++;
            }

            if (value.DistanceEvaluations >= evaluationLimit)
            {
                canceledAt = Stopwatch.GetTimestamp();

                budget.Cancel("evaluations");
            }
        });

        long before = GC.GetTotalAllocatedBytes(true);

        bool completed = false;

        long completeEvaluations = 0;

        try
        {
            var graph = new RaptorNeighborGraphBuilder(observer).Build(points, k, 751, budget.Token);

            Assert.True(graph.IsSuccess, graph.IsFailure ? graph.Error.Code : "");

            completed = true;

            completeEvaluations = graph.Value.DistanceEvaluations;

            Assert.Equal(count, graph.Value.PointCount);

            Assert.Equal(count * k, graph.Value.SourceIndices.Length);

            if (count > 2048) AssertMemoCounters(graph.Value);
        }
        catch (OperationCanceledException error)
        {
            Assert.Equal(budget.Token, error.CancellationToken);

            Assert.True(budget.Token.IsCancellationRequested);
        }
        finally
        {
            output.WriteLine(FormattableString.Invariant($"partial completed={completed} complete_evaluations={completeEvaluations} n={count} last_reported_phase={observed.Phase ?? "validation"} work_steps={observed.WorkSteps} memo_hits={observed.MemoHits} memo_evictions={observed.MemoEvictions} initialized_rows={observed.InitializedRows} last_reported_rows={observed.ProcessedRows} last_reported_pass={observed.Pass} last_reported_evaluations={observed.DistanceEvaluations} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} allocated_bytes={GC.GetTotalAllocatedBytes(true) - before} start_working_set={budget.StartWorkingSet} peak_working_set={budget.PeakWorkingSet} reason={budget.Reason} cancel_latency_ms={(canceledAt == 0 ? budget.CancelLatencyMilliseconds : Stopwatch.GetElapsedTime(canceledAt).TotalMilliseconds):F3}"));
        }

        Assert.True(completed || budget.Token.IsCancellationRequested);

        Assert.InRange(observed.DistanceEvaluations, 0, evaluationLimit + 1023);

        return completed;
    }

    [Fact]
    public void Approximate_graph_preserves_frozen_neighbor_bits_and_input_permutation()
    {
        RaptorPoint[] points = RaptorNeighborQualityTests.Points("clustered", 2304);

        int k = Neighbors(points.Length);

        var first = Measure("approximate-preservation", 120, token => new RaptorNeighborGraphBuilder().Build(points, k, 751, token));

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Code : "");

        output.WriteLine($"approximate_baseline n={points.Length} d={points[0].Vector.Length} k={k} hash={GraphHash(first.Value)} evaluations={first.Value.DistanceEvaluations} requests={first.Value.DistanceRequests} memo_lookups={first.Value.MemoLookups} memo_hits={first.Value.MemoHits} memo_evictions={first.Value.MemoEvictions} memo_bytes={first.Value.MemoAllocatedBytes}");

        // Frozen unchanged-kernel output is a preservation witness, not a scientific reference oracle.
        Assert.Equal("783dc08ffd6a44b2a5a4ccf1451607bab27d345d3ea8206dd7609386a43b4e55", GraphHash(first.Value));

        Assert.True(first.Value.DistanceEvaluations < (long)points.Length * (points.Length - 1) / 2, "Approximation must perform fewer scalar distances than exact all-pairs at the production global neighborhood.");

        AssertMemoCounters(first.Value);

        AssertRecall(points, first.Value, 32);

        RaptorPoint[] reversed = points.Reverse().ToArray();

        var second = Measure("approximate-permutation", 120, token => new RaptorNeighborGraphBuilder().Build(reversed, k, 751, token));

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Code : "");

        for (int row = 0; row < points.Length; row++)
        {
            int other = points.Length - row - 1;

            Assert.Equal(first.Value.SourceIndices.Slice(row * k, k).ToArray().Select(index => points[index].StableId), second.Value.SourceIndices.Slice(other * k, k).ToArray().Select(index => reversed[index].StableId));

            Assert.Equal(first.Value.Distances.Slice(row * k, k).ToArray().Select(BitConverter.DoubleToInt64Bits), second.Value.Distances.Slice(other * k, k).ToArray().Select(BitConverter.DoubleToInt64Bits));
        }
    }

    [Fact]
    public void Cancellation_is_polled_when_repeated_pair_requests_stop_scalar_evaluations()
    {
        using CancellationTokenSource source = new(TimeSpan.FromSeconds(20));

        float[] vector = new float[768];

        vector[0] = 1;

        RaptorPoint[] points = Enumerable.Range(0, 2200).Select(index => new RaptorPoint("hit-only-" + index.ToString("D6", CultureInfo.InvariantCulture), vector)).ToArray();

        RaptorNeighborProgress prior = default;

        bool hitOnlyCanceled = false;

        var observer = new Progress<RaptorNeighborProgress>(value =>
        {
            if (value.Phase == prior.Phase && value.DistanceEvaluations > 0 && value.DistanceEvaluations == prior.DistanceEvaluations && value.WorkSteps - prior.WorkSteps is > 0 and <= 1024 && value.WorkSteps % 1024 == 0 && value.MemoHits > prior.MemoHits)
            {
                hitOnlyCanceled = true;

                source.Cancel();
            }

            prior = value;
        });

        Exception? failure = Record.Exception(() => new RaptorNeighborGraphBuilder(observer).Build(points, Neighbors(points.Length), 751, source.Token));

        Assert.True(hitOnlyCanceled, "No in-work cancellation poll occurred while repeated pair requests reused the same scalar evaluation count.");

        OperationCanceledException canceled = Assert.IsAssignableFrom<OperationCanceledException>(failure);

        Assert.Equal(source.Token, canceled.CancellationToken);
    }

    [Fact]
    public void A_bounded_probe_records_natural_completion_instead_of_inventing_cancellation() => Assert.True(PartialNeighborPilot(100));

    [Fact]
    public void Synthetic_corpus_has_full_width_distinct_identity_and_repeatable_float_bits()
    {
        RaptorPoint[] points = Corpus(19);

        Assert.All(points, point => Assert.Equal(768, point.Vector.Length));

        Assert.Equal(19, points.Select(point => point.StableId).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(CorpusHash(points), CorpusHash(Corpus(19)));

        Assert.NotEqual(CorpusHash(points), CorpusHash(Corpus(20)));

        Assert.All(points, point => Assert.Contains(point.Vector.ToArray(), value => value != 0));
    }

    [Fact]
    public void Neighbor_cancellation_occurs_during_actual_work_with_the_supplied_token()
    {
        using CancellationTokenSource source = new();

        long evaluations = 0;

        var observer = new Progress<RaptorNeighborProgress>(value =>
        {
            if (value.DistanceEvaluations > 0)
            {
                evaluations = value.DistanceEvaluations;

                source.Cancel();
            }
        });

        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() => new RaptorNeighborGraphBuilder(observer).Build(Corpus(100), 9, 751, source.Token));

        Assert.Equal(source.Token, error.CancellationToken);

        Assert.Equal(1024, evaluations);
    }

    private static void RequireOptIn() => Skip.IfNot(System.Environment.GetEnvironmentVariable(OptIn) == "1", $"Set {OptIn}=1 for the explicit offline numerical scale probe.");

    private void Describe(RaptorPoint[] points, int k) => output.WriteLine($"corpus n={points.Length} d=768 k={k} seed=751 hash={CorpusHash(points)} manifest={RaptorAlgorithmManifest.Current.Fingerprint()} runtime={System.Environment.Version} architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} paid_calls=0");

    private T Measure<T>(string stage, int seconds, Func<CancellationToken, T> action, Func<string>? counters = null)
    {
        using var budget = new Budget(seconds);

        long before = GC.GetTotalAllocatedBytes(true);

        Stopwatch watch = Stopwatch.StartNew();

        bool completed = false;

        try
        {
            T result = action(budget.Token);

            completed = true;

            return result;
        }
        finally
        {
            if (counters is not null) output.WriteLine(counters());

            output.WriteLine(FormattableString.Invariant($"stage name={stage} completed={completed} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} allocated_bytes={GC.GetTotalAllocatedBytes(true) - before} start_working_set={budget.StartWorkingSet} peak_working_set={budget.PeakWorkingSet} reason={budget.Reason} cancel_latency_ms={budget.CancelLatencyMilliseconds:F3}"));
        }
    }

    private static int Neighbors(int count) => Math.Clamp((int)Math.Floor(Math.Sqrt(count - 1)), 2, count - 1);

    private static RaptorPoint[] Corpus(int count)
    {
        // A fixed integer stream and representable float arithmetic keep source bits independent of culture.
        ulong state = 177;

        float Next()
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);

            return ((int)(state >> 40) - 8388608) / 8388608f;
        }

        float[][] centers = Enumerable.Range(0, 8).Select(_ => Enumerable.Range(0, 768).Select(_ => Next()).ToArray()).ToArray();

        return Enumerable.Range(0, count).Select(index => new RaptorPoint("scale-" + index.ToString("D6", CultureInfo.InvariantCulture), Enumerable.Range(0, 768).Select(dimension => centers[index % 8][dimension] + Next() / 4).ToArray())).ToArray();
    }

    private static string CorpusHash(IEnumerable<RaptorPoint> points)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> bytes = stackalloc byte[4];

        foreach (RaptorPoint point in points)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(point.StableId));

            foreach (float value in point.Vector.Span)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));

                hash.AppendData(bytes);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string GraphHash(RaptorNeighborGraph graph)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> bytes = stackalloc byte[8];

        foreach (int index in graph.SourceIndices.Span)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, index);

            hash.AppendData(bytes[..4]);
        }

        foreach (double distance in graph.Distances.Span)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(distance));

            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AssertMemoCounters(RaptorNeighborGraph graph)
    {
        Assert.Equal(graph.DistanceRequests, graph.MemoLookups);

        Assert.Equal(graph.DistanceRequests, graph.DistanceEvaluations + graph.MemoHits);

        Assert.InRange(graph.MemoEvictions, 0, graph.DistanceEvaluations);

        Assert.True(graph.MemoHits > 0);

        Assert.InRange(graph.MemoAllocatedBytes, 1, 260L * 1024 * 1024);
    }

    private void AssertRecall(RaptorPoint[] points, RaptorNeighborGraph graph, int samples)
    {
        long hits = 0;

        for (int sample = 0; sample < samples; sample++)
        {
            int row = sample * (points.Length - 1) / (samples - 1);

            int[] expected = Enumerable.Range(0, points.Length).Where(index => index != row)
                .Select(index => (Index: index, Distance: OracleDistance(points[row].Vector.Span, points[index].Vector.Span)))
                .OrderBy(pair => pair.Distance).ThenBy(pair => points[pair.Index].StableId, StringComparer.Ordinal)
                .Take(graph.Neighbors - 1).Select(pair => pair.Index).ToArray();

            int[] actual = graph.SourceIndices.Slice(row * graph.Neighbors + 1, graph.Neighbors - 1).ToArray();

            hits += actual.Intersect(expected).Count();
        }

        double recall = (double)hits / (samples * (graph.Neighbors - 1));

        output.WriteLine(FormattableString.Invariant($"recall sampled_rows={samples} oracle_distances={(long)samples * (points.Length - 1)} nonself_recall={recall:R}"));

        Assert.InRange(recall, points.Length <= 2048 ? 1 : .95, 1);
    }

    private static double OracleDistance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        double dot = 0, a = 0, b = 0;

        for (int dimension = 0; dimension < left.Length; dimension++)
        {
            dot += (double)left[dimension] * right[dimension];

            a += (double)left[dimension] * left[dimension];

            b += (double)right[dimension] * right[dimension];
        }

        return Math.Clamp(1 - dot / Math.Sqrt(a * b), 0, 2);
    }

    private static string DoubleHash(IEnumerable<double> values)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> bytes = stackalloc byte[8];

        foreach (double value in values)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));

            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed class Progress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class Budget : IDisposable
    {
        private readonly CancellationTokenSource _source = new();

        private readonly System.Diagnostics.Process _process = System.Diagnostics.Process.GetCurrentProcess();

        private readonly Timer _timer;

        private readonly long _started = Stopwatch.GetTimestamp();

        private long _peak;

        private long _canceled;

        private string _reason = "none";

        internal Budget(int seconds)
        {
            _process.Refresh();

            StartWorkingSet = _process.WorkingSet64;

            _peak = StartWorkingSet;

            _timer = new Timer(_ =>
            {
                _process.Refresh();

                long current = _process.WorkingSet64;

                long previous;

                do
                {
                    previous = Interlocked.Read(ref _peak);
                }

                while (current > previous && Interlocked.CompareExchange(ref _peak, current, previous) != previous);

                if (current > 4L * 1024 * 1024 * 1024) Cancel("memory");

                else if (Stopwatch.GetElapsedTime(_started).TotalSeconds >= seconds) Cancel("time");
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        }

        internal CancellationToken Token => _source.Token;

        internal long StartWorkingSet { get; }

        internal long PeakWorkingSet => Interlocked.Read(ref _peak);

        internal string Reason => Volatile.Read(ref _reason);

        internal double CancelLatencyMilliseconds => Interlocked.Read(ref _canceled) is long stamp && stamp != 0 ? Stopwatch.GetElapsedTime(stamp).TotalMilliseconds : 0;

        internal void Cancel(string reason)
        {
            if (Interlocked.CompareExchange(ref _canceled, Stopwatch.GetTimestamp(), 0) == 0)
            {
                Volatile.Write(ref _reason, reason);

                _source.Cancel();
            }
        }

        public void Dispose()
        {
            _timer.DisposeAsync().AsTask().GetAwaiter().GetResult();

            _process.Dispose();

            _source.Dispose();
        }
    }
}
