using System.Globalization;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryRecursiveScaleProbe
{
    [Fact]
    public void Real_bypassed_fits_keep_zero_search_attempts_and_continue_hashing_after_the_sample_cap()
    {
        RaptorPoint[] points = RaptorScaleProbe.Corpus(16);

        TapestryClusterIdentity identity = new(Scope, 1, RaptorStage.GlobalMixture, "bounded-measurement", points.Select(point => point.StableId));

        double[][] rows = Enumerable.Range(0, 16).Select(_ => new double[] { 1, 1 }).ToArray();

        CountingClusterer measured = new();

        for (int call = 1; call <= 64; call++)
        {
            Assert.True(RaptorClusterer.ObserveDefaultFit(rows, 71, identity, measured, CancellationToken.None, call).IsSuccess);
        }

        string before = measured.Describe();

        Assert.True(RaptorClusterer.ObserveDefaultFit(rows, 71, identity, measured, CancellationToken.None, 65).IsSuccess);

        measured.AssertFitMeasurementsComplete();

        string after = measured.Describe();

        Assert.Equal(64, measured.FitSamples.Count);

        Assert.Contains("fits_completed=65", after, StringComparison.Ordinal);

        Assert.Contains("bic_attempted=0", after, StringComparison.Ordinal);

        Assert.Contains("bic_search_bypasses=65", after, StringComparison.Ordinal);

        Assert.NotEqual(before.Split("fit_digest=", StringSplitOptions.None)[1].Split(' ')[0],
            after.Split("fit_digest=", StringSplitOptions.None)[1].Split(' ')[0]);
    }

    private sealed partial class CountingClusterer
    {
        private readonly List<string> _fitSamples = [];

        private readonly long[] _fitLayers = new long[17];

        private FitMeasurement? _activeFit;

        private long _fitsStarted;

        private long _fitsCompleted;

        private long _fitsRefused;

        private long _fitsCanceled;

        private long _fitsFaulted;

        private long _globalFits;

        private long _localFits;

        private long _bicAttempted;

        private long _bicRefused;

        private long _searchBypasses;

        private long _closedAttempts;

        private string _fitDigest = TapestryHash.OfParts([]);

        internal IReadOnlyList<string> FitSamples { get { lock (_gate) return _fitSamples.ToArray(); } }

        public void Report(RaptorFitObservation value)
        {
            lock (_gate)
            {
                Assert.InRange(value.Layer, 1, 16);

                Assert.InRange(value.CallOrdinal, 1, long.MaxValue);

                Assert.Contains(value.Stage, new[] { RaptorStage.GlobalMixture, RaptorStage.LocalMixture });

                Assert.Equal(64, value.FitIdentity.Length);

                if (value.Phase == RaptorFitObservationPhase.Started)
                {
                    Assert.Null(_activeFit);

                    _activeFit = new(value, _calls);

                    _fitsStarted++;

                    _fitLayers[value.Layer]++;

                    if (value.Stage == RaptorStage.GlobalMixture) _globalFits++;

                    else _localFits++;

                    return;
                }

                FitMeasurement fit = Assert.IsType<FitMeasurement>(_activeFit);

                Assert.Equal(fit.Start.FitIdentity, value.FitIdentity);

                Assert.Equal(fit.Start.CallOrdinal, value.CallOrdinal);

                Assert.Equal(fit.Start.Layer, value.Layer);

                Assert.Equal(fit.Start.Stage, value.Stage);

                Assert.Equal(fit.Start.SampleCount, value.SampleCount);

                if (value.Phase == RaptorFitObservationPhase.Candidate)
                {
                    RaptorGmmCandidate candidate = Assert.IsType<RaptorGmmCandidate>(value.Candidate);

                    if (candidate.SearchBypassed)
                    {
                        Assert.Equal(0, fit.Attempts);

                        Assert.Equal(0, fit.Bypasses);

                        Assert.Equal(1, candidate.Components);

                        fit.Bypasses++;

                        _searchBypasses++;
                    }
                    else
                    {
                        Assert.Equal(0, fit.Bypasses);

                        Assert.Equal(fit.Attempts + 1, candidate.Components);

                        fit.Attempts++;

                        _bicAttempted++;
                    }

                    if (candidate.RefusalCode is not null)
                    {
                        fit.Refusals++;

                        if (!candidate.SearchBypassed) _bicRefused++;
                    }

                    string bic = candidate.Bic is { } number
                        ? BitConverter.DoubleToInt64Bits(number).ToString("X16", CultureInfo.InvariantCulture) : "none";

                    fit.Digest = TapestryHash.OfParts([fit.Digest, candidate.Components.ToString(CultureInfo.InvariantCulture),
                        candidate.SearchBypassed ? "bypassed" : "attempted", candidate.RefusalCode ?? "none", bic]);

                    return;
                }

                Assert.Contains(value.Phase, new[] { RaptorFitObservationPhase.Completed, RaptorFitObservationPhase.Refused,
                    RaptorFitObservationPhase.Canceled, RaptorFitObservationPhase.Faulted });

                if (value.Phase == RaptorFitObservationPhase.Completed)
                {
                    if (fit.Bypasses == 0) Assert.Equal(Math.Min(50, value.SampleCount) - 1, fit.Attempts);

                    else
                    {
                        Assert.Equal(1, fit.Bypasses);

                        Assert.Equal(0, fit.Attempts);
                    }

                    _fitsCompleted++;
                }
                else if (value.Phase == RaptorFitObservationPhase.Refused) _fitsRefused++;

                else if (value.Phase == RaptorFitObservationPhase.Canceled) _fitsCanceled++;

                else _fitsFaulted++;

                _closedAttempts = checked(_closedAttempts + fit.Attempts);

                _fitDigest = TapestryHash.OfParts([_fitDigest, fit.Digest, value.Phase.ToString(),
                    fit.Attempts.ToString(CultureInfo.InvariantCulture), fit.Bypasses.ToString(CultureInfo.InvariantCulture)]);

                if (_fitSamples.Count < 64)
                {
                    _fitSamples.Add(FormattableString.Invariant($"event=fit cluster_call={fit.ClusterCall} core_call={value.CallOrdinal} fit_key={value.FitIdentity} layer={value.Layer} stage={value.Stage} samples={value.SampleCount} outcome={value.Phase} bic_attempts={fit.Attempts} search_bypasses={fit.Bypasses} refused_fits={fit.Refusals} fit_digest={fit.Digest}"));
                }

                _activeFit = null;
            }
        }

        internal void AssertFitMeasurementsComplete()
        {
            lock (_gate)
            {
                Assert.Null(_activeFit);

                Assert.True(_fitsStarted > 0);

                Assert.Equal(_fitsStarted, _fitsCompleted);

                Assert.Equal(0, _fitsRefused);

                Assert.Equal(0, _fitsCanceled);

                Assert.Equal(0, _fitsFaulted);

                Assert.Equal(_globalFits + _localFits, _fitsStarted);

                Assert.Equal(_closedAttempts, _bicAttempted);

                Assert.InRange(_fitSamples.Count, 1, 64);
            }
        }

        private string DescribeFits() => FormattableString.Invariant($"bic_measurement=observed fits_started={_fitsStarted} fits_completed={_fitsCompleted} fits_refused={_fitsRefused} fits_canceled={_fitsCanceled} fits_faulted={_fitsFaulted} global_fits={_globalFits} local_fits={_localFits} bic_attempted={_bicAttempted} bic_refused={_bicRefused} bic_search_bypasses={_searchBypasses} active_fit={_activeFit?.Start.FitIdentity ?? "none"} active_attempts={_activeFit?.Attempts ?? 0} fit_digest={_fitDigest} fit_sample_cap=64 layer_fit_counts={string.Join(',', Enumerable.Range(1, 16).Where(layer => _fitLayers[layer] > 0).Select(layer => FormattableString.Invariant($"{layer}:{_fitLayers[layer]}")))}");

        private sealed class FitMeasurement(RaptorFitObservation start, int clusterCall)
        {
            internal RaptorFitObservation Start { get; } = start;

            internal int ClusterCall { get; } = clusterCall;

            internal int Attempts { get; set; }

            internal int Bypasses { get; set; }

            internal int Refusals { get; set; }

            internal string Digest { get; set; } = TapestryHash.OfParts([start.FitIdentity,
                start.CallOrdinal.ToString(CultureInfo.InvariantCulture), clusterCall.ToString(CultureInfo.InvariantCulture)]);
        }
    }
}
