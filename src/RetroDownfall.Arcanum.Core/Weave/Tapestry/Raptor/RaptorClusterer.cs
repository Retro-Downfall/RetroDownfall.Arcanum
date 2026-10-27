using System.Collections.Immutable;

using System.Globalization;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

internal abstract class RaptorPosteriorModel
{
    public abstract int Components { get; }

    public abstract Result<double> Posterior(int index, Span<double> destination);
}

internal delegate Result<double[][]> RaptorReduce(IReadOnlyList<RaptorPoint> points, int neighbors, int dimensions, TapestryClusterIdentity identity, CancellationToken token);

internal delegate Result<RaptorPosteriorModel> RaptorFit(double[][] points, ulong seed, CancellationToken token);

/// <summary>Original-vector global/local UMAP and full-covariance GMM composition. Never used by the legacy builder.</summary>
public sealed class RaptorClusterer : ITapestryClusterer
{
    private readonly RaptorReduce _reduce;

    private readonly RaptorFit _fit;

    private readonly IProgress<RaptorFitObservation>? _diagnostics;

    private long _observedCalls;

    private static RaptorNumericOptions Options => RaptorAlgorithmManifest.Current.Options;

    public RaptorClusterer() : this(Reduce, Fit) { }

    internal RaptorClusterer(IProgress<RaptorFitObservation> diagnostics) : this()
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        _diagnostics = diagnostics;
    }

    internal RaptorClusterer(RaptorReduce reduce, RaptorFit fit)
    {
        _reduce = reduce;

        _fit = fit;
    }

    public Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (points is null || identity is null || !Valid(points, cancellationToken)) return Invalid();

        var actual = new TapestryClusterIdentity(identity.Scope, identity.Layer, identity.Stage, identity.StageKey, points.Select(p => p.StableId));

        if (actual.MemberDigest != identity.MemberDigest) return Invalid();

        long observedCall = _diagnostics is null ? 0 : Interlocked.Increment(ref _observedCalls);

        if (points.Count == 0) return new RaptorClusterResult([], []);

        int[] order = Enumerable.Range(0, points.Count).ToArray();

        RaptorFuzzyGraphBuilder.Sort(order, (a, b) => StringComparer.Ordinal.Compare(points[a].StableId, points[b].StableId), cancellationToken);

        RaptorPoint[] sorted = order.Select(i => points[i]).ToArray();

        var paths = new List<RaptorMembership[]>();

        if (points.Count <= Options.ReductionDimensions + 1)
        {
            paths.Add(order.Select(i => new RaptorMembership(i, 0, 0, 1, 1, RaptorRepairReason.SmallGroup)).ToArray());
        }
        else
        {
            var global = Stage(sorted, identity, false, cancellationToken, observedCall);

            if (global.IsFailure) return global.Error;

            for (int g = 0; g < global.Value.Count; g++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Selection[] group = global.Value[g];

                RaptorPoint[] subset = group.Select(s => sorted[s.Index]).ToArray();

                string key = RaptorIdentityHash.OfParts([identity.StageKey, "global", g.ToString(CultureInfo.InvariantCulture)]);

                var localIdentity = new TapestryClusterIdentity(identity.Scope, identity.Layer, RaptorStage.LocalUmap, key, subset.Select(p => p.StableId));

                if (subset.Length <= Options.ReductionDimensions + 1)
                {
                    paths.Add(group.Select(s => new RaptorMembership(order[s.Index], g, 0, s.Probability, 1, s.Repair | RaptorRepairReason.SmallGroup)).ToArray());

                    continue;
                }

                var local = Stage(subset, localIdentity, true, cancellationToken, observedCall);

                if (local.IsFailure) return local.Error;

                for (int l = 0; l < local.Value.Count; l++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    paths.Add(local.Value[l].Select(s => new RaptorMembership(order[group[s.Index].Index], g, l, group[s.Index].Probability, s.Probability, group[s.Index].Repair | s.Repair)).ToArray());
                }
            }
        }

        var combined = new SortedDictionary<string, List<RaptorMembership>>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string id = ClusterId(identity, path.Select(m => points[m.PointIndex].StableId));

            if (!combined.TryGetValue(id, out var members)) combined[id] = members = [];

            members.AddRange(path);
        }

        var clusters = ImmutableArray.CreateBuilder<RaptorCluster>();

        foreach (var (id, members) in combined)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var unique = new HashSet<int>();

            bool duplicate = false;

            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();

                duplicate |= !unique.Add(member.PointIndex);
            }

            var ordered = members.ToArray();

            RaptorFuzzyGraphBuilder.Sort(ordered, (a, b) =>
            {
                int comparison = StringComparer.Ordinal.Compare(points[a.PointIndex].StableId, points[b.PointIndex].StableId);

                if (comparison != 0) return comparison;

                comparison = a.GlobalComponent.CompareTo(b.GlobalComponent);

                return comparison != 0 ? comparison : a.LocalComponent.CompareTo(b.LocalComponent);
            }, cancellationToken);

            if (duplicate)
            {
                for (int i = 0; i < ordered.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ordered[i] = ordered[i] with { Repair = ordered[i].Repair | RaptorRepairReason.DuplicatePaths };
                }
            }

            clusters.Add(new(id, ordered.ToImmutableArray()));
        }

        return new RaptorClusterResult(clusters.ToImmutable(), []);
    }

    /// <summary>Framed semantic identity includes scope, layer, split path and algorithm; duplicate paths share one identity.</summary>
    public static string ClusterId(TapestryClusterIdentity identity, IEnumerable<string> members) => RaptorIdentityHash.OfParts(
        ["raptor-cluster-v1", RaptorAlgorithmManifest.Current.Fingerprint(), ((int)identity.Scope.Kind).ToString(CultureInfo.InvariantCulture), identity.Scope.Id,
            identity.Layer.ToString(CultureInfo.InvariantCulture), identity.StageKey, new TapestryClusterIdentity(identity.Scope, identity.Layer, RaptorStage.Split, "", members).MemberDigest]);

    private Result<List<Selection[]>> Stage(RaptorPoint[] points, TapestryClusterIdentity identity, bool local, CancellationToken token, long observedCall)
    {
        token.ThrowIfCancellationRequested();

        int neighbors = local ? Math.Min(Options.LocalNeighbors, points.Length - 1) : Math.Clamp((int)Math.Floor(Math.Sqrt(points.Length - 1)), Options.MinimumNeighbors, points.Length - 1);

        int dimensions = Math.Min(Options.ReductionDimensions, points.Length - 2);

        var reduced = _reduce(points, neighbors, dimensions, identity with { Stage = local ? RaptorStage.LocalUmap : RaptorStage.GlobalUmap }, token);

        if (reduced.IsFailure) return reduced.Error;

        TapestryClusterIdentity fitIdentity = identity with { Stage = local ? RaptorStage.LocalMixture : RaptorStage.GlobalMixture };

        ulong seed = RaptorAlgorithmManifest.Current.DeriveSeed(fitIdentity);

        var fit = _diagnostics is null ? _fit(reduced.Value, seed, token)
            : ObserveDefaultFit(reduced.Value, seed, fitIdentity, _diagnostics, token, observedCall);

        if (fit.IsFailure) return fit.Error;

        int components = fit.Value.Components;

        if (components < 1 || components >= Options.ComponentUpperBoundExclusive) return Invalid();

        List<Selection>[] groups = Enumerable.Range(0, components).Select(_ => new List<Selection>()).ToArray();

        double[] posterior = new double[components];

        for (int index = 0; index < points.Length; index++)
        {
            token.ThrowIfCancellationRequested();

            var evaluated = fit.Value.Posterior(index, posterior);

            if (evaluated.IsFailure) return evaluated.Error;

            double sum = 0;

            int best = 0;

            bool assigned = false;

            for (int c = 0; c < components; c++)
            {
                double p = posterior[c];

                if (!double.IsFinite(p) || p < 0 || p > 1) return Invalid();

                sum += p;

                if (p > posterior[best]) best = c;

                if (p > Options.MembershipThreshold)
                {
                    groups[c].Add(new(index, p, RaptorRepairReason.None));

                    assigned = true;
                }
            }

            if (Math.Abs(sum - 1) > Options.MixtureNormalizationTolerance) return Invalid();

            if (!assigned) groups[best].Add(new(index, posterior[best], local ? RaptorRepairReason.LocalNoMembership : RaptorRepairReason.GlobalNoMembership));
        }
        // Components are ordered by their stable member identities; canonical GMM parameter order breaks identical-set ties.
        var nonempty = groups.Where(g => g.Count > 0).Select(g => g.ToArray()).ToList();

        return nonempty.OrderBy(group => group, Comparer<Selection[]>.Create((a, b) =>
        {
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                int comparison = StringComparer.Ordinal.Compare(points[a[i].Index].StableId, points[b[i].Index].StableId);

                if (comparison != 0) return comparison;
            }

            return a.Length.CompareTo(b.Length);
        })).ToList();
    }

    private static Result<double[][]> Reduce(IReadOnlyList<RaptorPoint> points, int neighbors, int dimensions, TapestryClusterIdentity identity, CancellationToken token)
    {
        var graph = new RaptorNeighborGraphBuilder().Build(points, neighbors, RaptorAlgorithmManifest.Current.DeriveSeed(identity with { Stage = identity.Stage == RaptorStage.LocalUmap ? RaptorStage.LocalNeighbors : RaptorStage.GlobalNeighbors }), token);

        if (graph.IsFailure) return graph.Error;

        var fuzzy = new RaptorFuzzyGraphBuilder().Build(graph.Value, token);

        if (fuzzy.IsFailure) return fuzzy.Error;

        var layout = new RaptorUmapOptimizer().Optimize(fuzzy.Value, dimensions, RaptorAlgorithmManifest.Current.DeriveSeed(identity), token);

        if (layout.IsFailure) return layout.Error;

        double[][] rows = new double[points.Count][];

        foreach (var point in layout.Value)
        {
            token.ThrowIfCancellationRequested();

            rows[point.SourceIndex] = point.Coordinates.ToArray();
        }

        return rows;
    }

    internal static Result<RaptorPosteriorModel> ObserveDefaultFit(double[][] points, ulong seed,
        TapestryClusterIdentity identity, IProgress<RaptorFitObservation> diagnostics, CancellationToken token, long callOrdinal = 0)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        token.ThrowIfCancellationRequested();

        FitObserver observer = new(diagnostics, identity, points.Length, token, callOrdinal);

        try
        {
            observer.Emit(RaptorFitObservationPhase.Started);

            token.ThrowIfCancellationRequested();

            Result<RaptorPosteriorModel> result = Fit(points, seed, token, observer);

            token.ThrowIfCancellationRequested();

            observer.Emit(result.IsSuccess ? RaptorFitObservationPhase.Completed : RaptorFitObservationPhase.Refused);

            return result;
        }
        catch (Exception primary)
        {
            // A failed observer cannot reliably accept another notification. Its original fault wins.
            if (observer.Failed) throw;

            try
            {
                observer.Emit(primary is OperationCanceledException && token.IsCancellationRequested
                    ? RaptorFitObservationPhase.Canceled : RaptorFitObservationPhase.Faulted);
            }
            catch (Exception secondary)
            {
                throw new AggregateException(primary, secondary);
            }

            throw;
        }
    }

    private static Result<RaptorPosteriorModel> Fit(double[][] points, ulong seed, CancellationToken token) =>
        Fit(points, seed, token, null);

    private static Result<RaptorPosteriorModel> Fit(double[][] points, ulong seed, CancellationToken token,
        IProgress<RaptorGmmCandidate>? diagnostics)
    {
        var result = new RaptorGaussianMixture(diagnostics: diagnostics).SelectByBic(points, seed, token);

        return result.IsFailure ? result.Error : new Fitted(points, result.Value);
    }

    private sealed class FitObserver : IProgress<RaptorGmmCandidate>
    {
        private readonly IProgress<RaptorFitObservation> _diagnostics;

        private readonly CancellationToken _token;

        private readonly int _layer;

        private readonly RaptorStage _stage;

        private readonly string _identity;

        private readonly int _samples;

        private readonly long _callOrdinal;

        internal FitObserver(IProgress<RaptorFitObservation> diagnostics, TapestryClusterIdentity identity,
            int samples, CancellationToken token, long callOrdinal)
        {
            _diagnostics = diagnostics;

            _token = token;

            _layer = identity.Layer;

            _stage = identity.Stage;

            _samples = samples;

            _callOrdinal = callOrdinal;

            _identity = RaptorIdentityHash.OfParts([RaptorAlgorithmManifest.Current.Fingerprint(),
                ((int)identity.Scope.Kind).ToString(CultureInfo.InvariantCulture), identity.Scope.Id,
                identity.Layer.ToString(CultureInfo.InvariantCulture), identity.Stage.ToString(), identity.StageKey, identity.MemberDigest]);
        }

        internal bool Failed { get; private set; }

        public void Report(RaptorGmmCandidate candidate)
        {
            // The fitter has already attempted this candidate: retain its truthful diagnostic even
            // if cancellation arrived after its arithmetic, then honor the supplied token.
            Emit(RaptorFitObservationPhase.Candidate, candidate);

            _token.ThrowIfCancellationRequested();
        }

        internal void Emit(RaptorFitObservationPhase phase, RaptorGmmCandidate? candidate = null)
        {
            try
            {
                _diagnostics.Report(new(phase, _layer, _stage, _identity, _samples, candidate) { CallOrdinal = _callOrdinal });
            }
            catch
            {
                Failed = true;

                throw;
            }
        }
    }

    private sealed class Fitted(double[][] points, RaptorGmmFit fit) : RaptorPosteriorModel
    {
        public override int Components => fit.Weights.Length;

        public override Result<double> Posterior(int index, Span<double> destination) => fit.Posterior(points[index], destination);
    }

    private static bool Valid(IReadOnlyList<RaptorPoint> points, CancellationToken token)
    {
        if (points.Count > TapestryLimits.MaxLeavesPerScope) return false;

        var ids = new HashSet<string>(StringComparer.Ordinal);

        int width = points.Count == 0 ? 0 : points[0]?.Vector.Length ?? 0;

        foreach (var p in points)
        {
            token.ThrowIfCancellationRequested();

            if (p is null || string.IsNullOrWhiteSpace(p.StableId) || !ids.Add(p.StableId) || width == 0 || p.Vector.Length != width) return false;

            bool nonzero = false;

            foreach (float value in p.Vector.Span)
            {
                token.ThrowIfCancellationRequested();

                if (!float.IsFinite(value)) return false;

                nonzero |= value != 0;
            }

            if (!nonzero) return false;
        }

        return true;
    }

    private static Error Invalid() => new("Raptor.InvalidInput", "Clustering input or posterior is invalid.");

    private sealed record Selection(int Index, double Probability, RaptorRepairReason Repair);
}
