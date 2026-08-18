using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The step that carries persisted canonical facts into the process-wide availability snapshot.
/// </summary>
/// <remarks>
/// <para><c>PublishSchema</c> only reports which tiers installed. The dataset generation, the
/// canonical and core deletion sequences, and the accelerator's applied tuple all live in
/// <c>covenant_state</c>, and nothing carried them into the snapshot — so
/// <c>CovenantAvailabilitySnapshot.DatasetGeneration</c> stayed at its bootstrap default of
/// <see langword="null"/> for the whole process lifetime.</para>
/// <para>That is not a stale-diagnostics problem. <c>CovenantOperationGate.CaptureFacts</c> refuses
/// every <c>requireCanonical: true</c> acquisition when the dataset generation is null, and
/// <c>AcquireOrdinary</c> — the ordinary lease every Covenant turn takes — always passes
/// <c>requireCanonical: true</c>. Without this publication Covenant fails closed on its own hot path
/// the instant the feature flag is enabled, and every dataset-generation and accelerator-epoch
/// staleness guard downstream is inert because the values it compares never change.</para>
/// </remarks>
public sealed class CovenantPersistedAvailabilityPublisherTests : IAsyncLifetime
{

    private CovenantSchemaScratchDatabase _database = null!;

    public async Task InitializeAsync()
    {

        _database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _database.InstallCanonicalAsync(CancellationToken.None);

    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task A_freshly_installed_canonical_tier_publishes_the_dataset_generation_it_persisted()
    {

        CovenantAvailability availability = new();

        bool published = await CovenantPersistedAvailabilityPublisher.PublishAsync(
            availability,
            _database.Connection,
            acceleratorHealthy: false,
            CovenantHealthTransition.Bootstrap,
            CancellationToken.None);

        Assert.True(published);

        CovenantAvailabilitySnapshot snapshot = availability.Current;

        // The identity every turn snapshot binds, and the one value whose absence closes the gate.
        Assert.NotNull(snapshot.DatasetGeneration);

        Assert.Equal(await PersistedDatasetGenerationAsync(), snapshot.DatasetGeneration);

        Assert.Equal(0L, snapshot.CanonicalSequence);

        // A never-built accelerator is behind by definition, so the seed says a rebuild is owed.
        Assert.True(snapshot.RebuildRequired);

        Assert.Null(snapshot.AppliedDatasetGeneration);

        Assert.Null(snapshot.AppliedSequence);

        Assert.Equal(CovenantHealthTransition.Bootstrap, snapshot.LastHealthTransition);

    }

    [Fact]
    public async Task An_accelerator_caught_up_to_canonical_truth_publishes_as_synchronized()
    {

        Guid generation = await PersistedDatasetGenerationAsync();

        await _database.ExecuteAsync(
            $"""
             UPDATE covenant_state
             SET CanonicalSearchSequence = 7,
                 AppliedDatasetGeneration = X'{Convert.ToHexString(generation.ToByteArray())}',
                 AppliedSearchSequence = 7,
                 AppliedCampaignDeletionSequence = 0,
                 AcceleratorEpoch = 4,
                 RebuildStateCode = 1
             WHERE StateKey = 1;
             """,
            CancellationToken.None);

        CovenantAvailability availability = new();

        Assert.True(await CovenantPersistedAvailabilityPublisher.PublishAsync(
            availability,
            _database.Connection,
            acceleratorHealthy: true,
            CovenantHealthTransition.Restore,
            CancellationToken.None));

        CovenantAvailabilitySnapshot snapshot = availability.Current;

        Assert.Equal(generation, snapshot.DatasetGeneration);

        Assert.Equal(7L, snapshot.CanonicalSequence);

        Assert.Equal(generation, snapshot.AppliedDatasetGeneration);

        Assert.Equal(7L, snapshot.AppliedSequence);

        Assert.Equal(4UL, snapshot.AcceleratorEpoch);

        Assert.Equal(CovenantFtsSynchronizationState.Synchronized, snapshot.FtsSynchronization);

        Assert.False(snapshot.RebuildRequired);

    }

    /// <summary>
    /// An applied tuple that trails canonical truth is Dirty, never Synchronized.
    /// </summary>
    /// <remarks>
    /// This is the same comparison <c>CovenantSearchSourceSnapshot.AcceleratorEligible</c> makes, and
    /// it has to be made here too: publishing Synchronized on a trailing tuple would let the
    /// accelerator answer queries from a projection that is missing committed mutations.
    /// </remarks>
    [Fact]
    public async Task An_accelerator_behind_canonical_truth_publishes_as_dirty()
    {

        Guid generation = await PersistedDatasetGenerationAsync();

        await _database.ExecuteAsync(
            $"""
             UPDATE covenant_state
             SET CanonicalSearchSequence = 9,
                 AppliedDatasetGeneration = X'{Convert.ToHexString(generation.ToByteArray())}',
                 AppliedSearchSequence = 5,
                 RebuildStateCode = 1
             WHERE StateKey = 1;
             """,
            CancellationToken.None);

        CovenantAvailability availability = new();

        Assert.True(await CovenantPersistedAvailabilityPublisher.PublishAsync(
            availability,
            _database.Connection,
            acceleratorHealthy: true,
            CovenantHealthTransition.AcceleratorSynchronization,
            CancellationToken.None));

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, availability.Current.FtsSynchronization);

    }

    /// <summary>
    /// A degraded accelerator tier stays Unavailable however current its persisted tuple looks.
    /// </summary>
    [Fact]
    public async Task An_unhealthy_accelerator_tier_never_publishes_a_synchronization_state()
    {

        CovenantAvailability availability = new();

        Assert.True(await CovenantPersistedAvailabilityPublisher.PublishAsync(
            availability,
            _database.Connection,
            acceleratorHealthy: false,
            CovenantHealthTransition.Bootstrap,
            CancellationToken.None));

        Assert.Equal(
            CovenantFtsSynchronizationState.Unavailable,
            availability.Current.FtsSynchronization);

    }

    /// <summary>
    /// Without the canonical tier there is nothing to read, and nothing is published.
    /// </summary>
    /// <remarks>
    /// A failed or absent canonical install must not be reported as a healthy dataset generation:
    /// the gate's refusal is correct in that case, and inventing a generation would defeat it.
    /// </remarks>
    [Fact]
    public async Task An_absent_canonical_tier_publishes_nothing()
    {

        await using CovenantSchemaScratchDatabase bare =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        CovenantAvailability availability = new();

        long before = availability.Current.Generation;

        Assert.False(await CovenantPersistedAvailabilityPublisher.PublishAsync(
            availability,
            bare.Connection,
            acceleratorHealthy: false,
            CovenantHealthTransition.Bootstrap,
            CancellationToken.None));

        Assert.Null(availability.Current.DatasetGeneration);

        Assert.Equal(before, availability.Current.Generation);

    }

    private async Task<Guid> PersistedDatasetGenerationAsync()
    {

        await using SqliteCommand command = _database.Connection.CreateCommand();

        command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));

        byte[] raw = new byte[16];

        _ = reader.GetBytes(0, 0, raw, 0, raw.Length);

        return new Guid(raw);

    }

}
