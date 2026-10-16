using System.Diagnostics;
using System.Globalization;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal sealed partial class LongRestQualificationRun
{
    /// <summary>The share of the folds the labelled clusters allow that a hostile adjudicator must still be made to apply.</summary>
    private const double MutinyMinimumFoldShare = 0.9;

    /// <summary>
    /// The Mutiny Scenario: a clean Global sweep over a freshly seeded corpus in which the forwarder rewrites every adjudication answer into
    /// the all-pairs verdict, whatever the model said ("every one of these memories states the same fact"). It proves the half of the Pairing
    /// Gate's guarantee that does not depend on the model: no pack ever held memories of two topics, so a model that agrees to everything folds
    /// every cluster and folds nothing across topics. The only wrong folds it can make are the pairs the embedding model leaves at or above
    /// their anchor's floor, which the calibration counts; on a model that separates topics that count is zero.
    /// </summary>
    public async Task RunMutinySweepAsync(CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();

        int proxyStart = Proxy.Position;

        Proxy.SetAnswerRewriter(static request => LongRestScriptedVerdicts.AllPairs(LongRestQualificationProxy.CountMemories(request.UserText)));

        LongRestOperationSnapshot requested;

        LongRestOperationSnapshot completed;

        try
        {
            requested = await Api.RequestGlobalSweepAsync(cancellationToken);

            _log.Info($"Mutiny Scenario: sweep requested, operation {requested.Id:D} ({requested})");

            completed = await DriveToCompletionAsync(
                "Mutiny Scenario sweep",
                requested.Id,
                PollInterval,
                timeline: null,
                LongRestRetryScope.AnyFault,
                operatorRetryTimes: null,
                cancellationToken);
        }
        finally
        {
            Proxy.SetAnswerRewriter(null);
        }

        Assert.Equal(LongRestOperationState.Completed, completed.State);

        Observe($"Mutiny Scenario: sweep completed in {Took(clock)} ({completed})");

        IReadOnlyList<LongRestRecordedRequest> traffic = Proxy.Since(proxyStart);

        LongRestRecordedRequest[] adjudications = [.. traffic.Where(static request => request.Kind == LongRestProviderRequestKind.Adjudication)];

        Check(
            adjudications.Length > 0 && adjudications.All(static request => request.Rewritten || request.StatusCode is not (>= 200 and < 300)),
            "Mutiny Scenario: an adjudication answer reached the host without being rewritten into the all-pairs verdict.");

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"Mutiny Scenario: {adjudications.Count(static request => request.Rewritten)} of {adjudications.Length} adjudication answers were rewritten into the all-pairs verdict"));

        // The generation the sweep completed, for the calibration of this installation's vectors (the count of pairs the gate cannot exclude).
        IReadOnlyList<LongRestSweepRow> sweeps = await Grimoire.ReadSweepsAsync(cancellationToken);

        LongRestSweepRow sweep = Assert.Single(sweeps, row => IsSameGuid(row.OperationId, requested.Id));

        Assert.NotNull(sweep.GenerationId);

        IReadOnlyList<LongRestApiMemory> memories = await Api.ListMemoriesAsync(cancellationToken);

        await CaptureCalibrationAsync(sweep.GenerationId!, memories, cancellationToken);

        // Wrong folds must not exceed the pairs of different topics the embedding model leaves at or above a floor; on a model that separates
        // topics that is none. And a hostile adjudicator must still fold nearly everything the clusters allow.
        FoldGrade grade = GradeConsolidations("Mutiny Scenario", memories, allowedWrong: _crossPairsAtOrAboveFloor);

        int required = (int)Math.Ceiling(grade.PossibleFolds * MutinyMinimumFoldShare);

        Check(
            grade.RightFolds >= required,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Mutiny Scenario: a model that agrees to everything folded {grade.RightFolds} of the {grade.PossibleFolds} folds the labelled clusters allow; at least {required} are expected, because every pack is a clique of one topic."));

        ObservePacks("Mutiny Scenario", traffic);

        AssertOperatorStateUntouched(memories);

        await AssertNeverSentAsync(traffic, LongRestCorpusRole.Retired, cancellationToken);

        await AssertNeverSentAsync(traffic, LongRestCorpusRole.Pinned, cancellationToken);

        IReadOnlyList<LongRestEffectRow> effects = [.. (await Grimoire.ReadEffectsAsync(cancellationToken)).Where(row => IsSameGuid(row.OperationId, requested.Id))];

        AssertEffectsSettled(effects);

        await CheckHealthAsync("Mutiny Scenario", cancellationToken);
    }
}
