using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The driver that finishes what a bootstrap started: it drains a pending sweep in bounded passes and
/// re-enters convergence, so a multi-step chain needs no restart per step.
/// </summary>
public sealed class GrimoireSchemaTransitionCoordinatorTests
{

    static GrimoireSchemaTransitionCoordinatorTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task A_pass_drains_a_pending_sweep_and_finishes_the_run()
    {

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(sourceRows: 5);

        Result<GrimoireSchemaTransitionPassOutcome> outcome =
            await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(outcome.IsSuccess);

        Assert.True(outcome.Value.Advanced);

        Assert.Equal(2, await harness.RecordedVersionAsync());

        Assert.Null(await harness.ReadJournalAsync());

        Assert.Equal(5, await harness.TargetRowCountAsync());

    }

    [Fact]
    public async Task A_pass_over_an_installation_with_no_run_reports_nothing_to_do()
    {

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(sourceRows: 3);

        _ = await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Result<GrimoireSchemaTransitionPassOutcome> second =
            await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(second.IsSuccess);

        Assert.False(second.Value.Advanced);

    }

    /// <summary>
    /// The driver is gated on the journal, never on availability.
    /// </summary>
    /// <remarks>
    /// Gating on health would deadlock in both directions: a Covenant tier mid-run is unavailable by
    /// design, and a Core tier mid-run stands its dependents down, so a driver that waited for a
    /// healthy tier could never run the very sweep that restores one.
    /// </remarks>
    [Fact]
    public async Task A_pass_runs_while_every_covenant_tier_is_unavailable()
    {

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(sourceRows: 4);

        Assert.NotEqual(
            CovenantCapabilityState.Healthy,
            harness.Availability.Current.Canonical);

        Result<GrimoireSchemaTransitionPassOutcome> outcome =
            await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(outcome.IsSuccess);

        Assert.True(outcome.Value.Advanced);

        Assert.Equal(2, await harness.RecordedVersionAsync());

    }

    /// <summary>
    /// A corpus larger than one pass can drain takes more passes, and every pass in between leaves the
    /// version unrecorded.
    /// </summary>
    [Fact]
    public async Task A_run_larger_than_one_pass_finishes_across_passes()
    {

        await using CoordinatorHarness harness = await CoordinatorHarness.StartAsync(
            sourceRows: (GrimoireSchemaTransitionCoordinator.MaxBatchesPerPass * 2) + 4,
            maxRowsPerBatch: 1);

        Result<GrimoireSchemaTransitionPassOutcome> first =
            await harness.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(first.Value.Advanced);

        Assert.Equal(1, await harness.RecordedVersionAsync());

        Assert.NotNull(await harness.ReadJournalAsync());

        while ((await harness.Coordinator.RunOnceAsync(CancellationToken.None)).Value.Advanced
            && await harness.ReadJournalAsync() is not null)
        {

        }

        Assert.Equal(2, await harness.RecordedVersionAsync());

    }

    private sealed class CoordinatorHarness : IAsyncDisposable
    {

        private readonly EvolutionScratchDatabase _file;

        private readonly SqliteConnection _connection;

        private readonly ServiceProvider _services;

        private CoordinatorHarness(
            EvolutionScratchDatabase file,
            SqliteConnection connection,
            ServiceProvider services,
            CovenantAvailability availability,
            GrimoireSchemaTransitionCoordinator coordinator)
        {

            _file = file;

            _connection = connection;

            _services = services;

            Availability = availability;

            Coordinator = coordinator;

        }

        internal CovenantAvailability Availability { get; }

        internal GrimoireSchemaTransitionCoordinator Coordinator { get; }

        internal static async Task<CoordinatorHarness> StartAsync(int sourceRows, int maxRowsPerBatch = 2)
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

            GrimoireSchemaVersionChainSet chains = GrimoireSchemaEvolutionFixture.TwoVersionChainSet(
                new TestBackfill("fill-target", maxRowsPerBatch));

            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                chains,
                1536,
                CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, installed.Core.Health);

            ServiceCollection collection = new();

            _ = collection.AddOptions();

            _ = collection.Configure<ArcanumSettings>(static _ => { });

            ServiceProvider services = collection.BuildServiceProvider();

            CovenantRuntimeGenerationProvider runtime = new();

            CovenantAvailability availability = new(runtime);

            _ = availability.PublishSchema(installed, CovenantHealthTransition.Bootstrap);

            GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create(chains);

            GrimoireSchemaTransitionCoordinator coordinator = new(
                new FixedCoreConnectionSource(connection),
                chains,
                installer,
                new GrimoireSchemaBackfillRunner(installer, TimeProvider.System),
                services,
                availability,
                TimeProvider.System);

            return new CoordinatorHarness(file, connection, services, availability, coordinator);

        }

        internal Task<GrimoireSchemaTransitionJournalRow?> ReadJournalAsync() =>
            GrimoireSchemaTransitionJournal.ReadAsync(
                _connection,
                transaction: null,
                GrimoireSchemaTransactionTier.Core,
                CancellationToken.None);

        internal async Task<int> RecordedVersionAsync()
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText =
                "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE TransactionTierCode = 0;";

            return Convert.ToInt32(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture);

        }

        internal async Task<int> TargetRowCountAsync()
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = "SELECT COUNT(*) FROM evolution_target;";

            return Convert.ToInt32(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture);

        }

        public async ValueTask DisposeAsync()
        {

            await _services.DisposeAsync();

            await _connection.DisposeAsync();

            _file.Dispose();

        }

    }

    /// <summary>
    /// The one open connection the harness has, which is what a request scope would otherwise supply.
    /// </summary>
    private sealed class FixedCoreConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);

    }

}
