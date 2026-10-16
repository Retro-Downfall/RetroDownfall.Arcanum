using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The structural guarantee of the Pairing Gate: whatever the model answers, no memory is folded into another unless they are byte-identical or
// at least as close as the anchor's Background Floor. The adjudicator here is hostile: it asserts that every pair of every request states the same
// fact. A request can then only hold memories the gate paired, so the worst a lying model can do is fold what the embeddings already call near.
public sealed partial class LongRestMaintenanceTests
{
    private const int HostileDimensions = 32;

    // Three topics of four paraphrases each, in a space where two paraphrases have a cosine of 0.9; two exact duplicates; six traps that share a
    // topic's template words and are unrelated in meaning (cosine about 0.28 to it); and one foreigner placed just under the floor of the topic
    // it resembles. The index is the memory's place in id order.
    private sealed record HostileMemory(string Label, string Text, float[] Vector);

    private static float[] HostileVector(int own, int axis, double lean)
    {
        float[] vector = new float[HostileDimensions];

        vector[axis] = (float)lean;

        vector[own] = (float)Math.Sqrt(1 - lean * lean);

        return vector;
    }

    private static HostileMemory[] HostileCorpus(double foreignerLean)
    {
        // Within a topic two paraphrases lean 0.9487 on its axis and are otherwise orthogonal: cosine 0.9487 * 0.9487 = 0.9.
        double paraphrase = Math.Sqrt(0.9);

        List<HostileMemory> memories = [];

        string[][] topics =
        [
            ["The user's dog is a beagle called Biscuit.", "The user owns a beagle named Biscuit.", "A beagle named Biscuit is the user's dog.", "The user has a pet beagle, Biscuit."],
            ["The staging database is refreshed every Sunday night.", "Every Sunday night the staging database is refreshed.", "Staging data gets refreshed on Sunday nights.", "The staging database refresh happens each Sunday night."],
            ["The user prefers black coffee without sugar.", "The user drinks coffee black, no sugar.", "Black coffee with no sugar is what the user prefers.", "The user takes their coffee black and unsweetened."],
        ];

        string[] labels = ["dog", "staging", "coffee"];

        string[] trapTexts =
        [
            "The user's laptop is a grey ultrabook called Biscuit.",
            "The user's phone is a rugged handset named Biscuit.",
            "The staging area for deliveries is cleaned every Monday morning.",
            "The staging crew rehearses a play every Friday afternoon.",
            "The user prefers black clothing for formal events.",
            "The user drinks tea, never coffee, in the evening.",
        ];

        int own = 3;

        for (int phrase = 0; phrase < 4; phrase++)
        {
            for (int topic = 0; topic < 3; topic++)
            {
                memories.Add(new(labels[topic], topics[topic][phrase], HostileVector(own++, topic, paraphrase)));
            }
        }

        // Two exact duplicates, in the vector space of the topic they copy: a byte-identical memory is paired with its twin whatever its vector says.
        memories.Add(new("dog", topics[0][0], HostileVector(own++, 0, paraphrase)));

        memories.Add(new("staging", topics[1][0], HostileVector(own++, 1, paraphrase)));

        for (int trap = 0; trap < trapTexts.Length; trap++)
        {
            memories.Add(new("trap", trapTexts[trap], HostileVector(own++, trap / 2, 0.3)));
        }

        memories.Add(new("foreigner", "The user has a collie called Rex at the farm.", HostileVector(own, 0, foreignerLean)));

        return [.. memories];
    }

    // The floor of every topic member and of the foreigner, by the plain reference, in the memory id order the corpus is seeded in.
    private static int[] HostileFloors(HostileMemory[] corpus) => [.. Enumerable.Range(0, corpus.Length).Select(index => LongRestPairingReference.Floor([.. corpus.Select(static memory => memory.Vector)], index))];

