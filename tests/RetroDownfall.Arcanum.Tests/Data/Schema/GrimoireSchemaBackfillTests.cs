using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// A backfill is bounded, checkpointed inside its own batch's transaction, idempotent, restart-safe,
/// and incapable of advancing past uncommitted work.
/// </summary>
public sealed class GrimoireSchemaBackfillTests
{

    static GrimoireSchemaBackfillTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task A_backfill_advances_in_bounded_batches_and_completes()
    {

        // Five source rows, two per batch: two full batches and a short third that drains it.
        await using BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 5, maxRowsPerBatch: 2);

        GrimoireSchemaBackfillProgress progress = await harness.AdvanceAsync(maxBatches: 16);

        Assert.True(progress.StepComplete);

        Assert.Equal(3, harness.Backfill.BatchesRun);

        Assert.Equal(5, await harness.TargetRowCountAsync());

    }

    [Fact]
    public async Task A_drained_final_step_records_the_head_version_and_closes_the_run()
    {

        await using BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 3, maxRowsPerBatch: 2);

        _ = await harness.AdvanceAsync(maxBatches: 16);

        Assert.Equal(2, await harness.RecordedVersionAsync());

        Assert.Null(await harness.ReadJournalAsync());

    }

    [Fact]
    public async Task A_pass_runs_no_more_than_its_batch_bound()
    {

        await using BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 100, maxRowsPerBatch: 2);

        GrimoireSchemaBackfillProgress progress = await harness.AdvanceAsync(maxBatches: 3);

        Assert.False(progress.StepComplete);

        Assert.Equal(3, progress.BatchesRun);

        Assert.Equal(6, await harness.TargetRowCountAsync());

        // The run is still open, so a later pass picks it up rather than the version being recorded.
        Assert.Equal(1, await harness.RecordedVersionAsync());

    }

    /// <summary>
    /// The property the whole design exists for: the cursor is written inside the batch's own
    /// transaction, so it can never describe work that did not commit.
    /// </summary>
    [Fact]
    public async Task A_failing_batch_leaves_the_cursor_at_the_last_committed_batch()
    {

        await using BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 6, maxRowsPerBatch: 2);

        harness.Backfill.ThrowOnBatch = 2;

        _ = await Assert.ThrowsAnyAsync<Exception>(() => harness.AdvanceAsync(maxBatches: 16));

        // Batch one committed two rows and the cursor that describes them. Batch two committed
        // nothing, so neither its rows nor its cursor survive.
        Assert.Equal(2, await harness.TargetRowCountAsync());

        GrimoireSchemaTransitionJournalRow? journal = await harness.ReadJournalAsync();

        Assert.NotNull(journal);

        Assert.Equal("2", journal.BackfillCursor);

        Assert.Equal(2, journal.BackfillRowsProcessed);

        harness.Backfill.ThrowOnBatch = null;

        GrimoireSchemaBackfillProgress resumed = await harness.AdvanceAsync(maxBatches: 16);

        Assert.True(resumed.StepComplete);

        Assert.Equal(6, await harness.TargetRowCountAsync());

    }

    /// <summary>
    /// A batch's work and the cursor that describes it commit together or not at all.
    /// </summary>
    /// <remarks>
    /// This is the case a mid-batch failure cannot see. A sweep that throws before writing anything
    /// rolls back the same way whether the cursor shares its transaction or gets one of its own, so a
    /// test built on that failure passes over an implementation that commits the work first and
    /// records it second. The failure that separates them has to land <i>between</i> the two: another
    /// writer moves the journal on, so the cursor write loses its compare-and-swap after the batch's
    /// rows are already in hand. Sharing one transaction discards both; splitting them leaves rows
    /// nothing has a cursor for.
    /// </remarks>
    [Fact]
    public async Task A_batch_whose_cursor_write_loses_its_race_commits_no_rows()
    {

        await using BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 6, maxRowsPerBatch: 2);

        await harness.MoveJournalOnAsync();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.AdvanceFromStaleJournalAsync(maxBatches: 16));

        Assert.Equal(0, await harness.TargetRowCountAsync());

    }

    /// <summary>
    /// A sweep that ignores its own bound is a sweep that can hold one transaction open over an
    /// unbounded corpus, which is exactly the migration this design refuses to be.
    /// </summary>
    [Fact]
    public async Task A_batch_that_breaks_its_own_row_bound_is_refused()
    {

        await using BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 10, maxRowsPerBatch: 2);

        harness.Backfill.OverrunToRows = 5;

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AdvanceAsync(maxBatches: 16));

    }

    /// <summary>
    /// An installation whose sweep has drained is at head and stays there: a later pass finds no run.
    /// </summary>
    [Fact]
    public async Task Converging_after_a_completed_run_is_an_ordinary_healthy_install()
    {

        await using BackfillHarness harness = await BackfillHarness.StartAsync(sourceRows: 3, maxRowsPerBatch: 2);

        _ = await harness.AdvanceAsync(maxBatches: 16);

        GrimoireSchemaInstallResult converged = await harness.ReinstallAsync();

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, converged.Core.Health);

        Assert.Equal(2, converged.Core.SchemaVersion);

    }

    /// <summary>
    /// One installation left mid-sweep, plus everything a test needs to drive and read it.
    /// </summary>
    private sealed class BackfillHarness : IAsyncDisposable
    {

        private readonly EvolutionScratchDatabase _file;

        private readonly SqliteConnection _connection;

        private GrimoireSchemaTransitionJournalRow? _staleJournal;

        private BackfillHarness(
            EvolutionScratchDatabase file,
            SqliteConnection connection,
            TestBackfill backfill,
            GrimoireSchemaVersionChainSet chains)
        {

            _file = file;

            _connection = connection;

            Backfill = backfill;

            Chains = chains;

        }

        internal TestBackfill Backfill { get; }

        internal GrimoireSchemaVersionChainSet Chains { get; }

        /// <summary>
        /// Installs version 1, seeds the corpus, then converges onto a version-2 chain whose step
        /// depends on a sweep - which is the state a real installation is left in.
        /// </summary>
        internal static async Task<BackfillHarness> StartAsync(int sourceRows, int maxRowsPerBatch)
        {

            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                GrimoireSchemaEvolutionFixture.OneVersionChainSet(),
                1536,
                CancellationToken.None);

            for (int id = 1; id <= sourceRows; id++)
            {

                await using SqliteCommand seed = connection.CreateCommand();

                seed.CommandText = "INSERT INTO evolution_source (Id, Value) VALUES ($id, $value);";

                _ = seed.Parameters.AddWithValue("$id", id);

                _ = seed.Parameters.AddWithValue("$value", $"value-{id}");

                _ = await seed.ExecuteNonQueryAsync(CancellationToken.None);

            }

            TestBackfill backfill = new("fill-target", maxRowsPerBatch);

            GrimoireSchemaVersionChainSet chains = GrimoireSchemaEvolutionFixture.TwoVersionChainSet(backfill);

            GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                chains,
                1536,
                CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, result.Core.Health);

            return new BackfillHarness(file, connection, backfill, chains);

        }

        internal async Task<GrimoireSchemaBackfillProgress> AdvanceAsync(int maxBatches)
        {

            GrimoireSchemaTransitionJournalRow journal = await ReadJournalAsync()
                ?? throw new InvalidOperationException("The harness has no run in flight.");

            return await AdvanceAsync(journal, maxBatches);

        }

        /// <summary>
        /// Runs a pass holding the journal row as it stood <i>before</i>
        /// <see cref="MoveJournalOnAsync"/>, which is what a driver that read the row and then lost
        /// the race holds.
        /// </summary>
        internal Task<GrimoireSchemaBackfillProgress> AdvanceFromStaleJournalAsync(int maxBatches) =>
            AdvanceAsync(
                _staleJournal ?? throw new InvalidOperationException("Nothing has moved the journal on yet."),
                maxBatches);

        /// <summary>
        /// Plays the other writer: advances the journal by one revision without changing what it
        /// says, so a pass still holding the previous revision loses its compare-and-swap.
        /// </summary>
        internal async Task MoveJournalOnAsync()
        {

            GrimoireSchemaTransitionJournalRow journal = await ReadJournalAsync()
                ?? throw new InvalidOperationException("The harness has no run in flight.");

            _staleJournal = journal;

            await using SqliteTransaction transaction =
                (SqliteTransaction)await _connection.BeginTransactionAsync(CancellationToken.None);

            Assert.True(
                await GrimoireSchemaTransitionJournal.AdvanceAsync(
                    _connection,
                    transaction,
                    journal,
                    journal.CompletedThroughVersion,
                    journal.BackfillName,
                    journal.BackfillCursor,
                    journal.BackfillRowsProcessed,
                    new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero),
                    CancellationToken.None));

            await transaction.CommitAsync(CancellationToken.None);

        }

        private Task<GrimoireSchemaBackfillProgress> AdvanceAsync(
            GrimoireSchemaTransitionJournalRow journal,
            int maxBatches)
        {

            GrimoireSchemaBackfillRunner runner = new(
                GrimoireSchemaTestInstaller.Create(Chains),
                TimeProvider.System);

            return runner.AdvanceAsync(
                _connection,
                Chains.ForTier(GrimoireSchemaTransactionTier.Core),
                journal,
                GrimoireSchemaTestInstaller.CreateContext(),
                maxBatches,
                CancellationToken.None);

        }

        internal Task<GrimoireSchemaInstallResult> ReinstallAsync() =>
            GrimoireSchemaTestInstaller.InstallAsync(_connection, Chains, 1536, CancellationToken.None);

        internal Task<GrimoireSchemaTransitionJournalRow?> ReadJournalAsync() =>
            GrimoireSchemaTransitionJournal.ReadAsync(
                _connection,
                transaction: null,
                GrimoireSchemaTransactionTier.Core,
                CancellationToken.None);

        internal async Task<int> TargetRowCountAsync()
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = "SELECT COUNT(*) FROM evolution_target;";

            return Convert.ToInt32(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture);

        }

        internal async Task<int> RecordedVersionAsync()
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText =
                "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE TransactionTierCode = 0;";

            return Convert.ToInt32(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture);

        }

        public async ValueTask DisposeAsync()
        {

            await _connection.DisposeAsync();

            _file.Dispose();

        }

    }

}
