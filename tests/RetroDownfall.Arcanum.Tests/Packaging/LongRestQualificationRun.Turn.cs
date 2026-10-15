using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal sealed partial class LongRestQualificationRun
{
    private static readonly TimeSpan DefaultModelTurnBudget = TimeSpan.FromMinutes(12);

    private static readonly TimeSpan CancelBudget = TimeSpan.FromMinutes(6);

    /// <summary>
    /// Scenario C: one ordinary Session turn on the default model in the same home, with Long Rest on and clean debt outstanding. The
    /// turn must succeed on the default model, the clean request it makes must not fail it, and the sweep that request starts belongs
    /// to the background worker: the turn neither waits for it nor depends on it. The sweep is then cancelled, so the run ends with
    /// nothing in flight, and must reach a terminal state.
    /// </summary>
    public async Task RunOrdinaryTurnAsync(CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();

        // Clean debt: pinning and unpinning a source advances its mutation epoch, so the completed sweep no longer covers it.
        LongRestApiMemory candidate = (await Api.ListMemoriesAsync(cancellationToken))
            .First(static memory => !memory.Retired && !memory.Pinned && memory.ConsolidatedIntoMemoryId is null);

        await Api.PinAsync(candidate.Id, cancellationToken);

        await Api.UnpinAsync(candidate.Id, cancellationToken);

        HashSet<Guid> sweepsBefore = [.. (await Api.ListSweepOperationsAsync(cancellationToken)).Select(static operation => operation.Id)];

        int proxyStart = Proxy.Position;

        IReadOnlyDictionary<string, double> metricsBefore = await Api.ReadMetricsAsync(cancellationToken);

        string marker = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

        string prompt = $"Reply with exactly TURN_OK; MARKER={marker} and no other text.";

        LongRestCliResult turn = await LongRestPublishedCli.RunAsync(
            _options.Executable,
            Installation,
            DefaultModelTurnBudget,
            "--plain",
            "--print",
            "--no-context",
            "run",
            "--new",
            "--model",
            _options.DefaultModel,
            "--temperature",
            "0",
            "--max-tokens",
            "96",
            "--unattended",
            prompt);

        string answer = PublishedArcanumOllamaQualificationTests.NormalizeModelAnswer(turn.StandardOutput);

        Observe(string.Create(CultureInfo.InvariantCulture, $"Scenario C: the ordinary turn on '{_options.DefaultModel}' answered in {Took(clock)}"));

        Assert.Contains(marker, answer, StringComparison.Ordinal);

        IReadOnlyList<LongRestRecordedRequest> traffic = Proxy.Since(proxyStart);

        LongRestRecordedRequest[] turnCalls = [.. traffic.Where(static request => request.Kind == LongRestProviderRequestKind.Chat)];

        Assert.NotEmpty(turnCalls);

        Assert.Contains(turnCalls, request => string.Equals(request.Model, _options.DefaultModel, StringComparison.Ordinal));

        Assert.All(turnCalls, request =>
        {
            Assert.Equal(LongRestRequestCredential.Chat, request.Credential);

            Assert.True(request.StatusCode is >= 200 and < 300, $"The turn's chat request #{request.Sequence} returned {request.StatusCode}.");
        });

        // The turn's clean request scheduled the debt as a background sweep, or found the scope's sweep already owned; either way the
        // turn did not fail and the refusal counter did not move for a reason that would have mattered.
        IReadOnlyList<LongRestOperationSnapshot> sweepsAfter = await Api.ListSweepOperationsAsync(cancellationToken);

        LongRestOperationSnapshot[] scheduled = [.. sweepsAfter.Where(operation => !sweepsBefore.Contains(operation.Id))];

        Assert.NotEmpty(scheduled);

        Assert.DoesNotContain(scheduled, static operation => operation.State == LongRestOperationState.Failed);

        IReadOnlyDictionary<string, double> metricsAfter = await Api.ReadMetricsAsync(cancellationToken);

        double refusedBefore = SumMetric(metricsBefore, "long_rest_clean_schedule_refused");

        double refusedAfter = SumMetric(metricsAfter, "long_rest_clean_schedule_refused");

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"Scenario C: the turn scheduled {scheduled.Length} new clean sweep(s) ({string.Join(", ", scheduled.Select(static operation => operation.ToString()))}); clean-schedule refusals {refusedBefore} -> {refusedAfter}"));

        // Nothing is left in flight: cancel whatever the turn started and require it to reach a terminal state.
        foreach (LongRestOperationSnapshot operation in scheduled.Where(static operation => !operation.IsTerminal))
        {
            await Api.CancelOperationAsync(operation.Id, cancellationToken);

            LongRestOperationSnapshot ended = await WaitForOperationAsync(
                "Scenario C cancelled sweep",
                operation.Id,
                static snapshot => snapshot.IsTerminal,
                CancelBudget,
                cancellationToken);

            Observe($"Scenario C: the cancelled sweep ended {ended}");
        }

        await ObserveRowsAsync(
            "Scenario C budget reservations (status, reserved, reconciled, expires)",
            "SELECT Status, ReservedUsd, ReconciledUsd, ExpiresAt FROM BudgetReservations ORDER BY CreatedAt DESC LIMIT 8;",
            cancellationToken);

        decimal outstanding = await Api.LocalSpendUsdAsync(cancellationToken);

        Observe(string.Create(CultureInfo.InvariantCulture, $"Scenario C: the budget snapshot ends the run at {outstanding:0.000000} USD of local spend"));
    }
}