    // The foreigner leans on the first topic's axis so that its cosine to each of that topic's paraphrases is one thousandth under the lowest floor
    // of any of them and of its own: just too far for any anchor to pair it, and as near as the gate allows.
    private static HostileMemory[] SettleHostileCorpus()
    {
        double paraphrase = Math.Sqrt(0.9);

        double lean = 0.6;

        for (int round = 0; round < 12; round++)
        {
            HostileMemory[] corpus = HostileCorpus(lean);

            int[] floors = HostileFloors(corpus);

            int lowest = Enumerable.Range(0, corpus.Length).Where(index => corpus[index].Label is "dog" or "foreigner").Min(index => floors[index]);

            double next = (lowest / 1000.0 - 0.001) / paraphrase;

            if (Math.Abs(next - lean) < 1e-6) return corpus;

            lean = next;
        }

        return HostileCorpus(lean);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hostile_adjudicator_that_asserts_every_pair_folds_nothing_across_topics(bool protectedSources)
    {
        HostileMemory[] corpus = SettleHostileCorpus();

        string Id(int index) => $"00000000-0000-4000-8000-{index + 1:D12}";

        Assert.True(corpus.Length >= 16 + 1);

        Dictionary<string, string> label = corpus.Select((memory, index) => (Id: Id(index), memory.Label)).ToDictionary(static item => item.Id, static item => item.Label);

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: protectedSources, sourceCount: corpus.Length,
            sourceId: index => Guid.Parse(Id(index)), sourceText: index => corpus[index].Text, sessionless: true);

        for (int index = 0; index < corpus.Length; index++) await f.H.SetEmbeddingAsync(Id(index), corpus[index].Vector);

        // The preconditions are the corpus's own: the traps and the foreigner are under every floor they could be paired at, and the topic is over it.
        int[] floors = HostileFloors(corpus);

        LongRestPairingVectors geometry = new();

        LongRestSourceLeaf Leaf(int index) => LongRestPackingData.Leaf(index, corpus[index].Text, null, [.. corpus[index].Vector]);

        for (int anchor = 0; anchor < corpus.Length; anchor++)
        {
            for (int other = 0; other < corpus.Length; other++)
            {
                if (anchor == other || corpus[anchor].Label == "trap" || corpus[anchor].Text == corpus[other].Text) continue;

                bool sameTopic = corpus[anchor].Label == corpus[other].Label && corpus[anchor].Label != "foreigner";

                bool paired = LongRestPairing.Clears(geometry.Cosine(Leaf(anchor), Leaf(other)), floors[anchor]);

                Assert.Equal(sameTopic, paired);
            }
        }

        int sends = 0;

        f.Handler.VectorFor = body =>
        {
            float[] vector = new float[64];

            vector[int.Parse(Regex.Match(body, @"Summary (\d+)").Groups[1].Value) % 64] = 1;

            return vector;
        };

        // The hostile adjudicator: every adjudication request, whatever it holds, is answered "all of these are the same fact".
        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.AllPairs(f.Handler.LastBody!) : "{\"summary\":\"Summary " + ++sends + "\"}";

            return Task.CompletedTask;
        };

        using AsyncFlowMetricCapture metrics = new("arcanum.long_rest.sweep.skipped_candidates");

