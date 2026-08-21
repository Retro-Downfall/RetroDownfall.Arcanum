using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

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

        CovenantAvailabilitySnapshot before = availability.Current;

        bool published = await CovenantPersistedAvailabilityPublisher.PublishAsync(
            availability,
            _database.Connection,
            acceleratorHealthy: false,
            CovenantHealthTransition.Bootstrap,
            CancellationToken.None);

        Assert.True(published);

        CovenantAvailabilitySnapshot snapshot = availability.Current;

        Assert.Equal(before.Generation + 1, snapshot.Generation);

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

    [Fact]
    public async Task Persisted_state_publication_exposes_one_complete_predecessor_then_one_complete_successor()
    {

        using BlockingCovenantRuntimePublicationCheckpoint checkpoint = new(
            CovenantRuntimePublicationStep.AvailabilityBeforeSwap,
            CovenantRuntimePublicationStep.AvailabilityAfterSwap);

        using CovenantRuntimeGenerationProvider runtime = new(checkpoint);

        CovenantAvailability availability = new(runtime);

        Guid datasetGeneration = await PersistedDatasetGenerationAsync();

        CovenantAvailabilitySnapshot predecessor = availability.PublishPersistedState(
            datasetGeneration,
            canonicalSequence: 3,
            coreCampaignDeletionSequence: 2,
            appliedDatasetGeneration: datasetGeneration,
            appliedSequence: 3,
            appliedCampaignDeletionSequence: 2,
            acceleratorEpoch: 4,
            CovenantFtsSynchronizationState.Synchronized,
            rebuildRequired: false,
            CovenantHealthTransition.Bootstrap);

        checkpoint.Arm();

        Task<CovenantAvailabilitySnapshot> publishing = Task.Run(() =>
        {

            return availability.PublishPersistedState(
                datasetGeneration,
                canonicalSequence: 8,
                coreCampaignDeletionSequence: 5,
                appliedDatasetGeneration: datasetGeneration,
                appliedSequence: 7,
                appliedCampaignDeletionSequence: 4,
                acceleratorEpoch: 9,
                CovenantFtsSynchronizationState.Dirty,
                rebuildRequired: true,
                CovenantHealthTransition.AcceleratorSynchronization);

        });

        checkpoint.WaitForBeforeSwap();

        Assert.False(publishing.IsCompleted);

        Assert.Same(predecessor, availability.Current);

        Assert.Equal(datasetGeneration, availability.Current.DatasetGeneration);

        Assert.Equal(3, availability.Current.CanonicalSequence);

        Assert.Equal(2, availability.Current.CoreCampaignDeletionSequence);

        Assert.Equal(datasetGeneration, availability.Current.AppliedDatasetGeneration);

        Assert.Equal(3, availability.Current.AppliedSequence);

        Assert.Equal(2, availability.Current.AppliedCampaignDeletionSequence);

        Assert.Equal(4UL, availability.Current.AcceleratorEpoch);

        Assert.Equal(CovenantFtsSynchronizationState.Synchronized, availability.Current.FtsSynchronization);

        Assert.False(availability.Current.RebuildRequired);

        checkpoint.AdvanceToAfterSwap();

        Assert.False(publishing.IsCompleted);

        CovenantAvailabilitySnapshot insideSuccessor = availability.Current;

        Assert.NotSame(predecessor, insideSuccessor);

        Assert.Equal(predecessor.Generation + 1, insideSuccessor.Generation);

        Assert.Equal(datasetGeneration, insideSuccessor.DatasetGeneration);

        Assert.Equal(8, insideSuccessor.CanonicalSequence);

        Assert.Equal(5, insideSuccessor.CoreCampaignDeletionSequence);

        Assert.Equal(datasetGeneration, insideSuccessor.AppliedDatasetGeneration);

        Assert.Equal(7, insideSuccessor.AppliedSequence);

        Assert.Equal(4, insideSuccessor.AppliedCampaignDeletionSequence);

        Assert.Equal(9UL, insideSuccessor.AcceleratorEpoch);

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, insideSuccessor.FtsSynchronization);

        Assert.True(insideSuccessor.RebuildRequired);

        checkpoint.ReleaseAfterSwap();

        CovenantAvailabilitySnapshot successor = await publishing;

        checkpoint.AssertNoFailure();

        Assert.Same(insideSuccessor, successor);

        Assert.Same(successor, availability.Current);

        Assert.Equal(predecessor.Generation + 1, successor.Generation);

        Assert.Equal(datasetGeneration, successor.DatasetGeneration);

        Assert.Equal(8, successor.CanonicalSequence);

        Assert.Equal(5, successor.CoreCampaignDeletionSequence);

        Assert.Equal(datasetGeneration, successor.AppliedDatasetGeneration);

        Assert.Equal(7, successor.AppliedSequence);

        Assert.Equal(4, successor.AppliedCampaignDeletionSequence);

        Assert.Equal(9UL, successor.AcceleratorEpoch);

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, successor.FtsSynchronization);

        Assert.True(successor.RebuildRequired);

        Assert.Equal(
            CovenantHealthTransition.AcceleratorSynchronization,
            successor.LastHealthTransition);

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
