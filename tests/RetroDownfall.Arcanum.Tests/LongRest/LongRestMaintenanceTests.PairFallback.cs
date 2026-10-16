using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Pair Fallback. A clique the model refuses as a whole may still hold two memories that state one fact: a model that is shown three
// paraphrases at once and told that "most sets contain few or no duplicates" is not the model that is shown the two. So after the clique,
// the anchor is put to the model once with each member, and the pair it asserts is folded under the same gate and the same Fold Check.
public sealed partial class LongRestMaintenanceTests
{
    private static readonly string[] PairFallbackTexts =
    [
        "The user's dog is a beagle called Biscuit.",
        "The user owns a beagle named Biscuit.",
        "The user's dog Biscuit sleeps on a beagle-sized bed.",
    ];

    [Fact]
    public async Task A_clique_the_model_refuses_whole_is_asked_again_as_pairs_and_the_pair_it_asserts_is_folded()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: 3,
            sourceText: index => PairFallbackTexts[index]);

        int sends = 0;

        f.Handler.VectorFor = body =>
        {
            float[] vector = new float[64];

            vector[int.Parse(Regex.Match(body, @"Summary (\d+)").Groups[1].Value) % 64] = 1;

            return vector;
        };

        // The model refuses the three together, and asserts one pair of the two it is shown: the first two paraphrases.
        f.Handler.InsideSend = () =>
        {
            string body = f.Handler.LastBody!;

            if (!LongRestScriptedVerdicts.IsAdjudication(body))
            {
                f.Handler.ChatText = "{\"summary\":\"Summary " + ++sends + "\"}";
            }
            else if (LongRestScriptedVerdicts.MemoriesIn(body) == 2 && body.Contains("owns a beagle named Biscuit", StringComparison.Ordinal)
                && body.Contains("is a beagle called Biscuit", StringComparison.Ordinal))
            {
                f.Handler.ChatText = LongRestScriptedVerdicts.AllPairs(2);
            }
            else
            {
                f.Handler.ChatText = LongRestScriptedVerdicts.NoMatches(body);
            }

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        LongRestProviderPayload[] requests = [.. f.Payloads.Where(static payload => payload.Kind == LongRestProviderPayloadKind.Adjudication)];

        // The clique of three was asked first, and then the anchor was asked with each of the other two.
        Assert.Equal(3, requests[0].Inputs.Length);

        Assert.Contains(requests, static request => request.Inputs.Length == 2);

        // Exactly the asserted pair was folded: one receipt of two memories, and the third paraphrase is still current.
        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_receipts WHERE KindCode = 2;"));

        Assert.Equal(2L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_receipt_inputs;"));

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));
    }

    [Fact]
    public async Task A_clique_the_model_folds_whole_is_not_asked_again_as_pairs()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: 3,
            sourceText: index => PairFallbackTexts[index]);

        int sends = 0;

        f.Handler.VectorFor = body =>
        {
            float[] vector = new float[64];

            vector[int.Parse(Regex.Match(body, @"Summary (\d+)").Groups[1].Value) % 64] = 1;

            return vector;
        };

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.AllPairs(f.Handler.LastBody!) : "{\"summary\":\"Summary " + ++sends + "\"}";

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        // One request, for the three; the pair units that followed it were disposed with no request because their memories were consolidated.
        Assert.Single(f.Payloads, static payload => payload.Kind == LongRestProviderPayloadKind.Adjudication);

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_receipts WHERE KindCode = 2;"));

        Assert.Equal(3L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_receipt_inputs;"));

        Assert.Equal(2L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));
    }
}
