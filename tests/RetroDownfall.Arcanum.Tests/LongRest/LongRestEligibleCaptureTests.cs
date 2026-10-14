using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Capture freezes the sources a sweep could ever use. A retired, pinned or suppressed memory can be neither an anchor nor a neighbour, so
// it is not a leaf: it is not folded, summarized, embedded or sent to a provider, and it costs no page of the corpus.
public sealed class LongRestEligibleCaptureTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_freezes_only_the_sources_a_sweep_could_use(bool protectedPartition)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        string[] memories = [.. Enumerable.Range(0, 7).Select(static index => $"00000000-0000-4000-8000-{index + 1:D12}")];

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        for (int index = 0; index < memories.Length; index++)
        {
            await h.SeedAsync(memories[index], $"opal observation {index}");
        }

        // 0 stays eligible; 1 is retired; 2 is pinned; 3 and 4 are consolidated, with 3 the survivor that stays eligible; 5 stays eligible;
        // 6 is retired as well.
        await h.Saga.Store.RetireAsync(memories[1], AnnalContentDigest.ForSagaMemory("opal observation 1"), DateTimeOffset.UtcNow, CancellationToken.None);

        await h.Saga.Store.SetPinAsync(memories[2], true, DateTimeOffset.UtcNow, CancellationToken.None);

        await h.Saga.Store.RetireAsync(memories[6], AnnalContentDigest.ForSagaMemory("opal observation 6"), DateTimeOffset.UtcNow, CancellationToken.None);

        LongRestReceipt receipt = LongRestSweepHarness.Require(await new SagaLongRestService(h.Saga.Context, TimeProvider.System).ApplyAsync(
            new(LongRestTransformationKind.EquivalentObservations, [await h.TargetAsync(memories[3]), await h.TargetAsync(memories[4])]), CancellationToken.None));

        Assert.Equal(LongRestOutcome.Applied, receipt.Outcome);

        Assert.Equal(1L, await h.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        CovenantDigest? partition = null;

        if (protectedPartition)
        {
            foreach (string memory in memories)
            {
                partition = await h.ProtectAsync(Guid.Parse(memory), provenance);
            }
        }

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        _ = await h.CaptureAsync(null, partition, protectedPartition ? authority : null);

        string[] leaves = await LeavesAsync(h);

        Assert.Equal([memories[0], memories[3], memories[5]], leaves);

        // The leaf nodes, the lexical index and the edges' endpoints cover exactly those leaves and no others.
        Assert.Equal(3L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 0;"));

        Assert.Equal(3L, await h.ScalarAsync("SELECT count(*) FROM " + (protectedPartition ? "long_rest_protected_fts" : "long_rest_clean_fts") + ";"));
    }

    [SkippableFact]
    public async Task Capture_pages_past_ineligible_sources_without_spending_pages_on_them()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        const int sources = 140;

        for (int index = 0; index < sources; index++)
        {
            await h.SeedAsync($"00000000-0000-4000-8000-{index + 1:D12}", $"observation {index}");
        }

        // The last twenty memory ids are retired, so a page of 128 holds every eligible source with room to spare.
        for (int index = sources - 20; index < sources; index++)
        {
            await h.Saga.Store.RetireAsync($"00000000-0000-4000-8000-{index + 1:D12}", AnnalContentDigest.ForSagaMemory($"observation {index}"), DateTimeOffset.UtcNow, CancellationToken.None);
        }

        LongRestDiscoveryGeneration generation = await h.StartAsync();

        LongRestSourcePage page = LongRestSweepHarness.Require(await h.Store.CaptureNextSourcePageAsync(generation.GenerationId, generation.Revision, null, CancellationToken.None));

        Assert.Equal(sources - 20, page.Leaves.Length);

        Assert.False(page.HasMore);
    }

    private static async Task<string[]> LeavesAsync(LongRestSweepHarness h)
    {
        List<string> leaves = [];

        await using Microsoft.Data.Sqlite.SqliteCommand command = h.Connection.CreateCommand();

        command.CommandText = "SELECT MemoryId FROM long_rest_discovery_leaves ORDER BY MemoryId;";

        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            leaves.Add(reader.GetString(0));
        }

        return [.. leaves];
    }
}
