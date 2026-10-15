using System.Diagnostics;
using System.Globalization;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal sealed partial class LongRestQualificationRun
{
    private static readonly string[] ResumableParkCodes =
    [
        "long_rest.interrupted",
        "long_rest.maintenance_deferred",
        "long_rest.worker_unavailable",
        "long_rest.readmit_requested",
    ];

    private static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Scenario B: a sweep whose host is killed with SIGKILL while a Long Rest chat request is in flight (so its attempt is
    /// <c>Sent</c> and the host cannot settle it), restarted on the same home. The restarted host must park the interrupted sweep, its
    /// worker must readmit it, the sweep must complete, the interrupted attempt must be settled rather than left <c>Sent</c>, and the
    /// paid work must be counted once.
    /// </summary>
    public async Task RunKilledHostSweepAsync(CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();

        // A pinned memory that is unpinned is an eligible source no generation holds: the next request starts a new sweep, not a replay.
        LongRestSeededMemory pinned = _corpus.Single(static memory => memory.Role == LongRestCorpusRole.Pinned);

        await Api.UnpinAsync(pinned.Id, cancellationToken);

        int proxyStart = Proxy.Position;

        decimal spendBefore = await Api.LocalSpendUsdAsync(cancellationToken);

        LongRestPublishedHost doomed = _host ?? throw new InvalidOperationException("There is no host to kill.");

        TaskCompletionSource<LongRestRecordedRequest> killedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Proxy.SetArrivalHook(request =>
        {
            if (!request.IsLongRestChat)
            {
                return false;
            }

            // SIGKILL while this very request is in flight: the host wrote the attempt as Sent before it sent, and will never write
            // another byte. The forwarder then forwards a request whose client is gone.
            doomed.KillNow();

            _ = killedSignal.TrySetResult(request);

            return true;
        });

        LongRestOperationSnapshot requested = await Api.RequestGlobalSweepAsync(cancellationToken);

        Assert.NotEqual(_cleanSweepOperation, requested.Id);

        _log.Info($"Scenario B: sweep requested, operation {requested.Id:D} ({requested}); waiting for a Long Rest chat request to kill the host under");

        LongRestRecordedRequest killed = await killedSignal.Task.WaitAsync(SweepBudget, cancellationToken);

        Proxy.SetArrivalHook(null);

        await doomed.KillAsync(TimeSpan.FromSeconds(30));

        _retiredHosts.Add(doomed);

        _host = null;

        _log.Info($"Scenario B: host killed with SIGKILL during {LongRestQualificationProxy.Describe(killed)}");

        // What the dead host left behind: an unsettled Sent attempt, and a sweep that did not complete.
        IReadOnlyList<LongRestEffectRow> leftBehind = [.. (await Grimoire.ReadEffectsAsync(cancellationToken)).Where(row => IsSameGuid(row.OperationId, requested.Id))];

        LongRestEffectRow[] unsettled = [.. leftBehind.Where(static row => row.StateCode < AttemptStateSettledLow)];

        Assert.NotEmpty(unsettled);

        Assert.Contains(unsettled, static row => row.StateCode == AttemptStateSent);

        IReadOnlyList<LongRestSweepRow> sweepsAfterKill = await Grimoire.ReadSweepsAsync(cancellationToken);

        Assert.NotEqual(
            SweepPhaseCompleted,
            Assert.Single(sweepsAfterKill, row => IsSameGuid(row.OperationId, requested.Id)).PhaseCode);

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"Scenario B: after SIGKILL the journal held {unsettled.Length} unsettled work item(s) ({string.Join(", ", unsettled.Select(static row => $"attempt state {row.StateCode}, sent {row.SentCount}, paid {row.PaidCount}"))})"));

        Stopwatch restart = Stopwatch.StartNew();

        await StartKnownHostAsync("after SIGKILL", cancellationToken);

        TimeSpan restartReady = restart.Elapsed;

        List<(TimeSpan At, LongRestOperationSnapshot Snapshot)> timeline = [];

        LongRestOperationSnapshot completed = await DriveToCompletionAsync(
            "Scenario B recovery",
            requested.Id,
            RecoveryPollInterval,
            timeline,
            cancellationToken);

        Assert.Equal(LongRestOperationState.Completed, completed.State);

        int parkedIndex = timeline.FindIndex(static entry => entry.Snapshot.State == LongRestOperationState.ReconciliationRequired
            && Array.IndexOf(ResumableParkCodes, entry.Snapshot.TerminalErrorCode ?? string.Empty) >= 0);

        string timelineText = string.Join(" -> ", timeline.Select(static entry => $"{entry.At:mm\\:ss\\.fff} {entry.Snapshot}"));

        // The park can be too brief for a poll to see (a stale lease is parked and readmitted in one pass), so the durable record of it counts
        // too: the sweep row's park stamp is written by a park and again by a readmission, and a free park (the interrupted code is one)
        // spends none of the failure budget, so its readmission count stays zero.
        LongRestSweepRow recovered = Assert.Single(await Grimoire.ReadSweepsAsync(cancellationToken), row => IsSameGuid(row.OperationId, requested.Id));

        bool parkEvidence = parkedIndex >= 0 || recovered.ReadmitCount >= 1 || recovered.ParkedAt is not null;

        Check(
            parkEvidence,
            "Scenario B: the restarted host left no evidence that it parked the interrupted sweep and readmitted it (no ReconciliationRequired state under a resumable code in the status timeline, and no park stamp or readmission on the sweep row). Timeline: " + timelineText);

        TimeSpan parkedAt = parkedIndex >= 0 ? timeline[parkedIndex].At : TimeSpan.Zero;

        string parkedCode = parkedIndex >= 0 ? timeline[parkedIndex].Snapshot.TerminalErrorCode ?? "-" : "(not seen by polling every 500 ms)";

        bool readmitted = timeline.Any(static entry => entry.Snapshot.State == LongRestOperationState.Running && entry.Snapshot.AttemptCount > 1);

        TimeSpan resumedAfter = timeline
            .Where(static entry => entry.Snapshot.AttemptCount > 1)
            .Select(static entry => entry.At)
            .DefaultIfEmpty(TimeSpan.Zero)
            .First();

        Check(
            completed.AttemptCount >= 2,
            $"Scenario B: the sweep completed on attempt {completed.AttemptCount}; a readmission by the worker is a second attempt.");

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"Scenario B: the restarted host was ready in {restartReady:mm\\:ss\\.fff}; the sweep stayed on its dead owner's attempt until {resumedAfter:mm\\:ss} after the restart, then was parked as '{parkedCode}' and readmitted "
            + $"(sweep row: {recovered.ReadmitCount} readmissions, parked at {recovered.ParkedAt ?? "-"}; {(readmitted ? "seen running again" : "not seen running")}); completed on attempt {completed.AttemptCount} after {Took(clock)} in all. Timeline: {timelineText}"));

        IReadOnlyList<LongRestRecordedRequest> traffic = Proxy.Since(proxyStart);

        // Settlement of the interrupted attempt, and the paid work counted once.
        IReadOnlyList<LongRestEffectRow> effects = [.. (await Grimoire.ReadEffectsAsync(cancellationToken)).Where(row => IsSameGuid(row.OperationId, requested.Id))];

        AssertEffectsSettled(effects);

        foreach (LongRestEffectRow row in unsettled)
        {
            LongRestEffectRow settled = effects.Single(candidate => candidate.WorkItemId == row.WorkItemId);

            Observe(string.Create(
                CultureInfo.InvariantCulture,
                $"Scenario B: the interrupted unit {Short(settled.WorkItemId)} ended in attempt state {settled.StateCode} (sent {settled.SentCount}, paid {settled.PaidCount}, failed {settled.FailureCount}, code {settled.FailureCode ?? "-"}); it had been sent {row.SentCount} time(s) before the kill"));

            Check(settled.PaidCount >= 1, "Scenario B: the interrupted unit was never paid for after the restart.");

            Check(
                settled.FailureCount >= 1 || settled.SentCount == row.SentCount,
                "Scenario B: the interrupted attempt was neither settled as a failure (unknown usage) nor released as not sent.");
        }

        GradeConsolidations("Scenario B", await Api.ListMemoriesAsync(cancellationToken));

        decimal spendAfter = await Api.LocalSpendUsdAsync(cancellationToken);

        // A send that ended without a result is unknown usage and is never estimated or written as zero, so its accounting run is left open and
        // its reservation stays held until it expires: the budget counts that hold as spend. One hold per interrupted unit is the design;
        // the figure is reconciled with it and reported, and a hold beyond the interrupted units is a finding.
        (int heldReservations, decimal heldUsd) = await Grimoire.ReadOutstandingReservationsAsync(cancellationToken);

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"Scenario B: {heldReservations} budget reservation(s) held open for unknown usage, {heldUsd:0.000000} USD reserved (the interrupted send)"));

        Check(
            heldReservations <= unsettled.Length,
            $"Scenario B: {heldReservations} budget reservations are held open but only {unsettled.Length} unit(s) were interrupted.");

        AssertAccounting("Scenario B", effects, traffic, spendAfter - spendBefore, killed, heldUsd);

        await AssertNeverSentAsync(traffic, LongRestCorpusRole.Retired, cancellationToken);

        AssertServedBy([.. traffic.Where(request => request.Sequence != killed.Sequence)], requireBothKinds: false);

        ObserveModelBehavior("Scenario B", traffic);

        ObservePacks("Scenario B", traffic);

        ObserveAnswerQuality("Scenario B", traffic);

        IReadOnlyDictionary<string, double> metrics = await Api.ReadMetricsAsync(cancellationToken);

        // The park of a stale lease is written by the durable recovery handler, which the park counter does not cover (it counts the owning
        // worker's and a fresh Session request's parks), so only the readmission is a counter this scenario is owed.
        double parkedCount = SumMetric(metrics, "long_rest_sweep_parked");

        double readmittedCount = SumMetric(metrics, "long_rest_sweep_readmitted");

        Check(readmittedCount >= 1, "Scenario B: the restarted host counted no readmitted Long Rest sweep (arcanum.long_rest.sweep.readmitted) although its worker readmitted one.");

        Observe(string.Create(CultureInfo.InvariantCulture, $"Scenario B counters on the restarted host: parked {parkedCount}, readmitted {readmittedCount}. Metrics endpoint: {DescribeMetrics(metrics)}"));

        await ObserveRowsAsync(
            "Scenario B budget reservations (status, reserved, reconciled, expires)",
            "SELECT Status, ReservedUsd, ReconciledUsd, ExpiresAt FROM BudgetReservations ORDER BY CreatedAt DESC LIMIT 8;",
            cancellationToken);

        // Supersede-on-Completion across generations: the first sweep keeps its Completed operation but loses its derived data, and an
        // unchanged request now replays the newest sweep.
        await AssertSupersededAsync(requested.Id, cancellationToken);
    }

    private async Task AssertSupersededAsync(Guid newest, CancellationToken cancellationToken)
    {
        LongRestOperationSnapshot first = await Api.GetOperationAsync(_cleanSweepOperation, cancellationToken);

        Check(first.State == LongRestOperationState.Completed, $"Scenario B: the first sweep's operation is {first.State} after its successor completed; it should stay Completed in history.");

        (IReadOnlyList<LongRestNodeRow> nodes, _) = await Grimoire.ReadHierarchyAsync(_cleanSweepGeneration!, cancellationToken);

        Check(nodes.Count == 0, $"Scenario B: the first sweep's generation still holds {nodes.Count} nodes after its successor completed; the derived data should be deleted.");

        long firstIndexRows = await Grimoire.CountCleanIndexRowsAsync(_cleanSweepGeneration!, cancellationToken);

        Check(firstIndexRows == 0, $"Scenario B: the first sweep's generation still holds {firstIndexRows} search-index rows.");

        IReadOnlyList<LongRestSweepRow> sweeps = await Grimoire.ReadSweepsAsync(cancellationToken);

        LongRestSweepRow second = Assert.Single(sweeps, row => IsSameGuid(row.OperationId, newest));

        Assert.NotNull(second.GenerationId);

        long secondIndexRows = await Grimoire.CountCleanIndexRowsAsync(second.GenerationId!, cancellationToken);

        Check(secondIndexRows == 0, $"Scenario B: the completed second generation still holds {secondIndexRows} search-index rows.");

        LongRestOperationSnapshot replay = await Api.RequestGlobalSweepAsync(cancellationToken);

        Check(replay.Id == newest, $"Scenario B: an unchanged request returned operation {replay.Id:D} instead of replaying the newest completed sweep {newest:D}.");

        Observe("Scenario B: the first sweep's generation was superseded (operation Completed, nodes and index rows checked) and an unchanged request was checked against the newest sweep");
    }
}
