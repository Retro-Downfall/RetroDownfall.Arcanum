using System.Diagnostics;
using System.Globalization;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal sealed partial class LongRestQualificationRun
{
    private const int MaximumOperatorRetries = 24;

    private const int AttemptStateSent = (int)LongRestDispatchAttemptState.Sent;

    private const int SweepPhaseCompleted = (int)LongRestSweepPhase.Completed;

    /// <summary>The generation the clean sweep completed, whose hierarchy the calibration reads before a successor replaces it.</summary>
    private string? _cleanSweepGeneration;

    private Guid _cleanSweepOperation;

    private int _operatorRetries;

    private int _cleanSweepProxyStart;

    private int _cleanSweepProxyEnd;

    private IReadOnlyList<LongRestEffectRow> _cleanSweepEffects = [];

    /// <summary>
    /// Scenario A: a clean Global sweep from request to completion, against the real chat and embedding models. It must complete,
    /// adjudicate and summarize, leave the retired and the pinned memory untouched and unsent, end its generation's search index
    /// (Supersede-on-Completion), be served by the fast model alone, and have its paid units recorded exactly once.
    /// </summary>
    public async Task RunCleanSweepAsync(CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();

        int proxyStart = Proxy.Position;

        decimal spendBefore = await Api.LocalSpendUsdAsync(cancellationToken);

        LongRestOperationSnapshot requested = await Api.RequestGlobalSweepAsync(cancellationToken);

        _log.Info($"Scenario A: sweep requested, operation {requested.Id:D} ({requested})");

        LongRestOperationSnapshot completed = await DriveToCompletionAsync(
            "Scenario A sweep",
            requested.Id,
            PollInterval,
            timeline: null,
            LongRestRetryScope.AnyFault,
            operatorRetryTimes: null,
            cancellationToken);

        Assert.Equal(LongRestOperationState.Completed, completed.State);

        Observe($"Scenario A: clean sweep completed in {Took(clock)} ({completed})");

        _cleanSweepOperation = requested.Id;

        _cleanSweepProxyStart = proxyStart;

        _cleanSweepProxyEnd = Proxy.Position;

        IReadOnlyList<LongRestRecordedRequest> traffic = Proxy.Since(proxyStart);

        // The sweep row, its generation and the durable evidence.
        IReadOnlyList<LongRestSweepRow> sweeps = await Grimoire.ReadSweepsAsync(cancellationToken);

        LongRestSweepRow sweep = Assert.Single(sweeps, row => IsSameGuid(row.OperationId, requested.Id));

        Assert.Equal(SweepPhaseCompleted, sweep.PhaseCode);

        Assert.NotNull(sweep.GenerationId);

        _cleanSweepGeneration = sweep.GenerationId;

        // The calibration needs the completed generation, which the next completed sweep deletes, and it is the one result of the run
        // that must exist whatever else fails: it is taken the moment the sweep is known to have completed.
        await CaptureCalibrationAsync(sweep.GenerationId!, await Api.ListMemoriesAsync(cancellationToken), cancellationToken);

        long summaries = await Grimoire.CountAsync("long_rest_accepted_summaries", cancellationToken);

        long adjudications = await Grimoire.CountAsync("long_rest_adjudications", cancellationToken);

        long receipts = await Grimoire.CountAsync("long_rest_receipts", cancellationToken);

        Check(summaries >= 1, "Scenario A: the completed sweep accepted no summary.");

        Check(adjudications >= 1, "Scenario A: the completed sweep accepted no adjudication.");

        Check(receipts >= 1, "Scenario A: the completed sweep wrote no receipt.");

        IReadOnlyList<(int Code, long Count)> decisions = await Grimoire.CountAdjudicationDecisionsAsync(cancellationToken);

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"Scenario A evidence: {summaries} accepted summaries, {adjudications} accepted adjudications ({string.Join(", ", decisions.Select(static decision => $"{LongRestQualificationChecks.DescribeAdjudicationKind(decision.Code)} x{decision.Count}"))}), {receipts} receipts"));

        // Receipts reach the memories: at least one memory is consolidated, and the API names its receipt.
        IReadOnlyList<LongRestApiMemory> memories = await Api.ListMemoriesAsync(cancellationToken);

        LongRestApiMemory[] consolidated = [.. memories.Where(static memory => memory.ConsolidatedIntoMemoryId is not null)];

        Check(consolidated.Length > 0, "Scenario A: no memory names a Long Rest receipt or a survivor after the sweep.");

        Check(
            consolidated.All(static memory => !string.IsNullOrEmpty(memory.LongRestReceiptId)),
            "Scenario A: a consolidated memory does not name its Long Rest receipt.");

        Observe(string.Create(CultureInfo.InvariantCulture, $"Scenario A: {consolidated.Length} of {memories.Count} memories consolidated into a survivor"));

        await CheckReceiptsAsync("Scenario A", consolidated, cancellationToken);

        // Every consolidation is graded against the corpus's labels: none may join memories of different topics (a memory folded into another
        // topic is suppressed from recall for good), both exact-duplicate groups are folded without a model call, and at least half of the
        // paraphrase folds the labelled clusters allow are made.
        FoldGrade grade = GradeConsolidations("Scenario A", memories);

        Check(
            grade.ExactFolds >= grade.PossibleExact,
            string.Create(CultureInfo.InvariantCulture, $"Scenario A: only {grade.ExactFolds} of the {grade.PossibleExact} exact-duplicate folds were made; an exact duplicate is folded without a model call."));

        int requiredParaphrases = (grade.PossibleParaphrase + 1) / 2;

        Check(
            grade.ParaphraseFolds >= requiredParaphrases,
            string.Create(CultureInfo.InvariantCulture, $"Scenario A: only {grade.ParaphraseFolds} of the {grade.PossibleParaphrase} possible paraphrase folds were made; at least {requiredParaphrases} are expected."));

        // The retired and the pinned memory were not part of the corpus: untouched, and their text never left the process.
        AssertOperatorStateUntouched(memories);

        await AssertNeverSentAsync(traffic, LongRestCorpusRole.Retired, cancellationToken);

        await AssertNeverSentAsync(traffic, LongRestCorpusRole.Pinned, cancellationToken);

        // Supersede-on-Completion: the generation's index rows ended with its completion while its nodes remain evidence.
        long indexRows = await Grimoire.CountCleanIndexRowsAsync(sweep.GenerationId!, cancellationToken);

        Check(indexRows == 0, $"Scenario A: the completed generation still holds {indexRows} search-index rows; they should end with its completion.");

        (IReadOnlyList<LongRestNodeRow> nodes, _) = await Grimoire.ReadHierarchyAsync(sweep.GenerationId!, cancellationToken);

        Check(nodes.Count > 0, "Scenario A: the completed generation retained no nodes.");

        Observe(string.Create(CultureInfo.InvariantCulture, $"Scenario A: completed generation holds {nodes.Count} nodes ({nodes.Count(static node => node.Layer > 0)} summaries) and {indexRows} search-index rows"));

        CheckSummaryBound("Scenario A", nodes);

        // Which models served which calls, and with which credential.
        AssertServedBy(traffic, requireBothKinds: true);

        ObserveModelBehavior("Scenario A", traffic);

        ObservePacks("Scenario A", traffic);

        int invalidAnswers = ObserveAnswerQuality("Scenario A", traffic);

        Check(
            invalidAnswers == 0,
            string.Create(CultureInfo.InvariantCulture, $"Scenario A: {invalidAnswers} adjudication answers were not an instance of the member-verdict shape; each is a paid call that parks the sweep for an operator."));

        Check(
            _operatorRetries == 0,
            string.Create(CultureInfo.InvariantCulture, $"Scenario A: the sweep was parked {_operatorRetries} times by a fault only an operator clears; an answer the model gives must never need one."));

        // Every paid unit was settled exactly once and the host's accounting equals what the provider reported.
        IReadOnlyList<LongRestEffectRow> effects = await Grimoire.ReadEffectsAsync(cancellationToken);

        _cleanSweepEffects = [.. effects.Where(row => IsSameGuid(row.OperationId, requested.Id))];

        AssertEffectsSettled(_cleanSweepEffects);

        decimal spendAfter = await Api.LocalSpendUsdAsync(cancellationToken);

        AssertAccounting("Scenario A", _cleanSweepEffects, traffic, spendAfter - spendBefore, killedRequest: null);

        // Counters and health: nothing parked for a transient reason or a stale generation, no turn was refused its clean request, and
        // every park only an operator clears is one the run answered.
        IReadOnlyDictionary<string, double> metrics = await Api.ReadMetricsAsync(cancellationToken);

        Check(
            SumMetric(metrics, "long_rest_sweep_parked", "resume_later") + SumMetric(metrics, "long_rest_sweep_parked", "restart_generation") == 0,
            "Scenario A: the host counted a park for a transient reason or a stale generation during a sweep that met neither.");

        Check(SumMetric(metrics, "long_rest_clean_schedule_refused") == 0, "Scenario A: the host counted a refused clean schedule although no Session turn ran.");

        double operatorParks = SumMetric(metrics, "long_rest_sweep_parked", "needs_operator");

        string counters = string.Join(
            ", ",
            metrics
                .Where(static pair => pair.Key.Contains("long_rest", StringComparison.Ordinal))
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value}")));

        Observe($"Scenario A counters on the host: {(counters.Length == 0 ? "no Long Rest series" : counters)}; the run answered {_operatorRetries} needs-operator parks. Metrics endpoint: {DescribeMetrics(metrics)}");

        if (operatorParks < _operatorRetries)
        {
            RecordFinding(string.Create(
                CultureInfo.InvariantCulture,
                $"Scenario A: the sweep was parked {_operatorRetries} times by a fault only an operator clears, but arcanum.long_rest.sweep.parked counted {operatorParks} needs-operator parks."));
        }

        await CheckHealthAsync("Scenario A", cancellationToken);

        // An unchanged request replays the completed sweep: the same operation, and not one more provider call.
        int beforeReplay = Proxy.Position;

        LongRestOperationSnapshot replay = await Api.RequestGlobalSweepAsync(cancellationToken);

        Check(replay.Id == requested.Id && replay.State == LongRestOperationState.Completed, $"Scenario A: an unchanged request returned operation {replay.Id:D} ({replay.State}) instead of replaying the completed sweep {requested.Id:D}.");

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        Check(
            !Proxy.Since(beforeReplay).Any(static request => request.Kind is not LongRestProviderRequestKind.Models),
            "Scenario A: the replay of a completed sweep made a provider call.");

        _log.Info("Scenario A: an unchanged request replayed the completed sweep without a provider call");
    }

    private void AssertOperatorStateUntouched(IReadOnlyList<LongRestApiMemory> memories)
    {
        LongRestSeededMemory retired = _corpus.Single(static memory => memory.Role == LongRestCorpusRole.Retired);

        LongRestSeededMemory pinned = _corpus.Single(static memory => memory.Role == LongRestCorpusRole.Pinned);

        LongRestApiMemory retiredNow = memories.Single(memory => memory.Id == retired.Id);

        LongRestApiMemory pinnedNow = memories.Single(memory => memory.Id == pinned.Id);

        Check(retiredNow.Retired, "Scenario A: the retired memory is no longer retired.");

        Check(retiredNow.ConsolidatedIntoMemoryId is null, "Scenario A: the retired memory was consolidated into a survivor.");

        Check(retired.Content == retiredNow.Content, "Scenario A: the retired memory's text changed.");

        Check(pinnedNow.Pinned, "Scenario A: the pinned memory is no longer pinned.");

        Check(pinnedNow.ConsolidatedIntoMemoryId is null, "Scenario A: the pinned memory was consolidated into a survivor.");

        Check(pinned.Content == pinnedNow.Content, "Scenario A: the pinned memory's text changed.");

        Check(
            !memories.Any(memory => memory.ConsolidatedIntoMemoryId == retired.Id || memory.ConsolidatedIntoMemoryId == pinned.Id),
            "Scenario A: a memory was consolidated into the retired or the pinned memory as its survivor.");
    }

    /// <summary>
    /// Waits for a sweep to complete the way an operator would: a park the worker resumes by itself is waited out, and a park only an
    /// operator can clear (a paid answer the model got wrong, which the fault table classifies as needs-operator) is answered with
    /// <c>arcanum operation retry</c>, the documented remedy, up to a bound. Every retry is an observation: a real model's malformed
    /// answers are part of what this qualification measures. Where the scope is <see cref="LongRestRetryScope.AfterWorkerReadmission"/> an
    /// answer before the worker has readmitted the sweep is also a finding, and each retry's time is recorded so that a readmission can be told from an operator's.
    /// </summary>
    private async Task<LongRestOperationSnapshot> DriveToCompletionAsync(
        string what,
        Guid operation,
        TimeSpan interval,
        List<(TimeSpan At, LongRestOperationSnapshot Snapshot)>? timeline,
        LongRestRetryScope retryScope,
        List<TimeSpan>? operatorRetryTimes,
        CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();

        int retries = 0;

        string? retriedFor = null;

        LongRestOperationSnapshot final = await LongRestQualificationPolling.UntilAsync(
            what,
            async token =>
            {
                LongRestOperationSnapshot snapshot = await Api.GetOperationAsync(operation, token);

                if (timeline is not null && (timeline.Count == 0 || timeline[^1].Snapshot.ToString() != snapshot.ToString()))
                {
                    timeline.Add((clock.Elapsed, snapshot));
                }

                if (snapshot.State == LongRestOperationState.ReconciliationRequired
                    && LongRestFaultPolicy.Classify(snapshot.TerminalErrorCode) == LongRestFaultClass.NeedsOperator
                    && !string.Equals(retriedFor, snapshot.ToString(), StringComparison.Ordinal))
                {
                    retriedFor = snapshot.ToString();

                    Assert.True(
                        ++retries <= MaximumOperatorRetries,
                        $"{what} was parked {retries} times by faults only an operator can clear (last: {snapshot.TerminalErrorCode}); the real models did not let it finish.");

                    Observe($"{what}: parked as '{snapshot.TerminalErrorCode}' (a fault only an operator clears); retrying as the operator (retry {retries})");

                    // Where the scope does not allow an operator's help the run still gives it, to go on and report the rest, but it is a finding.
                    bool workerHasReadmitted = timeline is not null
                        && LongRestQualificationChecks.ReadmittedBeforeAnyOperator(timeline, operatorRetryTimes ?? []);

                    string? finding = LongRestQualificationChecks.OperatorRetryFinding(retryScope, what, snapshot.TerminalErrorCode, workerHasReadmitted);

                    if (finding is not null)
                    {
                        RecordFinding(finding);
                    }

                    operatorRetryTimes?.Add(clock.Elapsed);

                    await Api.RetryOperationAsync(operation, token);
                }

                return snapshot;
            },
            static snapshot => snapshot.IsTerminal,
            static snapshot => snapshot.ToString(),
            SweepBudget,
            interval,
            _log,
            cancellationToken);

        _operatorRetries += retries;

        return final;
    }

    private async Task<LongRestOperationSnapshot> WaitForOperationAsync(
        string what,
        Guid operation,
        Func<LongRestOperationSnapshot, bool> done,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await LongRestQualificationPolling.UntilAsync(
            what,
            token => Api.GetOperationAsync(operation, token),
            done,
            static snapshot => snapshot.ToString(),
            timeout,
            PollInterval,
            _log,
            cancellationToken);

    /// <summary>The ledger spells operation identities without hyphens in some tables; compare them as identities.</summary>
    private static bool IsSameGuid(string stored, Guid identity) =>
        Guid.TryParse(stored, out Guid parsed) && parsed == identity;

    private static double SumMetric(IReadOnlyDictionary<string, double> metrics, string fragment, string? label = null) =>
        metrics
            .Where(pair => pair.Key.Contains(fragment, StringComparison.Ordinal)
                && (label is null || pair.Key.Contains($"\"{label}\"", StringComparison.Ordinal)))
            .Sum(static pair => pair.Value);
}
