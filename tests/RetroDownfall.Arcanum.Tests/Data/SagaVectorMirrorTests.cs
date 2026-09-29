using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The Saga vector mirror is classified from the database in front of the store, never from the
/// process's accelerator flag.
/// </summary>
/// <remarks>
/// Two mirror shapes stand in for what an installation can hold. The plain table is what an
/// accelerator-less build can read and delete from; the FTS5 virtual table stands in for a legacy
/// <c>vec0</c> mirror, which records the same <c>CREATE VIRTUAL TABLE</c> text in <c>sqlite_master</c>
/// and which this runtime could not open. Every mirror row reaches the table through the store's own
/// insert with the flag on, except the stale-row seeds no build this harness composes would write.
/// </remarks>
public sealed class SagaVectorMirrorTests
{
    private const string Original = "the operator prefers tabs";

    private const string Corrected = "the operator prefers spaces";

    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => CancellationToken.None;

    [SkippableTheory]
    [InlineData("absent")]
    [InlineData("plain")]
    [InlineData("virtual")]
    public async Task Classification_reads_the_catalog_not_the_process_flag(string shape)
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        SagaVectorMirrorKind expected = shape switch
        {
            "absent" => SagaVectorMirrorKind.Absent,
            "plain" => SagaVectorMirrorKind.PlainTable,
            _ => SagaVectorMirrorKind.LegacyVirtualTable,
        };

        if (shape == "plain")
        {
            await harness.CreatePlainVectorMirrorAsync();
        }
        else if (shape == "virtual")
        {
            await harness.CreateLegacyVectorMirrorAsync();
        }

        // On for every shape, so the flag cannot be what answers.
        harness.VectorAccelerator.SetAvailable(true);

        Assert.Equal(expected, await SagaVectorMirror.ClassifyAsync(harness.Connection, null, Token));
    }

    [SkippableTheory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Insert_writes_the_mirror_only_while_the_accelerator_is_live(bool live, int rows)
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await harness.CreatePlainVectorMirrorAsync();

        harness.VectorAccelerator.SetAvailable(live);

        SagaMemoryWriteOutcome outcome = await InsertAsync(harness, Guid.NewGuid().ToString());

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

        Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings", "1 = 1"));

        Assert.Equal(rows, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));
    }

    [SkippableTheory]
    [InlineData("delete")]
    [InlineData("delete-all")]
    [InlineData("retire")]
    [InlineData("correct")]
    public async Task A_plain_mirror_row_goes_with_its_memory_while_the_flag_is_off(string verb)
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await harness.CreatePlainVectorMirrorAsync();

        harness.VectorAccelerator.SetAvailable(true);

        string id = Guid.NewGuid().ToString();

        Assert.Equal(SagaMemoryWriteOutcome.Written, await InsertAsync(harness, id));

        // The production insert filled the mirror, not this test.
        Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));

        harness.VectorAccelerator.SetAvailable(false);

        DateTimeOffset now = CreatedAt.AddMinutes(1);

        switch (verb)
        {
            case "delete":
                Assert.True(await harness.Store.DeleteAsync(id, Token));

                break;

            case "delete-all":
                await harness.Store.DeleteAllAsync(Token);

                break;

            case "retire":
                Assert.Equal(
                    SagaCurationOutcomeKind.Applied,
                    (await harness.Store.RetireAsync(id, AnnalContentDigest.ForSagaMemory(Original), now, Token)).Kind);

                break;

            default:
                Assert.Equal(
                    SagaCurationOutcomeKind.Applied,
                    (await harness.Store.CorrectAsync(
                        id,
                        AnnalContentDigest.ForSagaMemory(Original),
                        Corrected,
                        harness.Embedding(2),
                        now,
                        Token)).Kind);

                break;
        }

        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));
    }

    [SkippableFact]
    public async Task Reinstate_removes_a_stale_mirror_row_while_the_flag_is_off()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await harness.CreatePlainVectorMirrorAsync();

        string id = Guid.NewGuid().ToString();

        Assert.Equal(SagaMemoryWriteOutcome.Written, await InsertAsync(harness, id));

        Assert.Equal(
            SagaCurationOutcomeKind.Applied,
            (await harness.Store.RetireAsync(
                id,
                AnnalContentDigest.ForSagaMemory(Original),
                CreatedAt.AddMinutes(1),
                Token)).Kind);

        // An earlier build's residue: nothing this flag-off process runs would have written it.
        await harness.SeedVectorMirrorRowAsync(id);

        SagaCurationOutcome reinstated = await harness.Store.ReinstateAsync(
            id,
            AnnalContentDigest.ForSagaMemory(Original),
            harness.Embedding(1),
            CreatedAt.AddMinutes(2),
            Token);

        Assert.Equal(SagaCurationOutcomeKind.Applied, reinstated.Kind);

        Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings", "1 = 1"));

        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));
    }

    [SkippableFact]
    public async Task A_legacy_virtual_mirror_is_left_alone_by_every_store_write()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await harness.CreateLegacyVectorMirrorAsync();

        string id = Guid.NewGuid().ToString();

        // Nothing in this build can write into a virtual mirror, so its row is seeded as residue.
        await harness.SeedVectorMirrorRowAsync(id);

        harness.VectorAccelerator.SetAvailable(true);

        SagaMemoryWriteOutcome inserted = await InsertAsync(harness, id);

        SagaCurationOutcome corrected = await harness.Store.CorrectAsync(
            id,
            AnnalContentDigest.ForSagaMemory(Original),
            Corrected,
            harness.Embedding(2),
            CreatedAt.AddMinutes(1),
            Token);

        SagaCurationOutcome retired = await harness.Store.RetireAsync(
            id,
            AnnalContentDigest.ForSagaMemory(Corrected),
            CreatedAt.AddMinutes(2),
            Token);

        SagaCurationOutcome reinstated = await harness.Store.ReinstateAsync(
            id,
            AnnalContentDigest.ForSagaMemory(Corrected),
            harness.Embedding(3),
            CreatedAt.AddMinutes(3),
            Token);

        await harness.Store.DeleteAllAsync(Token);

        Assert.Equal(SagaMemoryWriteOutcome.Written, inserted);

        Assert.All(
            new[] { corrected, retired, reinstated },
            outcome => Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind));

        Assert.Equal(0, await harness.CountAsync("saga_memories", "1 = 1"));

        Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings_vec", "1 = 1"));
    }

    private static Task<SagaMemoryWriteOutcome> InsertAsync(SagaStoreHarness harness, string id) =>
        harness.Store.InsertAsync(
            id,
            Original,
            CreatedAt,
            sessionId: null,
            tags: null,
            source: "saga-extraction",
            harness.Embedding(1),
            Token);
}
