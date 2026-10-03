using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The Saga store's raw deletes ask the labelled-artifact guard inside their own write transaction, and
/// refuse when the label table cannot be read.
/// </summary>
/// <remarks>
/// <para>Two properties, each one a way the guard used to be a convention rather than a guarantee. A
/// check made before the transaction opens and a delete made inside it are two moments, and a label
/// written between them is removed with its artifact and leaves nothing behind. And a guard that answered
/// "nothing is labelled" whenever it could not read the label table turned a damaged Grimoire into
/// permission to delete (§10.20.2).</para>
///
/// <para>The first is exercised with a second writer on its own connection that tries to label the
/// artifact right after the guard answers, which is where a label written between the check and the
/// delete would land. A delete holding its write lock leaves that writer blocked; one that asked
/// outside its transaction lets it commit, and the label then outlives the memory it names.</para>
/// </remarks>
public sealed class SagaLabeledDeleteTransactionTests
{

    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task A_bulk_delete_that_cannot_read_the_label_table_refuses_and_removes_nothing()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(
            annalsEnabled: false,
            labeledArtifactGuard: FixtureLabeledArtifactGuard.For);

        string[] ids = await InsertAsync(harness, 3);

        await UnreadableLabelTableAsync(harness);

        LabeledArtifactRefusalException refused = await Assert.ThrowsAsync<LabeledArtifactRefusalException>(
            () => harness.Store.DeleteAllAsync(Token));

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.DoesNotContain(ids[0], refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, await harness.CountAsync("saga_memories", "1 = 1"));

        Assert.Equal(3, await harness.CountAsync("saga_memory_embeddings", "1 = 1"));

    }

    [SkippableFact]
    public async Task A_single_delete_that_cannot_read_the_label_table_refuses_and_removes_nothing()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(
            annalsEnabled: false,
            labeledArtifactGuard: FixtureLabeledArtifactGuard.For);

        string[] ids = await InsertAsync(harness, 2);

        await UnreadableLabelTableAsync(harness);

        LabeledArtifactRefusalException refused = await Assert.ThrowsAsync<LabeledArtifactRefusalException>(
            () => harness.Store.DeleteAsync(ids[0], Token));

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.Equal(2, await harness.CountAsync("saga_memories", "1 = 1"));

        Assert.Equal(2, await harness.CountAsync("saga_memory_embeddings", "1 = 1"));

    }

    [SkippableFact]
    public async Task A_bulk_delete_asks_the_guard_inside_its_transaction_so_no_label_slips_in_before_the_delete()
    {

        ArcanumDbContext? sibling = null;

        LabelIntruder intruder = new(
            _ => Task.FromResult((SqliteConnection)sibling!.Database.GetDbConnection()),
            SensitiveArtifactKind.Saga);

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(
            annalsEnabled: false,
            labeledArtifactGuard: db => new LabelIntrusionGuard(FixtureLabeledArtifactGuard.For(db), intruder));

        string[] ids = await InsertAsync(harness, 3);

        await using ArcanumDbContext siblingContext = harness.CreateSiblingContext();

        sibling = siblingContext;

        await sibling.Database.OpenConnectionAsync(Token);

        intruder.ArtifactId = Guid.Parse(ids[0]);

        await harness.Store.DeleteAllAsync(Token);

        Assert.Equal(1, intruder.Attempts);

        Assert.Equal(1, intruder.Blocked);

        Assert.Equal(0, await harness.CountAsync("saga_memories", "1 = 1"));

        // No label survives the memory it names.
        Assert.Equal(0, await harness.CountAsync("artifact_sensitivity", "1 = 1"));

        Assert.Equal(1, intruder.AskedInsideTransaction);

        Assert.Equal(0, intruder.AskedOutsideTransaction);

    }

    [SkippableFact]
    public async Task A_single_delete_asks_the_guard_inside_its_transaction_so_no_label_slips_in_before_the_delete()
    {

        ArcanumDbContext? sibling = null;

        LabelIntruder intruder = new(
            _ => Task.FromResult((SqliteConnection)sibling!.Database.GetDbConnection()),
            SensitiveArtifactKind.Saga);

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(
            annalsEnabled: false,
            labeledArtifactGuard: db => new LabelIntrusionGuard(FixtureLabeledArtifactGuard.For(db), intruder));

        string[] ids = await InsertAsync(harness, 2);

        await using ArcanumDbContext siblingContext = harness.CreateSiblingContext();

        sibling = siblingContext;

        await sibling.Database.OpenConnectionAsync(Token);

        intruder.ArtifactId = Guid.Parse(ids[0]);

        Assert.True(await harness.Store.DeleteAsync(ids[0], Token));

        Assert.Equal(1, intruder.Attempts);

        Assert.Equal(1, intruder.Blocked);

        Assert.Equal(1, await harness.CountAsync("saga_memories", "1 = 1"));

        // No label survives the memory it names.
        Assert.Equal(0, await harness.CountAsync("artifact_sensitivity", "1 = 1"));

        Assert.Equal(1, intruder.AskedInsideTransaction);

        Assert.Equal(0, intruder.AskedOutsideTransaction);

    }

    private static async Task<string[]> InsertAsync(SagaStoreHarness harness, int count)
    {

        string[] ids = [.. Enumerable.Range(0, count).Select(static _ => Guid.NewGuid().ToString("D"))];

        for (int index = 0; index < ids.Length; index++)
        {

            _ = await harness.Store.InsertAsync(
                ids[index],
                $"Ward-stone {index} stands in the mill yard.",
                CreatedAt.AddMinutes(index),
                sessionId: null,
                tags: null,
                source: "extraction",
                harness.Embedding(index),
                Token);

        }

        return ids;

    }

    /// <summary>
    /// Makes the label table unreadable on the harness's connection by shadowing it with a temporary
    /// table of the same name that has none of its columns.
    /// </summary>
    private static async Task UnreadableLabelTableAsync(SagaStoreHarness harness)
    {

        await using SqliteCommand command = ((SqliteConnection)harness.Connection).CreateCommand();

        command.CommandText = "CREATE TEMP TABLE artifact_sensitivity (Unreadable INTEGER);";

        _ = await command.ExecuteNonQueryAsync(Token);

    }

}
