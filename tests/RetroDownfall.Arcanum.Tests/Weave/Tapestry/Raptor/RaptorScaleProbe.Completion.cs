using System.Globalization;
using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed partial class RaptorScaleProbe
{
    [Fact]
    public void Legacy_approximate_recall_stops_before_work_when_its_budget_is_canceled()
    {
        RaptorPoint[] points = Corpus(100);

        var graph = new RaptorNeighborGraphBuilder().Build(points, 9, 751, CancellationToken.None);

        Assert.True(graph.IsSuccess);

        using Budget budget = new(240);

        budget.Cancel("time");

        int pairs = 0;

        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            AssertApproximateRecallWithinBudget(points, graph.Value, budget, () => pairs++));

        Assert.Equal(budget.Token, canceled.CancellationToken);

        Assert.Equal(0, pairs);
    }

    [Fact]
    public void Legacy_approximate_recall_stops_during_real_oracle_pair_work()
    {
        RaptorPoint[] points = Corpus(100);

        var graph = new RaptorNeighborGraphBuilder().Build(points, 9, 751, CancellationToken.None);

        Assert.True(graph.IsSuccess);

        using Budget budget = new(240);

        int pairs = 0;

        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            AssertApproximateRecallWithinBudget(points, graph.Value, budget, () =>
            {
                if (++pairs == 64) budget.Cancel("memory");
            }));

        Assert.Equal(budget.Token, canceled.CancellationToken);

        Assert.Equal(64, pairs);
    }

    [Fact]
    public void Legacy_approximate_recall_keeps_the_complete_uncanceled_validation()
    {
        RaptorPoint[] points = Corpus(100);

        var graph = new RaptorNeighborGraphBuilder().Build(points, 9, 751, CancellationToken.None);

        Assert.True(graph.IsSuccess);

        using Budget budget = new(240);

        RecordingOutput recorded = new();

        RaptorScaleProbe probe = new(recorded);

        int pairs = 0;

        probe.AssertApproximateRecallWithinBudget(points, graph.Value, budget, () => pairs++);

        Assert.Equal(32 * 99, pairs);

        Assert.Contains(recorded.Lines, line => line.Contains("nonself_recall=1", StringComparison.Ordinal));
    }

    [Fact]
    public void Stage_measurement_does_not_call_a_kernel_refusal_completed()
    {
        RecordingOutput recorded = new();

        RaptorScaleProbe probe = new(recorded);

        var result = probe.Measure("refusal", 30, token => new RaptorNeighborGraphBuilder().Build(Corpus(3), 0, 751, token));

        Assert.True(result.IsFailure);

        Assert.Contains(recorded.Lines, line => line.Contains("completed=False", StringComparison.Ordinal));
    }

    [Fact]
    public void Stage_measurement_retains_real_natural_completion()
    {
        RecordingOutput recorded = new();

        RaptorScaleProbe probe = new(recorded);

        var result = probe.Measure("natural", 30, token => new RaptorNeighborGraphBuilder().Build(Corpus(3), 2, 751, token));

        Assert.True(result.IsSuccess);

        Assert.Contains(recorded.Lines, line => line.Contains("completed=True", StringComparison.Ordinal));
    }

    [Fact]
    public void Oracle_validation_honors_the_supplied_caller_token()
    {
        RaptorPoint[] points = Corpus(100);

        var graph = new RaptorNeighborGraphBuilder().Build(points, 9, 751, CancellationToken.None);

        Assert.True(graph.IsSuccess);

        using CancellationTokenSource source = new();

        source.Cancel();

        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() => AssertRecall(points, graph.Value, 32, source.Token));

        Assert.Equal(source.Token, canceled.CancellationToken);
    }

    [Fact]
    public void Oracle_validation_cancels_after_real_pair_work_without_finishing_a_row()
    {
        RaptorPoint[] points = Corpus(100);

        var graph = new RaptorNeighborGraphBuilder().Build(points, 9, 751, CancellationToken.None);

        Assert.True(graph.IsSuccess);

        using CancellationTokenSource caller = new();

        int pairs = 0;

        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            AssertRecall(points, graph.Value, 32, caller.Token, () =>
            {
                if (++pairs == 64) caller.Cancel();
            }));

        Assert.Equal(caller.Token, canceled.CancellationToken);

        Assert.Equal(64, pairs);
    }

    [Fact]
    public void Completion_budget_observes_caller_cancellation_during_real_kernel_work()
    {
        using CancellationTokenSource caller = new();

        using Budget budget = new(30, caller.Token);

        caller.Cancel();

        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            new RaptorNeighborGraphBuilder().Build(Corpus(100), 9, 751, budget.Token));

        Assert.Equal(budget.Token, canceled.CancellationToken);

        Assert.Equal("caller", budget.Reason);
    }

    [Fact]
    public void Alternating_descent_phases_cannot_hide_later_round_progress()
    {
        NeighborProgressCheckpoints checkpoints = new();

        for (int row = 0; row < 4096; row++)
        {
            _ = checkpoints.ShouldRecord(new("descent-pair-enumeration", 0, ProcessedRows: row, Pass: 1));

            _ = checkpoints.ShouldRecord(new("descent", 0, ProcessedRows: row, Pass: 1));
        }

        Assert.True(checkpoints.ShouldRecord(new("descent", 0, ProcessedRows: 4096, Pass: 1)));

        Assert.True(checkpoints.ShouldRecord(new("descent-pair-enumeration", 0, ProcessedRows: 0, Pass: 2)));

        Assert.True(checkpoints.ShouldRecord(new("descent", 0, ProcessedRows: 4096, Pass: 2)));

        Assert.Equal(4, checkpoints.Count);
    }

    [Fact]
    public void Distinct_initialization_passes_and_windows_remain_observable()
    {
        NeighborProgressCheckpoints checkpoints = new();

        Assert.True(checkpoints.ShouldRecord(new("initialization", 0, ProcessedRows: 0, Pass: 1)));

        Assert.False(checkpoints.ShouldRecord(new("initialization", 0, ProcessedRows: 1, Pass: 1)));

        Assert.True(checkpoints.ShouldRecord(new("initialization", 0, ProcessedRows: 4096, Pass: 1)));

        Assert.True(checkpoints.ShouldRecord(new("initialization", 0, ProcessedRows: 0, Pass: 2)));

        Assert.Equal(3, checkpoints.Count);
    }

    // Only the dedup key folds alternating work phases; emitted records retain the actual phase.
    private sealed class NeighborProgressCheckpoints
    {
        private string? _phase;

        private int _pass = -1;

        private int _window = -1;

        internal int Count { get; private set; }

        internal bool ShouldRecord(RaptorNeighborProgress value)
        {
            int window = value.ProcessedRows / 4096;

            string family = value.Phase is "descent" or "descent-pair-enumeration" ? "descent" : value.Phase;

            if (Count >= 1024 || (family == _phase && value.Pass == _pass && window == _window)) return false;

            _phase = family;

            _pass = value.Pass;

            _window = window;

            Count++;

            return true;
        }
    }

    private sealed class RecordingOutput : ITestOutputHelper
    {
        internal List<string> Lines { get; } = [];

        public void WriteLine(string message) => Lines.Add(message);

        public void WriteLine(string format, params object[] args) => WriteLine(string.Format(CultureInfo.InvariantCulture, format, args));
    }

    [SkippableFact]
    public void Complete_neighbor_graph_at_fifty_thousand()
    {
        RequireOptIn();

        using StreamWriter sidecar = CompletionWriter("neighbors-50k");

        RunCompletionProbe("neighbors-50k", 2520, sidecar, (whole, journal) =>
        {
            RaptorPoint[] points = Corpus(50000);

            Assert.Equal("e9a8589f0e8a63d3c96daf663a6eddce62d1b97488582bcb7ed2be5a1b3e6ee0", CorpusHash(points));

            Describe(points, 223);

            RaptorNeighborGraph graph = CompletionNeighbors(points, 2400, whole, journal, 100_000_000_000);

            _ = CompletionStage("validation", 120, whole, journal, budget =>
            {
                ValidateCompletionGraph(points, graph, budget.Token);

                AssertRecall(points, graph, 32, budget.Token);

                return Result<bool>.Success(true);
            });
        });
    }

    [SkippableFact]
    public void Complete_global_pipeline_at_ten_thousand()
    {
        RequireOptIn();

        using StreamWriter sidecar = CompletionWriter("global-10k");

        RunCompletionProbe("global-10k", 1620, sidecar, (whole, journal) =>
        {
            RaptorPoint[] points = Corpus(10000);

            Assert.Equal("1666be3372195f72acc9e450f84082b34e4b94f4335255d488fe1e6838a03123", CorpusHash(points));

            Describe(points, 99);

            RaptorNeighborGraph graph = CompletionNeighbors(points, 240, whole, journal);

            Assert.Equal("4de447eabf110f6de835b9a3795c5f98bb634f1d50c0010604398748c41de354", GraphHash(graph));

            _ = CompletionStage("validation", 120, whole, journal, budget =>
            {
                ValidateCompletionGraph(points, graph, budget.Token);

                AssertRecall(points, graph, 32, budget.Token);

                return Result<bool>.Success(true);
            });

            RaptorFuzzyGraph fuzzy = CompletionStage("fuzzy", 60, whole, journal,
                budget => new RaptorFuzzyGraphBuilder().Build(graph, budget.Token));

            output.WriteLine($"fuzzy edges={fuzzy.SourceIndices.Length}");

            RaptorUmapProgress optimized = default;

            var layout = CompletionStage("umap", 600, whole, journal, budget =>
                new RaptorUmapOptimizer(new Progress<RaptorUmapProgress>(value =>
                {
                    optimized = value;

                    journal.Umap(value);
                })).Optimize(fuzzy, 10, 752, budget.Token));

            Assert.Equal(499, optimized.Epoch);

            Assert.Equal(Enumerable.Range(0, points.Length), layout.Select(point => point.SourceIndex).Order());

            double[][] reduced = layout.OrderBy(point => point.SourceIndex).Select(point => point.Coordinates.ToArray()).ToArray();

            Assert.All(reduced, row => Assert.Equal(10, row.Length));

            Assert.All(reduced.SelectMany(row => row), value => Assert.True(double.IsFinite(value)));

            output.WriteLine($"umap rows={layout.Count} epochs={optimized.Epoch + 1} sampled_edges={optimized.SampledEdges} negative_samples={optimized.NegativeSamples} fingerprint={DoubleHash(reduced.SelectMany(row => row))}");

            List<RaptorGmmCandidate> candidates = [];

            RaptorGmmFit fit = CompletionStage("gmm", 600, whole, journal, budget =>
                new RaptorGaussianMixture(new Progress<RaptorGmmProgress>(journal.Gmm), new Progress<RaptorGmmCandidate>(candidate =>
                {
                    candidates.Add(candidate);

                    journal.Candidate(candidate);
                })).SelectByBic(reduced, 753, budget.Token));

            Assert.Equal(Enumerable.Range(1, 49), candidates.Select(candidate => candidate.Components));

            Assert.True(fit.Converged);

            Assert.True(double.IsFinite(fit.TotalLogLikelihood));

            Assert.True(double.IsFinite(fit.Bic));

            double[] posterior = new double[fit.Weights.Length];

            for (int row = 0; row < reduced.Length; row++)
            {
                whole.Token.ThrowIfCancellationRequested();

                Assert.True(fit.Posterior(reduced[row], posterior).IsSuccess);

                Assert.All(posterior, value => Assert.InRange(value, 0, 1));

                Assert.InRange(Math.Abs(posterior.Sum() - 1), 0, 1e-12);
            }

            output.WriteLine($"gmm attempted={candidates.Count} converged={candidates.Count(candidate => candidate.RefusalCode is null)} selected={fit.Weights.Length} selected_iterations={fit.Iterations} fingerprint={DoubleHash(fit.Weights.Concat(fit.Means.SelectMany(row => row)).Concat(fit.Covariances.SelectMany(matrix => matrix.SelectMany(row => row))))}");
        });
    }

    [SkippableFact]
    public void Complete_real_clusterer_at_ten_thousand()
    {
        RequireOptIn();

        using StreamWriter sidecar = CompletionWriter("clusterer-10k");

        RunCompletionProbe("clusterer-10k", 1800, sidecar, (whole, journal) =>
        {
            RaptorPoint[] points = Corpus(10000);

            Assert.Equal("1666be3372195f72acc9e450f84082b34e4b94f4335255d488fe1e6838a03123", CorpusHash(points));

            Describe(points, 99);

            TapestryClusterIdentity identity = new(new(TapestryScopeKind.Workspace, "/synthetic/raptor-scale"), 1, RaptorStage.GlobalUmap, "", points.Select(point => point.StableId));

            RaptorClusterResult result = CompletionStage("clusterer", 1800, whole, journal,
                budget => new RaptorClusterer().Cluster(points, identity, budget.Token));

            Assert.Empty(result.Unassigned);

            Assert.Equal(Enumerable.Range(0, points.Length), result.Clusters.SelectMany(cluster => cluster.Members).Select(member => member.PointIndex).Distinct().Order());

            Assert.Equal(result.Clusters.Select(cluster => cluster.StableId).Order(StringComparer.Ordinal), result.Clusters.Select(cluster => cluster.StableId));

            foreach (RaptorCluster cluster in result.Clusters)
            {
                whole.Token.ThrowIfCancellationRequested();

                foreach (RaptorMembership member in cluster.Members)
                {
                    Assert.InRange(member.PointIndex, 0, points.Length - 1);

                    Assert.InRange(member.GlobalPosterior, 0, 1);

                    Assert.InRange(member.LocalPosterior, 0, 1);
                }
            }

            output.WriteLine($"clusterer completed=true clusters={result.Clusters.Length} memberships={result.Clusters.Sum(cluster => cluster.Members.Length)} covered={points.Length} internal_stage_counts=unobserved fingerprint={DoubleHash(result.Clusters.SelectMany(cluster => cluster.Members.SelectMany(member => new double[] { member.PointIndex, member.GlobalComponent, member.LocalComponent, member.GlobalPosterior, member.LocalPosterior, (int)member.Repair })))}");
        });
    }

    [Fact]
    public void A_refused_completion_probe_never_emits_an_accepted_receipt()
    {
        RecordingOutput recorded = new();

        using StringWriter sidecar = new(CultureInfo.InvariantCulture);

        RaptorScaleProbe probe = new(recorded);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => probe.RunCompletionProbe("refusal-control", 30, sidecar, (whole, journal) =>
            _ = probe.CompletionStage("neighbors", 30, whole, journal,
                budget => new RaptorNeighborGraphBuilder().Build(Corpus(3), 0, 751, budget.Token))));

        Assert.Contains("outcome=failed", sidecar.ToString(), StringComparison.Ordinal);

        Assert.Contains("reason=kernel-refusal", sidecar.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("outcome=accepted", sidecar.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Natural_completion_emits_exactly_one_accepted_terminal_receipt()
    {
        RecordingOutput recorded = new();

        using StringWriter sidecar = new(CultureInfo.InvariantCulture);

        RaptorScaleProbe probe = new(recorded);

        probe.RunCompletionProbe("natural-control", 30, sidecar, (whole, journal) =>
            _ = probe.CompletionStage("neighbors", 30, whole, journal,
                budget => new RaptorNeighborGraphBuilder().Build(Corpus(3), 2, 751, budget.Token)));

        Assert.Single(sidecar.ToString().Split('\n'), line => line.Contains("event=terminal", StringComparison.Ordinal));

        Assert.Contains("outcome=accepted", sidecar.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("memory")]
    [InlineData("membership-limit")]
    public void Resource_containment_interrupts_real_work_without_a_completion_receipt(string reason)
    {
        using StringWriter sidecar = new(CultureInfo.InvariantCulture);

        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            RunCompletionProbe("containment-control", 30, sidecar, (whole, journal) =>
                _ = CompletionStage("neighbors", 30, whole, journal, budget =>
                {
                    if (reason == "time") budget.ObserveResources(0, 30);

                    else if (reason == "memory") budget.ObserveResources(4L * 1024 * 1024 * 1024 + 1, 0);

                    else budget.Cancel("membership-limit");

                    Assert.Equal(reason, budget.Reason);

                    return new RaptorNeighborGraphBuilder().Build(Corpus(100), 9, 751, budget.Token);
                })));

        Assert.True(canceled.CancellationToken.IsCancellationRequested);

        Assert.Contains("outcome=incomplete", sidecar.ToString(), StringComparison.Ordinal);

        Assert.Contains("reason=" + reason, sidecar.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("outcome=accepted", sidecar.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_pair_work_is_an_acceptance_failure_even_when_the_graph_finishes()
    {
        RaptorNeighborGraph graph = new RaptorNeighborGraphBuilder().Build(Corpus(100), 9, 751, CancellationToken.None).Value;

        Assert.Equal(4950, graph.DistanceEvaluations);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNeighborWorkAcceptance(graph, 4950));
    }

    [Fact]
    public void A_completed_graph_at_the_exact_work_limit_is_not_an_incomplete_run()
    {
        using StringWriter sidecar = new(CultureInfo.InvariantCulture);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RunCompletionProbe("work-limit-control", 30, sidecar, (whole, journal) =>
        {
            RaptorNeighborGraph graph = CompletionStage("neighbors", 30, whole, journal,
                budget => new RaptorNeighborGraphBuilder().Build(Corpus(100), 9, 751, budget.Token));

            AssertNeighborWorkAcceptance(graph, 4950);
        }));

        Assert.Contains("outcome=work-acceptance-failed", sidecar.ToString(), StringComparison.Ordinal);

        Assert.Contains("reason=work-limit", sidecar.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("outcome=incomplete", sidecar.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("outcome=accepted", sidecar.ToString(), StringComparison.Ordinal);
    }

    private static StreamWriter CompletionWriter(string name)
    {
        string? directory = System.Environment.GetEnvironmentVariable("ARCANUM_RAPTOR_SCALE_RECEIPT_DIRECTORY");

        Assert.False(string.IsNullOrWhiteSpace(directory), "Set the explicit test-only scale receipt directory.");

        _ = Directory.CreateDirectory(directory!);

        return new(new FileStream(Path.Combine(directory!, name + ".progress.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    }

    private void RunCompletionProbe(string name, int seconds, TextWriter sidecar, Action<Budget, CompletionJournal> action)
    {
        using Budget whole = new(seconds);

        using CompletionJournal journal = new(name, whole, sidecar, output);

        try
        {
            action(whole, journal);

            whole.Token.ThrowIfCancellationRequested();

            journal.Finish("accepted", "none");
        }
        catch (NeighborWorkLimitException)
        {
            journal.Finish("work-acceptance-failed", "work-limit");

            throw;
        }
        catch (OperationCanceledException)
        {
            string reason = journal.Reason == "none" ? whole.Reason : journal.Reason;

            journal.Finish(reason == "work-limit" ? "work-acceptance-failed" : "incomplete", reason);

            throw;
        }
        catch
        {
            journal.Finish("failed", journal.Reason == "none" ? "acceptance" : journal.Reason);

            throw;
        }
    }

    private T CompletionStage<T>(string name, int seconds, Budget whole, CompletionJournal journal, Func<Budget, Result<T>> action)
    {
        using Budget stage = new(seconds, whole.Token);

        journal.Stage(name);

        Stopwatch watch = Stopwatch.StartNew();

        long before = GC.GetTotalAllocatedBytes(true);

        bool completed = false;

        try
        {
            Result<T> result = action(stage);

            if (result.IsFailure) journal.Problem("kernel-refusal");

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "");

            stage.Token.ThrowIfCancellationRequested();

            completed = true;

            return result.Value;
        }
        catch (OperationCanceledException)
        {
            journal.Problem(whole.Token.IsCancellationRequested ? whole.Reason : stage.Reason);

            throw;
        }
        finally
        {
            output.WriteLine(FormattableString.Invariant($"completion_stage name={name} completed={completed} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} allocated_bytes={GC.GetTotalAllocatedBytes(true) - before} peak_working_set={stage.PeakWorkingSet} reason={stage.Reason} cancel_latency_ms={stage.CancelLatencyMilliseconds:F3}"));
        }
    }

    private RaptorNeighborGraph CompletionNeighbors(RaptorPoint[] points, int seconds, Budget whole, CompletionJournal journal, long membershipLimit = long.MaxValue)
    {
        long exactLimit = (long)points.Length * (points.Length - 1) / 2;

        RaptorNeighborGraph graph = CompletionStage("neighbors", seconds, whole, journal, budget =>
            new RaptorNeighborGraphBuilder(new Progress<RaptorNeighborProgress>(value =>
            {
                journal.Neighbor(value);

                if (value.DistanceEvaluations >= exactLimit) budget.Cancel("work-limit");

                else if (value.PairEnumerationVisits >= membershipLimit) budget.Cancel("membership-limit");
            })).Build(points, Neighbors(points.Length), 751, budget.Token));

        AssertNeighborWorkAcceptance(graph, exactLimit);

        AssertMemoCounters(graph);

        Assert.Equal(2 * graph.DistanceEvaluations, graph.ForwardHeapChecks);

        output.WriteLine($"graph n={graph.PointCount} k={graph.Neighbors} edges={graph.SourceIndices.Length} evaluations={graph.DistanceEvaluations} iterations={graph.DescentIterations} requests={graph.DistanceRequests} memo_lookups={graph.MemoLookups} memo_hits={graph.MemoHits} memo_evictions={graph.MemoEvictions} memo_bytes={graph.MemoAllocatedBytes} heap_checks={graph.ForwardHeapChecks} pair_visits={graph.PairEnumerationVisits} pair_bytes={graph.PairEnumerationAllocatedBytes} fingerprint={GraphHash(graph)}");

        return graph;
    }

    private static void AssertNeighborWorkAcceptance(RaptorNeighborGraph graph, long exactLimit)
    {
        if (graph.DistanceEvaluations >= exactLimit) throw new NeighborWorkLimitException(graph.DistanceEvaluations, exactLimit);
    }

    private sealed class NeighborWorkLimitException(long actual, long limit)
        : Xunit.Sdk.XunitException(FormattableString.Invariant($"WORK ACCEPTANCE FAILED: {actual} scalar distances must be strictly below {limit} exact pairs."));

    private static void ValidateCompletionGraph(RaptorPoint[] points, RaptorNeighborGraph graph, CancellationToken token)
    {
        int count = points.Length;

        int k = Neighbors(count);

        Assert.Equal(count, graph.PointCount);

        Assert.Equal(k, graph.Neighbors);

        Assert.Equal(count + 1, graph.RowOffsets.Length);

        Assert.Equal(count * k, graph.SourceIndices.Length);

        Assert.Equal(graph.SourceIndices.Length, graph.Distances.Length);

        ReadOnlySpan<int> offsets = graph.RowOffsets.Span;

        ReadOnlySpan<int> indices = graph.SourceIndices.Span;

        ReadOnlySpan<double> distances = graph.Distances.Span;

        Assert.Equal(0, offsets[0]);

        Assert.Equal(indices.Length, offsets[^1]);

        HashSet<int> seen = [];

        for (int row = 0; row < count; row++)
        {
            token.ThrowIfCancellationRequested();

            Assert.Equal(row * k, offsets[row]);

            Assert.Equal((row + 1) * k, offsets[row + 1]);

            Assert.Equal(row, indices[offsets[row]]);

            Assert.Equal(0, distances[offsets[row]]);

            seen.Clear();

            for (int edge = offsets[row]; edge < offsets[row + 1]; edge++)
            {
                Assert.InRange(indices[edge], 0, count - 1);

                Assert.True(seen.Add(indices[edge]));

                Assert.InRange(distances[edge], 0, 2);

                if (edge <= offsets[row] + 1) continue;

                int previous = edge - 1;

                Assert.True(distances[previous] < distances[edge] ||
                    (distances[previous] == distances[edge] && StringComparer.Ordinal.Compare(points[indices[previous]].StableId, points[indices[edge]].StableId) < 0));
            }
        }
    }

    private sealed class CompletionJournal : IDisposable
    {
        private readonly string _name;

        private readonly Budget _budget;

        private readonly TextWriter _sidecar;

        private readonly ITestOutputHelper _output;

        private readonly object _gate = new();

        private readonly Timer _heartbeat;

        private readonly Stopwatch _watch = Stopwatch.StartNew();

        private readonly long _allocatedStart = GC.GetTotalAllocatedBytes(true);

        private readonly NeighborProgressCheckpoints _checkpoints = new();

        private RaptorNeighborProgress _neighbor;

        private RaptorUmapProgress _umap;

        private RaptorGmmProgress _gmm;

        private string _stage = "corpus";

        private string _reason = "none";

        private int _candidates;

        private int _refusedCandidates;

        private int _records;

        private bool _terminal;

        internal CompletionJournal(string name, Budget budget, TextWriter sidecar, ITestOutputHelper output)
        {
            _name = name;

            _budget = budget;

            _sidecar = sidecar;

            _output = output;

            Write("start", "running");

            _heartbeat = new Timer(_ =>
            {
                lock (_gate) if (!_terminal) Write("heartbeat", "running");
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        internal string Reason
        {
            get { lock (_gate) return _reason; }
        }

        internal void Stage(string stage)
        {
            lock (_gate)
            {
                _stage = stage;

                Write("stage", "running");
            }
        }

        internal void Problem(string reason)
        {
            lock (_gate) _reason = reason;
        }

        internal void Neighbor(RaptorNeighborProgress value)
        {
            lock (_gate)
            {
                _neighbor = value;

                if (_checkpoints.ShouldRecord(value)) Write("checkpoint", "running");
            }
        }

        internal void Umap(RaptorUmapProgress value)
        {
            lock (_gate) _umap = value;
        }

        internal void Gmm(RaptorGmmProgress value)
        {
            lock (_gate) _gmm = value;
        }

        internal void Candidate(RaptorGmmCandidate candidate)
        {
            lock (_gate)
            {
                _candidates++;

                if (candidate.RefusalCode is not null) _refusedCandidates++;

                Write("candidate", "running");
            }
        }

        internal void Finish(string outcome, string reason)
        {
            lock (_gate)
            {
                if (outcome == "accepted") _budget.Token.ThrowIfCancellationRequested();

                if (_terminal) return;

                _reason = reason;

                _terminal = true;

                Write("terminal", outcome);
            }
        }

        private void Write(string kind, string outcome)
        {
            // Reserve one slot for the terminal record; alternating phases cannot suppress it.
            if (_records >= (kind == "terminal" ? 1024 : 1023)) return;

            _records++;

            string phase = _stage == "neighbors" ? _neighbor.Phase ?? "neighbors" : _stage;

            string line = FormattableString.Invariant($"event={kind} probe={_name} stage={_stage} phase={phase} outcome={outcome} reason={_reason} elapsed_ms={_watch.Elapsed.TotalMilliseconds:F3} scalar={_neighbor.DistanceEvaluations} requests={_neighbor.WorkSteps} hits={_neighbor.MemoHits} evictions={_neighbor.MemoEvictions} pair_visits={_neighbor.PairEnumerationVisits} initialized_rows={_neighbor.InitializedRows} processed_rows={_neighbor.ProcessedRows} pass={_neighbor.Pass} umap_epoch={_umap.Epoch} sampled_edges={_umap.SampledEdges} negative_samples={_umap.NegativeSamples} gmm_components={_gmm.Components} gmm_iteration={_gmm.Iteration} gmm_samples={_gmm.Samples} candidates={_candidates} refused_candidates={_refusedCandidates} start_working_set={_budget.StartWorkingSet} peak_working_set={_budget.PeakWorkingSet} allocated_bytes={GC.GetTotalAllocatedBytes(kind == "terminal") - _allocatedStart}");

            _sidecar.WriteLine(line);

            _sidecar.Flush();

            if (kind == "terminal") _output.WriteLine(line);
        }

        public void Dispose() => _heartbeat.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
