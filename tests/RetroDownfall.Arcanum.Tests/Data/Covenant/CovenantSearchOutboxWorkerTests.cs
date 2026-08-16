using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Contiguity, coalescing, and rollback behaviour of the single-writer accelerator worker.
/// </summary>
public sealed class CovenantSearchOutboxWorkerTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task An_empty_outbox_adopts_the_current_dataset_without_projecting_anything()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        CovenantOutboxSyncOutcome outcome = await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        Assert.False(outcome.RebuildRequired);

        Assert.Equal(0, outcome.ProjectionsWritten);

        Assert.Equal(
            await fixture.ReadDatasetGenerationAsync(Token),
            new Guid((byte[])(await ReadAsync(fixture, "SELECT AppliedDatasetGeneration FROM covenant_state;"))!));

    }

    [Fact]
    public async Task A_contiguous_range_projects_every_head_and_consumes_its_rows()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        for (int index = 0; index < 4; index++)
        {

            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.key{index}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Body {index}.",
                Token);

        }

        CovenantOutboxSyncOutcome outcome = await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        Assert.Equal(4, outcome.ProjectionsWritten);

        Assert.Equal(4, outcome.RowsConsumed);

        Assert.Equal(0, await Count(fixture, "covenant_search_outbox"));

        Assert.Equal(4, await Count(fixture, "covenant_search_documents"));

        Assert.Equal(
            await Scalar(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"),
            await Scalar(fixture, "SELECT AppliedSearchSequence FROM covenant_state;"));

    }

    [Fact]
    public async Task A_bounded_batch_stops_on_a_sequence_boundary_and_resumes()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        for (int index = 0; index < 5; index++)
        {

            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.key{index}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Body {index}.",
                Token);

        }

        // Two rows per pass: each seeded head is its own sequence, so the range stops at a boundary.
        CovenantOutboxSyncOutcome first = await CovenantSearchFixture.SynchronizeAsync(fixture, Token, maxRows: 2);

        Assert.Equal(2, first.AppliedSearchSequence);

        Assert.Equal(2, first.RowsConsumed);

        Assert.False(first.RebuildRequired);

        CovenantOutboxSyncOutcome second = await CovenantSearchFixture.SynchronizeAsync(fixture, Token, maxRows: 2);

        Assert.Equal(4, second.AppliedSearchSequence);

        CovenantOutboxSyncOutcome third = await CovenantSearchFixture.SynchronizeAsync(fixture, Token, maxRows: 2);

        Assert.Equal(5, third.AppliedSearchSequence);

        Assert.Equal(5, await Count(fixture, "covenant_search_documents"));

    }

    [Fact]
    public async Task Repeated_deltas_for_one_head_coalesce_to_one_write()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        SeededHead head = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.churn",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "First.",
            Token);

        SeededHead second = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.churn",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Second.",
            Token,
            entryId: head.EntryId,
            laneRevision: 2,
            predecessorVersionId: head.VersionId);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.churn",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Third.",
            Token,
            entryId: head.EntryId,
            laneRevision: 3,
            predecessorVersionId: second.VersionId);

        CovenantOutboxSyncOutcome outcome = await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        // Three deltas, one projection row, one write.
        Assert.Equal(1, outcome.ProjectionsWritten);

        Assert.Equal(3, outcome.RowsConsumed);

        Assert.Equal(1, await Count(fixture, "covenant_search_documents"));

    }

    [Fact]
    public async Task A_deletion_delta_removes_its_projection_row()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        SeededHead head = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.gone",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body.",
            Token);

        _ = await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        Assert.Equal(1, await Count(fixture, "covenant_search_documents"));

        await ExecuteAsync(
            fixture,
            $"""
             UPDATE covenant_state SET CanonicalSearchSequence = CanonicalSearchSequence + 1 WHERE StateKey = 1;

             INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
             SELECT CanonicalSearchSequence, 0, {head.SearchRowId}, '{head.EntryId:D}', 1, NULL
             FROM covenant_state WHERE StateKey = 1;
             """);

        CovenantOutboxSyncOutcome outcome = await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        Assert.Equal(1, outcome.ProjectionsRemoved);

        Assert.Equal(0, await Count(fixture, "covenant_search_documents"));

        Assert.Equal(0, await Count(fixture, "covenant_fts"));

    }

    [Fact]
    public async Task A_missing_desired_version_forces_a_rebuild_and_advances_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.orphan",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body.",
            Token);

        // A delta naming a version that no owner-journal cleanup left behind.
        await ExecuteAsync(
            fixture,
            $"""
             UPDATE covenant_search_outbox SET DesiredVersionId = '{Guid.NewGuid():D}';
             """);

        CovenantOutboxSyncOutcome outcome = await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        Assert.True(outcome.RebuildRequired);

        Assert.Equal(0, outcome.RowsConsumed);

        Assert.Equal(1, await Count(fixture, "covenant_search_outbox"));

    }

    [Fact]
    public async Task A_changed_accelerator_epoch_refuses_the_batch()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body.",
            Token);

        FakeCovenantAvailability availability = await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token);

        availability.Mutate(current => current with { AcceleratorEpoch = current.AcceleratorEpoch + 10 });

        Result<CovenantOutboxSyncOutcome> refused = await CovenantSearchFixture.TrySynchronizeAsync(
            fixture,
            Token,
            availability: availability);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

        Assert.Equal(1, await Count(fixture, "covenant_search_outbox"));

    }

    [Fact]
    public async Task A_canonical_mutation_succeeds_even_when_the_accelerator_is_absent()
    {

        // No accelerator tier at all: the canonical mutation still commits and still queues its delta.
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.key",
                    "Body.",
                    0,
                    0)),
            Token);

        Assert.True(applied.IsSuccess);

        Assert.Equal(1, await Count(fixture, "covenant_search_outbox"));

    }

    private static async Task<long> Count(CovenantCanonicalFixture fixture, string table) =>
        await CovenantCapacityFixture.ScalarAsync(fixture, $"SELECT COUNT(*) FROM {table};", Token);

    private static Task<long> Scalar(CovenantCanonicalFixture fixture, string sql) =>
        CovenantCapacityFixture.ScalarAsync(fixture, sql, Token);

    private static async Task<object?> ReadAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using Microsoft.Data.Sqlite.SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(Token);

        return value is DBNull ? null : value;

    }

    private static async Task ExecuteAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using Microsoft.Data.Sqlite.SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);

    }

}
