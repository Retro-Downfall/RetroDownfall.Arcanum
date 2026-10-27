using System.Collections.Concurrent;
using System.Globalization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorClustererObservationTests
{
    private static readonly TapestryScope Scope = new(TapestryScopeKind.Workspace, "/synthetic/observer-private-scope");

    [Theory]
    [InlineData(24)]
    [InlineData(256)]
    public void Actual_default_observation_preserves_every_membership_bit_and_measures_each_fit(int count)
    {
        RaptorPoint[] points = RaptorScaleProbe.Corpus(count);

        TapestryClusterIdentity identity = Identity(points, 3);

        RecordingObserver observer = new();

        Result<RaptorClusterResult> plain = new RaptorClusterer().Cluster(points, identity, CancellationToken.None);

        Result<RaptorClusterResult> observed = new RaptorClusterer(observer).Cluster(points, identity, CancellationToken.None);

        Assert.True(plain.IsSuccess);

        Assert.True(observed.IsSuccess);

        Assert.Equal(MembershipBits(plain.Value), MembershipBits(observed.Value));

        RaptorFitObservation[] events = observer.Events.ToArray();

        Assert.NotEmpty(events);

        Assert.All(events, value => Assert.Equal(3, value.Layer));

        Assert.Contains(events, value => value.Stage == RaptorStage.GlobalMixture);

        if (count == 256) Assert.Contains(events, value => value.Stage == RaptorStage.LocalMixture);

        foreach (IGrouping<string, RaptorFitObservation> fit in events.GroupBy(value => value.FitIdentity))
        {
            Assert.Equal(64, fit.Key.Length);

            Assert.All(fit.Key, character => Assert.True(char.IsAsciiHexDigit(character)));

            RaptorFitObservation started = Assert.Single(fit, value => value.Phase == RaptorFitObservationPhase.Started);

            _ = Assert.Single(fit, value => value.Phase == RaptorFitObservationPhase.Completed);

            Assert.All(fit, value => Assert.Equal(started.SampleCount, value.SampleCount));

            Assert.All(fit, value => Assert.Equal(started.Stage, value.Stage));

            RaptorGmmCandidate[] candidates = fit.Where(value => value.Phase == RaptorFitObservationPhase.Candidate)
                .Select(value => Assert.IsType<RaptorGmmCandidate>(value.Candidate)).ToArray();

            if (candidates.Any(value => value.SearchBypassed))
            {
                RaptorGmmCandidate bypass = Assert.Single(candidates);

                Assert.True(bypass.SearchBypassed);

                Assert.Equal(1, bypass.Components);
            }
            else
            {
                Assert.Equal(Enumerable.Range(1, Math.Min(50, started.SampleCount) - 1), candidates.Select(value => value.Components));
            }
        }
    }

    [Fact]
    public void Identical_rows_report_the_actual_single_fit_and_explicitly_bypass_BIC_search()
    {
        List<RaptorGmmCandidate> reported = [];

        var observer = new Synchronous<RaptorGmmCandidate>(reported.Add);

        double[][] rows = Enumerable.Range(0, 16).Select(_ => new double[] { 1, 1 }).ToArray();

        Result<RaptorGmmFit> plain = new RaptorGaussianMixture().SelectByBic(rows, 71, CancellationToken.None);

        Result<RaptorGmmFit> observed = new RaptorGaussianMixture(diagnostics: observer).SelectByBic(rows, 71, CancellationToken.None);

        Assert.True(plain.IsSuccess);

        Assert.True(observed.IsSuccess);

        Assert.Equal(plain.Value.Bic, observed.Value.Bic);

        RaptorGmmCandidate bypass = Assert.Single(reported);

        Assert.True(bypass.SearchBypassed);

        Assert.Equal(1, bypass.Components);

        Assert.Null(bypass.RefusalCode);

        Assert.Equal(observed.Value.Bic, bypass.Bic);

        Assert.DoesNotContain(reported, value => !value.SearchBypassed);
    }

    [Fact]
    public void Ordinary_direct_BIC_search_keeps_its_existing_exclusive_attempt_sequence()
    {
        List<RaptorGmmCandidate> reported = [];

        double[][] rows = Enumerable.Range(0, 12).Select(index => new double[] { index, index * index }).ToArray();

        Result<RaptorGmmFit> result = new RaptorGaussianMixture(diagnostics: new Synchronous<RaptorGmmCandidate>(reported.Add))
            .SelectByBic(rows, 71, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(Enumerable.Range(1, 11), reported.Select(value => value.Components));

        Assert.All(reported, value => Assert.False(value.SearchBypassed));
    }

    [Fact]
    public void Separate_layers_and_parallel_calls_have_distinct_fit_identity_without_shared_lifetime_state()
    {
        RaptorPoint[] points = RaptorScaleProbe.Corpus(24);

        RecordingObserver observer = new();

        RaptorClusterer clusterer = new(observer);

        Parallel.Invoke(
            () => Assert.True(clusterer.Cluster(points, Identity(points, 1), CancellationToken.None).IsSuccess),
            () => Assert.True(clusterer.Cluster(points.Reverse().ToArray(), Identity(points, 2), CancellationToken.None).IsSuccess));

        RaptorFitObservation[] events = observer.Events.ToArray();

        Assert.Contains(events, value => value.Layer == 1);

        Assert.Contains(events, value => value.Layer == 2);

        string[] first = events.Where(value => value.Layer == 1).Select(value => value.FitIdentity).Distinct().ToArray();

        string[] second = events.Where(value => value.Layer == 2).Select(value => value.FitIdentity).Distinct().ToArray();

        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));

        foreach (IGrouping<string, RaptorFitObservation> fit in events.GroupBy(value => value.FitIdentity))
        {
            Assert.Equal(fit.First().Layer, fit.Last().Layer);

            Assert.Equal(RaptorFitObservationPhase.Started, fit.First().Phase);

            Assert.Equal(RaptorFitObservationPhase.Completed, fit.Last().Phase);
        }
    }

    [Fact]
    public void Candidate_observer_cancellation_propagates_the_supplied_token_without_manufactured_completion()
    {
        RaptorPoint[] points = RaptorScaleProbe.Corpus(24);

        using CancellationTokenSource caller = new();

        RecordingObserver observer = new(value =>
        {
            if (value.Phase == RaptorFitObservationPhase.Candidate) caller.Cancel();
        });

        OperationCanceledException failure = Assert.ThrowsAny<OperationCanceledException>(() =>
            new RaptorClusterer(observer).Cluster(points, Identity(points, 1), caller.Token));

        Assert.Equal(caller.Token, failure.CancellationToken);

        Assert.Contains(observer.Events, value => value.Phase == RaptorFitObservationPhase.Candidate);

        Assert.Contains(observer.Events, value => value.Phase == RaptorFitObservationPhase.Canceled);

        Assert.DoesNotContain(observer.Events, value => value.Phase == RaptorFitObservationPhase.Completed);
    }

    [Fact]
    public void Observer_fault_propagates_unchanged_and_cannot_create_a_successful_fit_receipt()
    {
        RaptorPoint[] points = RaptorScaleProbe.Corpus(24);

        InvalidOperationException expected = new("private observer fault must not become diagnostic payload");

        RecordingObserver observer = new(value =>
        {
            if (value.Phase == RaptorFitObservationPhase.Candidate) throw expected;
        });

        Exception actual = Assert.Throws<InvalidOperationException>(() =>
            new RaptorClusterer(observer).Cluster(points, Identity(points, 1), CancellationToken.None));

        Assert.Same(expected, actual);

        Assert.DoesNotContain(observer.Events, value => value.Phase == RaptorFitObservationPhase.Completed);
    }

    [Fact]
    public void Real_default_fitter_refusal_has_no_candidate_or_successful_completion()
    {
        RaptorPoint[] points = RaptorScaleProbe.Corpus(1);

        RecordingObserver observer = new();

        Result<RaptorPosteriorModel> result = RaptorClusterer.ObserveDefaultFit(
            [new double[] { double.NaN, 1 }], 71, Identity(points, 1) with { Stage = RaptorStage.GlobalMixture },
            observer, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Raptor.InvalidParameters", result.Error.Code);

        Assert.Collection(observer.Events,
            value => Assert.Equal(RaptorFitObservationPhase.Started, value.Phase),
            value => Assert.Equal(RaptorFitObservationPhase.Refused, value.Phase));
    }

    [Fact]
    public void A_cancellation_receipt_fault_retains_both_the_original_cancellation_and_observer_fault()
    {
        RaptorPoint[] points = RaptorScaleProbe.Corpus(24);

        using CancellationTokenSource caller = new();

        InvalidOperationException secondary = new("private terminal observer fault");

        RecordingObserver observer = new(value =>
        {
            if (value.Phase == RaptorFitObservationPhase.Candidate) caller.Cancel();

            if (value.Phase == RaptorFitObservationPhase.Canceled) throw secondary;
        });

        AggregateException failures = Assert.Throws<AggregateException>(() =>
            new RaptorClusterer(observer).Cluster(points, Identity(points, 1), caller.Token));

        Assert.Equal(2, failures.InnerExceptions.Count);

        OperationCanceledException canceled = Assert.IsAssignableFrom<OperationCanceledException>(failures.InnerExceptions[0]);

        Assert.Equal(caller.Token, canceled.CancellationToken);

        Assert.Same(secondary, failures.InnerExceptions[1]);

        Assert.DoesNotContain(observer.Events, value => value.Phase == RaptorFitObservationPhase.Completed);
    }

    [Fact]
    public void Concurrent_identical_semantic_calls_have_distinct_instance_local_observation_ordinals()
    {
        RaptorPoint[] points = RaptorScaleProbe.Corpus(24);

        TapestryClusterIdentity identity = Identity(points, 1);

        RecordingObserver observer = new();

        RaptorClusterer clusterer = new(observer);

        Parallel.Invoke(
            () => Assert.True(clusterer.Cluster(points, identity, CancellationToken.None).IsSuccess),
            () => Assert.True(clusterer.Cluster(points, identity, CancellationToken.None).IsSuccess));

        RaptorFitObservation[] events = observer.Events.ToArray();

        Assert.Equal(new long[] { 1, 2 }, events.Select(value => value.CallOrdinal).Distinct().Order());

        foreach (IGrouping<(long, string), RaptorFitObservation> fit in events.GroupBy(value => (value.CallOrdinal, value.FitIdentity)))
        {
            _ = Assert.Single(fit, value => value.Phase == RaptorFitObservationPhase.Started);

            _ = Assert.Single(fit, value => value.Phase == RaptorFitObservationPhase.Completed);
        }
    }

    private static TapestryClusterIdentity Identity(IReadOnlyList<RaptorPoint> points, int layer) =>
        new(Scope, layer, RaptorStage.Split, "observed-split-v1", points.Select(point => point.StableId));

    private static string MembershipBits(RaptorClusterResult result) => TapestryHash.OfParts(Parts(result));

    private static IEnumerable<string> Parts(RaptorClusterResult result)
    {
        foreach (RaptorCluster cluster in result.Clusters)
        {
            yield return cluster.StableId;

            foreach (RaptorMembership member in cluster.Members)
            {
                yield return FormattableString.Invariant($"{member.PointIndex}/{member.GlobalComponent}/{member.LocalComponent}/{BitConverter.DoubleToInt64Bits(member.GlobalPosterior):X16}/{BitConverter.DoubleToInt64Bits(member.LocalPosterior):X16}/{(int)member.Repair}");
            }
        }

        yield return "unassigned";

        foreach (int index in result.Unassigned) yield return index.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class RecordingObserver(Action<RaptorFitObservation>? action = null) : IProgress<RaptorFitObservation>
    {
        internal ConcurrentQueue<RaptorFitObservation> Events { get; } = new();

        public void Report(RaptorFitObservation value)
        {
            Events.Enqueue(value);

            action?.Invoke(value);
        }
    }

    private sealed class Synchronous<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
