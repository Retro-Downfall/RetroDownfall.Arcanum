using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The resumable base rebuild: its one entry point, its captured identity, and its terminal phases.
/// </summary>
public sealed class CovenantIndexRebuildTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void The_rebuilder_exposes_one_batch_method_and_no_whole_operation_shortcut()
    {

        System.Reflection.MethodInfo[] declared = [.. typeof(CovenantIndexRebuilder)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)];

        System.Reflection.MethodInfo only = Assert.Single(declared);

        Assert.Equal(nameof(CovenantIndexRebuilder.AdvanceBatchAsync), only.Name);

        Assert.Equal(
            [
                typeof(CovenantIndexRebuildProgress),
                typeof(CovenantAcceleratorLease),
                typeof(CancellationToken),
            ],
            only.GetParameters().Select(static parameter => parameter.ParameterType));

    }

    [Fact]
    public void Rebuild_phase_codes_are_immutable()
    {

        Assert.Equal((byte)1, (byte)CovenantIndexRebuildPhase.BaseScan);

        Assert.Equal((byte)2, (byte)CovenantIndexRebuildPhase.DeltaCatchUp);

        Assert.Equal((byte)3, (byte)CovenantIndexRebuildPhase.Verifying);

        Assert.Equal((byte)4, (byte)CovenantIndexRebuildPhase.Completed);

        Assert.Equal((byte)5, (byte)CovenantIndexRebuildPhase.RestartRequired);

        Assert.Equal(5, Enum.GetValues<CovenantIndexRebuildPhase>().Length);

    }

    [Fact]
    public void Progress_invariants_reject_impossible_checkpoints()
    {

        _ = Assert.Throws<ArgumentException>(
            () => Progress(Guid.Empty, 1, 0, CovenantIndexRebuildPhase.BaseScan));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => Progress(Guid.NewGuid(), 0, 0, CovenantIndexRebuildPhase.BaseScan));

        // Zero is not a valid base-scan cursor: row IDs start at one, so zero would be
        // indistinguishable from "no rows committed yet".
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => Progress(Guid.NewGuid(), 1, 0, CovenantIndexRebuildPhase.BaseScan) with
            {

                BaseScanAfterSearchRowId = 0,

            });

    }

    [Fact]
    public async Task A_start_captures_its_identity_and_clears_the_old_projection()
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

        _ = await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        Assert.Equal(1, await Count(fixture, "covenant_search_documents"));

        CovenantIndexRebuildProgress started = await AdvanceAsync(fixture, null);

        Assert.Equal(CovenantIndexRebuildPhase.BaseScan, started.Phase);

        Assert.Equal(await fixture.ReadDatasetGenerationAsync(Token), started.DatasetGeneration);

        Assert.Null(started.BaseScanAfterSearchRowId);

        Assert.Equal(0, started.BaseHeadsProcessed);

        Assert.Equal(1, started.BaseHeadsTotal);

        Assert.Equal(started.BaseTargetSearchSequence, started.LastContiguousAppliedSequence);

        // The stale projection is gone and the applied tuple is null, so nothing partial is eligible.
        Assert.Equal(0, await Count(fixture, "covenant_search_documents"));

        Assert.Equal(0, await Scalar(fixture, "SELECT COUNT(AppliedSearchSequence) FROM covenant_state;"));

    }

    [Fact]
    public async Task A_rebuild_runs_to_completion_and_publishes_eligibility_once()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        for (int index = 0; index < 3; index++)
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

        CovenantIndexRebuildProgress progress = await AdvanceAsync(fixture, null);

        int guard = 0;

        while (!progress.IsTerminal && guard++ < 32)
        {

            Assert.Equal(
                0,
                await Scalar(fixture, "SELECT COUNT(AppliedSearchSequence) FROM covenant_state;"));

            progress = await AdvanceAsync(fixture, progress);

        }

        Assert.Equal(CovenantIndexRebuildPhase.Completed, progress.Phase);

        Assert.Equal(3, progress.BaseHeadsProcessed);

        Assert.Equal(3, await Count(fixture, "covenant_search_documents"));

        Assert.Equal(
            await Scalar(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"),
            await Scalar(fixture, "SELECT AppliedSearchSequence FROM covenant_state;"));

        Assert.Equal(1, await Scalar(fixture, "SELECT RebuildStateCode FROM covenant_state;"));

        // Completion is idempotent.
        Assert.Equal(progress, await AdvanceAsync(fixture, progress));

    }

    [Fact]
    public async Task A_mutation_during_the_base_scan_is_recovered_from_the_post_target_outbox()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        SeededHead head = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.moving",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Original marker.",
            Token);

        CovenantIndexRebuildProgress progress = await AdvanceAsync(fixture, null);

        progress = await AdvanceAsync(fixture, progress);

        Assert.Equal(1, progress.BaseHeadsProcessed);

        // A write to an already-passed key, after the captured base target.
        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.moving",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Replacement marker.",
            Token,
            entryId: head.EntryId,
            laneRevision: 2,
            predecessorVersionId: head.VersionId);

        int guard = 0;

        while (!progress.IsTerminal && guard++ < 32)
        {

            progress = await AdvanceAsync(fixture, progress);

        }

        Assert.Equal(CovenantIndexRebuildPhase.Completed, progress.Phase);

        Assert.True(progress.DeltaRowsProcessed > 0);

        Assert.Equal(
            "Replacement marker.",
            await StringAsync(fixture, "SELECT AuthoredContent FROM covenant_search_documents;"));

    }

    [Fact]
    public async Task A_changed_dataset_generation_restarts_rather_than_publishing()
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

        CovenantIndexRebuildProgress progress = await AdvanceAsync(fixture, null);

        CovenantIndexRebuildProgress stale = progress with { DatasetGeneration = Guid.NewGuid() };

        CovenantIndexRebuildProgress restarted = await AdvanceAsync(fixture, stale, useStaleLease: true);

        Assert.Equal(CovenantIndexRebuildPhase.RestartRequired, restarted.Phase);

        // The stale captured identity travels with the terminal checkpoint.
        Assert.Equal(stale.DatasetGeneration, restarted.DatasetGeneration);

        Assert.True(restarted.IsTerminal);

        Assert.Equal(restarted, await AdvanceAsync(fixture, restarted, useStaleLease: true));

    }

    [Fact]
    public async Task A_gap_in_the_post_target_outbox_restarts()
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

        CovenantIndexRebuildProgress progress = await AdvanceAsync(fixture, null);

        // Canonical advances but its deltas are gone, which is what an overflowed outbox looks like.
        await ExecuteAsync(
            fixture,
            "UPDATE covenant_state SET CanonicalSearchSequence = CanonicalSearchSequence + 2 WHERE StateKey = 1;");

        int guard = 0;

        while (!progress.IsTerminal && guard++ < 32)
        {

            progress = await AdvanceAsync(fixture, progress);

        }

        Assert.Equal(CovenantIndexRebuildPhase.RestartRequired, progress.Phase);

        Assert.Equal(0, await Scalar(fixture, "SELECT COUNT(AppliedSearchSequence) FROM covenant_state;"));

    }

    [Fact]
    public async Task A_revoked_accelerator_lease_advances_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token));

        CovenantAcceleratorLease lease = (await gate.AcquireAcceleratorAsync(Token)).Value;

        await lease.DisposeAsync();

        Result<CovenantIndexRebuildProgress> refused = await new CovenantIndexRebuilder(
                new FixedCovenantConnectionSource(fixture.Connection))
            .AdvanceBatchAsync(null, lease, Token);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    private static CovenantIndexRebuildProgress Progress(
        Guid generation,
        ulong epoch,
        long target,
        CovenantIndexRebuildPhase phase) =>
        new(generation, epoch, target, 0, phase, null, target, 0, null, 0);

    private static async Task<CovenantIndexRebuildProgress> AdvanceAsync(
        CovenantCanonicalFixture fixture,
        CovenantIndexRebuildProgress? progress,
        bool useStaleLease = false)
    {

        FakeCovenantAvailability availability = await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        await using CovenantAcceleratorLease lease = (await gate.AcquireAcceleratorAsync(Token)).Value;

        Result<CovenantIndexRebuildProgress> advanced = await new CovenantIndexRebuilder(
                new FixedCovenantConnectionSource(fixture.Connection))
            .AdvanceBatchAsync(progress, lease, Token);

        Assert.True(advanced.IsSuccess, advanced.IsFailure ? advanced.Error.Message : null);

        _ = useStaleLease;

        return advanced.Value;

    }

    private static Task<long> Count(CovenantCanonicalFixture fixture, string table) =>
        CovenantCapacityFixture.ScalarAsync(fixture, $"SELECT COUNT(*) FROM {table};", Token);

    private static Task<long> Scalar(CovenantCanonicalFixture fixture, string sql) =>
        CovenantCapacityFixture.ScalarAsync(fixture, sql, Token);

    private static async Task<string?> StringAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using Microsoft.Data.Sqlite.SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(Token);

        return value is null or DBNull ? null : Convert.ToString(value);

    }

    private static async Task ExecuteAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using Microsoft.Data.Sqlite.SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);

    }

}
