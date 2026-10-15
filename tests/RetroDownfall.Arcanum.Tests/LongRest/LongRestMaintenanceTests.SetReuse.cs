using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The cost guard of decision reuse. Paid adjudication is the expensive call, and k memories that name one another are one question.
public sealed partial class LongRestMaintenanceTests
{
    // The question a clique of k mutual neighbours is. The model is asked about the whole set once; if it says the set is one fact the pairs that
    // follow need no question, and if it says otherwise each pair is its own question, asked once whichever of its two members is the anchor.
    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public async Task Mutual_neighbours_cost_one_adjudication_when_the_model_accepts_the_set(int sourceCount)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: sourceCount,
            sourceText: index => $"alpha beta{Word(index)} gamma{Word(index)} note");

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.AllPairs(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        // Every memory has the same neighbours, so the anchor's set is all of them: one request, and the pairs it queued are closed without one.
        LongRestProviderPayload request = Assert.Single(f.Payloads, static payload => payload.Kind == LongRestProviderPayloadKind.Adjudication);

        Assert.Equal(sourceCount, request.Inputs.Length);

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_receipts WHERE OutcomeCode = 1;"));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public async Task Mutual_neighbours_that_the_model_refuses_as_a_set_cost_each_pair_once(int sourceCount)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: sourceCount,
            sourceText: index => $"alpha beta{Word(index)} gamma{Word(index)} note");

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        using AsyncFlowMetricCapture metrics = new("arcanum.long_rest.sweep.reused_decisions");

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        // The set once, then every unordered pair of its members once: (a, b) and (b, a) are one question, however many anchors meet it.
        int pairs = sourceCount * (sourceCount - 1) / 2;

        LongRestProviderPayload[] requests = [.. f.Payloads.Where(static payload => payload.Kind == LongRestProviderPayloadKind.Adjudication)];

        Assert.Equal(1 + pairs, requests.Length);

        Assert.Equal(1 + pairs, requests.Select(static request => string.Join('|', request.Inputs.Select(static input => input.Target.MemoryId).Order(StringComparer.Ordinal))).Distinct().Count());

        Assert.Equal(1L + pairs, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_adjudications;"));

        // Every set an anchor finds that was already decided is a reuse: the set once for each later anchor, and each pair once for the anchors
        // that meet it after the first.
        Assert.Equal((sourceCount - 1) + (long)sourceCount * (sourceCount - 1) - pairs, metrics.Sum("arcanum.long_rest.sweep.reused_decisions"));

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));
    }

    // A word that is not a number: the memories say one kind of thing, and a digit in each would be a different number, which an equivalence refuses.
    private static string Word(int index) => string.Concat(Enumerable.Range(0, 3).Select(place => (char)('a' + ((index >> (4 * place)) & 15))));
}