        LongRestMaintenanceCapability? capability = protectedSources
            ? LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None)) : null;

        LongRestSweepProgress progress = LongRestSweepHarness.Require(protectedSources
            ? await f.Coordinator.ResumeAsync(f.Operation, capability, CancellationToken.None)
            : await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        // No request holds memories of two topics, a trap or the foreigner: the gate admitted nothing the embeddings call far.
        LongRestProviderPayload[] requests = [.. f.Payloads.Where(static payload => payload.Kind == LongRestProviderPayloadKind.Adjudication)];

        Assert.NotEmpty(requests);

        foreach (LongRestProviderPayload request in requests)
        {
            string[] labels = [.. request.Inputs.Select(input => label[input.Snapshot.MemoryId])];

            Assert.Single(labels.Distinct());

            Assert.DoesNotContain(labels[0], new[] { "trap", "foreigner" });
        }

        // Every receipt joined memories of one topic and none a trap or the foreigner.
        Dictionary<string, List<string>> receipts = [];

        await using (Microsoft.Data.Sqlite.SqliteCommand command = f.H.Connection.CreateCommand())
        {
            command.CommandText = "SELECT ReceiptId, MemoryId FROM long_rest_receipt_inputs ORDER BY ReceiptId, Ordinal;";

            await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                if (!receipts.TryGetValue(reader.GetString(0), out List<string>? members)) receipts[reader.GetString(0)] = members = [];

                members.Add(reader.GetString(1));
            }
        }

        Assert.NotEmpty(receipts);

        foreach (List<string> members in receipts.Values)
        {
            Assert.Single(members.Select(member => label[member]).Distinct());

            Assert.DoesNotContain(label[members[0]], new[] { "trap", "foreigner" });
        }

        // The two exact duplicates were folded locally: their units sent no request, so no request holds the two copies of a text.
        foreach (LongRestProviderPayload request in requests)
        {
            Assert.Equal(request.Inputs.Length, request.Inputs.Select(static input => input.Snapshot.ContentHash).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts WHERE KindCode = 1;"));

        // Every topic ends with exactly one memory that is not suppressed; the traps and the foreigner are all still current.
        HashSet<string> suppressed = [];

        await using (Microsoft.Data.Sqlite.SqliteCommand command = f.H.Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT claim.SubjectId FROM long_rest_suppressions suppression
                JOIN annal_heads head ON head.CurrentVersionId = suppression.SourceVersionId AND head.SubjectStoreCode = 1
                JOIN annal_claims claim ON claim.ClaimId = head.ClaimId AND claim.SubjectStoreCode = 1;
                """;

            await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync()) suppressed.Add(reader.GetString(0));
        }

        foreach (string topic in new[] { "dog", "staging", "coffee" })
        {
            Assert.Single(label, pair => pair.Value == topic && !suppressed.Contains(pair.Key));
        }

        Assert.All(label.Where(pair => pair.Value is "trap" or "foreigner"), pair => Assert.DoesNotContain(pair.Key, suppressed));

        // The traps were nominated by their shared words and refused by the gate, and the refusals were counted without identity.
        Assert.True(metrics.Sum("arcanum.long_rest.sweep.skipped_candidates", "reason", "unpaired") > 0);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode IN (1, 2, 3);"));
    }

    private static readonly string[] CollapsedUnrelated =
    [
        "The user's dog is a beagle called Biscuit.", "The project is released under the MIT license.", "Production deploys go to Frankfurt.",
        "The user avoids writing Python.", "The user prefers dark mode in every editor.", "The user plans a trip to Lisbon.",
        "The staging database is refreshed on Sunday nights.", "The user's favourite colour is teal.", "The user drinks black coffee.",
        "The office wifi password rotates quarterly.", "The user's cat is a grey tabby.", "The team standup is on Tuesdays.",
        "The user reads science fiction.", "The user's bike is a road bike.", "The user commutes by train.",
        "The user's sister lives in Oslo.", "The user is learning Japanese.", "The user plays chess on weekends.",
        "The user's laptop is an ultrabook.", "The user dislikes cilantro.",
    ];

    // An embedding model that has collapsed answers one direction for every input. Nothing then says which memories resemble which, and the
    // Pairing Gate must fail closed whether the generation is large enough to have a distribution of its own (twenty memories) or small enough to
    // use the fallback (ten): a hostile adjudicator that asserts every pair folds nothing but the one pair of byte-identical memories.
    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 10)]
    [InlineData(false, 20)]
    [InlineData(true, 20)]
    public async Task A_collapsed_embedding_with_a_hostile_adjudicator_folds_nothing_but_exact_duplicates(bool protectedSources, int count)
    {
        // The last memory repeats the first one's text, which is the one thing a collapsed space can still tell: they are the same bytes.
        string Text(int index) => index == count - 1 ? CollapsedUnrelated[0] : CollapsedUnrelated[index];

        string Id(int index) => $"00000000-0000-4000-8000-{index + 1:D12}";

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: protectedSources, sourceCount: count,
            sourceId: index => Guid.Parse(Id(index)), sourceText: Text, sessionless: true);

        float[] collapsed = new float[HostileDimensions];

        collapsed[0] = 1;

        for (int index = 0; index < count; index++) await f.H.SetEmbeddingAsync(Id(index), collapsed);

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

        LongRestMaintenanceCapability? capability = protectedSources
            ? LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None)) : null;

        LongRestSweepProgress progress = LongRestSweepHarness.Require(protectedSources
            ? await f.Coordinator.ResumeAsync(f.Operation, capability, CancellationToken.None)
            : await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        // No memory was put to the model beside another: every unit that was queued is a pair of byte-identical memories, which the maintenance
        // folds locally.
        Assert.DoesNotContain(f.Payloads, static payload => payload.Kind == LongRestProviderPayloadKind.Adjudication);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts WHERE KindCode = 1;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND PairingFloorPermille <> 1000;"));
    }
}
