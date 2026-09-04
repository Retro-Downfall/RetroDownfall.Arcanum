using System.Buffers.Binary;
using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Issue #127 — the complete erasure inventory is proved before effects and replayed through bounded,
/// typed keyset pages whose owned connections are gone before a kernel can run.
/// </summary>
public sealed class CovenantErasureInventorySourceTests
{

    private const int LabelCount = 769;

    private static readonly Guid ErasureOperationId =
        new("AAAAAAAA-1111-4111-8111-111111111111");

    private static readonly Guid ExistingWorkItemId =
        new("BBBBBBBB-2222-4222-8222-222222222222");

    [Fact]
    public async Task Complete_preflight_and_replays_exhaust_769_interleaved_labels_in_four_bounded_pages()
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.SeedInterleavedLabelsAsync(LabelCount, includeExistingWorkItem: true);

        Guid dataset = await fixture.ReadDatasetGenerationAsync();

        CovenantErasureInventorySource source = fixture.CreateSource();

        (CovenantClosedPeriodTestAuthority period, InventoryFixture.CountingMaintenanceFactory opens) =
            await fixture.ClosedPeriodAsync();

        await using CovenantClosedPeriodTestAuthority closedPeriod = period;

        Result<CovenantErasureInventorySummary> preflight = await source.PreflightBeforeCanonicalAsync(
            CovenantExclusiveOperation.CovenantReset,
            dataset,
            period.Authority,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.IsFailure ? preflight.Error.Message : null);

        Assert.Equal(385, preflight.Value.DatabaseArtifactCount);

        Assert.Equal(384, preflight.Value.ManagedFileArtifactCount);

        Assert.Equal(new CovenantDisclosureExposure(0, CovenantDisclosureCountKind.Exact), preflight.Value.Exposure);

        Assert.Equal(0, fixture.Drain.ActiveCount);

        List<Guid> databaseLabels = [];

        Guid? databaseCursor = null;

        int databaseCalls = 0;

        do
        {

            Result<CovenantDatabaseErasureBatch> batch = await source.ReadNextDatabaseBatchAsync(
                dataset,
                databaseCursor,
                period.Authority,
                CancellationToken.None);

            Assert.True(batch.IsSuccess, batch.IsFailure ? batch.Error.Message : null);

            databaseCalls++;

            Assert.Equal(0, fixture.Drain.ActiveCount);

            Assert.True(batch.Value.Page is null
                || batch.Value.Page.Items.Count <= CovenantProtectedArtifactErasurePage.MaxItems);

            if (batch.Value.Page is { } page)
            {

                Assert.Equal(dataset, page.ExpectedDatasetGeneration);

                databaseLabels.AddRange(page.Items.Select(static item => item.SensitivityLabelId));

            }

            databaseCursor = batch.Value.NextCursor;

            if (batch.Value.IsComplete)
            {

                break;

            }

        }

        while (true);

        Assert.Equal(4, databaseCalls);

        Assert.Equal(385, databaseLabels.Count);

        Assert.Equal(385, databaseLabels.Distinct().Count());

        Assert.Equal(
            databaseLabels.OrderBy(static value => Format(value), StringComparer.Ordinal),
            databaseLabels);

        List<CovenantManagedFileErasureRequest> managedRequests = [];

        Guid? managedCursor = null;

        int managedCalls = 0;

        do
        {

            Result<CovenantManagedFileErasureBatch> batch = await source.ReadNextManagedFileBatchAsync(
                ErasureOperationId,
                managedCursor,
                period.Authority,
                CancellationToken.None);

            Assert.True(batch.IsSuccess, batch.IsFailure ? batch.Error.Message : null);

            managedCalls++;

            Assert.Equal(0, fixture.Drain.ActiveCount);

            Assert.InRange(batch.Value.Requests.Count, 0, CovenantProtectedArtifactErasurePage.MaxItems);

            managedRequests.AddRange(batch.Value.Requests);

            managedCursor = batch.Value.NextCursor;

            if (batch.Value.IsComplete)
            {

                break;

            }

        }

        while (true);

        Assert.Equal(4, managedCalls);

        Assert.Equal(384, managedRequests.Count);

        Assert.Equal(384, managedRequests.Select(static request => request.SensitivityLabelId).Distinct().Count());

        Assert.All(managedRequests, static request => Assert.Equal(ErasureOperationId, request.OperationId));

        Assert.All(managedRequests, static request => Assert.NotEqual(Guid.Empty, request.WorkItemId));

        Assert.Equal(
            ExistingWorkItemId,
            Assert.Single(managedRequests, static request => request.SensitivityLabelId == LabelId(1)).WorkItemId);

        Assert.Equal(0, fixture.Drain.ActiveCount);

        Assert.Equal(0, fixture.Drain.MaximumActiveCount);

        // Same accounting, different factory. Nine owned snapshots — one preflight plus four pages
        // of each kind — each released before the next began, which is what "bounded pages whose
        // owned connections are gone before a kernel can run" means. They are counted on the
        // maintenance factory now because admission is closed while an erasure inventories itself,
        // and the ordinary factory the count used to come from is exactly what closure refuses.
        Assert.Equal(9, opens.Opened);

        Assert.Equal(0, opens.LiveLeaseCount);

        // Nothing reached for an ordinary handle behind the closed period's back.
        Assert.Empty(fixture.OrdinaryConnections.Opened);

        CovenantCanonicalDatasetTransition preselected = await fixture.PreselectAsync(CancellationToken.None);

        await fixture.ClosePrimaryConnectionAsync();

        CovenantCanonicalErasureTransaction canonical = new(
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System);

        // The same closed period the inventory ran under. That the erasure which follows an inventory
        // needs no second closure is the point of there being one authority per run.
        Result<Guid> applied = await canonical.ApplyAsync(
            CovenantExclusiveOperation.CovenantReset,
            preselected,
            period.Authority,
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

    }

    [Fact]
    public async Task Complete_preflight_refuses_a_managed_label_without_one_exact_adopted_owner_before_any_batch()
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.SeedInterleavedLabelsAsync(3, includeExistingWorkItem: false);

        await fixture.DeleteManagedProducerAsync(LabelId(1));

        CovenantErasureInventorySource source = fixture.CreateSource();

        Guid dataset = await fixture.ReadDatasetGenerationAsync();

        (CovenantClosedPeriodTestAuthority period, InventoryFixture.CountingMaintenanceFactory opens) =
            await fixture.ClosedPeriodAsync();

        await using CovenantClosedPeriodTestAuthority closedPeriod = period;

        Result<CovenantErasureInventorySummary> preflight = await source.PreflightBeforeCanonicalAsync(
            CovenantExclusiveOperation.CovenantReset,
            dataset,
            period.Authority,
            CancellationToken.None);

        Assert.True(preflight.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualArtifactErasureRequired, preflight.Error.Code);

        Assert.Equal(0, fixture.Drain.ActiveCount);

        Assert.Equal(1, opens.Opened);

        Assert.Equal(0, opens.LiveLeaseCount);

        Assert.Equal(ConnectionState.Closed, opens.LastConnection!.State);

    }

    [Fact]
    public async Task Complete_preflight_refuses_a_wrong_lease_dataset_and_releases_its_private_handle()
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.SeedInterleavedLabelsAsync(1, includeExistingWorkItem: false);

        CovenantErasureInventorySource source = fixture.CreateSource();

        (CovenantClosedPeriodTestAuthority period, _) = await fixture.ClosedPeriodAsync();

        await using CovenantClosedPeriodTestAuthority closedPeriod = period;

        Result<CovenantErasureInventorySummary> preflight = await source.PreflightBeforeCanonicalAsync(
            CovenantExclusiveOperation.CovenantReset,
            Guid.NewGuid(),
            period.Authority,
            CancellationToken.None);

        Assert.True(preflight.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, preflight.Error.Code);

        Assert.Equal(0, fixture.Drain.ActiveCount);

    }

    [Fact]
    public async Task Factory_preflight_borrows_the_inventory_snapshot_and_opens_no_guard_handle()
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: true);

        await fixture.SeedInterleavedLabelsAsync(1, includeExistingWorkItem: false);

        CovenantErasureInventorySource source = fixture.CreateSource();

        Guid dataset = await fixture.ReadDatasetGenerationAsync();

        (CovenantClosedPeriodTestAuthority period, InventoryFixture.CountingMaintenanceFactory opens) =
            await fixture.ClosedPeriodAsync();

        await using CovenantClosedPeriodTestAuthority closedPeriod = period;

        Result<CovenantErasureInventorySummary> preflight = await source.PreflightBeforeCanonicalAsync(
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            dataset,
            period.Authority,
            CancellationToken.None);

        Assert.True(preflight.IsSuccess, preflight.IsFailure ? preflight.Error.Message : null);

        // One open, not two. The guard borrows the inventory's snapshot rather than acquiring a
        // handle of its own — which under a closed period is not merely wasteful but impossible, and
        // this is the assertion that says the borrowing is real rather than incidental.
        Assert.Equal(1, opens.Opened);

        Assert.Empty(fixture.OrdinaryConnections.Opened);

        Assert.Equal(0, fixture.Drain.MaximumActiveCount);

        Assert.Equal(0, fixture.Drain.ActiveCount);

    }

    [Fact]
    public async Task Factory_guard_dataset_and_labels_observe_one_snapshot_during_a_catalog_interleaving()
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: true);

        await fixture.SeedInterleavedLabelsAsync(1, includeExistingWorkItem: false);

        await using SqliteConnection connection = await fixture.Factory
            .OpenReadOnlyAsync(CancellationToken.None);

        using IDisposable enrollment = fixture.Drain.Register(connection);

        await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
            connection,
            CovenantSqliteConnectionMode.ReadOnly,
            CancellationToken.None);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(CancellationToken.None);

        await using (SqliteCommand establish = connection.CreateCommand())
        {

            establish.Transaction = transaction;

            establish.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

            Assert.IsType<byte[]>(await establish.ExecuteScalarAsync(CancellationToken.None));

        }

        await fixture.DamageCatalogAndAddDatabaseLabelAsync(index: 40);

        Result healthyOnSnapshot = await fixture.CreateGuard().RequireHealthyWithinAsync(
            connection,
            transaction,
            CancellationToken.None);

        Result<IReadOnlyList<ArtifactSensitivityLabel>> labelsOnSnapshot =
            await ArtifactSensitivityLedger.ReadPageWithinAsync(
                connection,
                transaction,
                afterLabelId: null,
                CovenantProtectedArtifactErasurePage.MaxItems,
                CancellationToken.None);

        Assert.True(healthyOnSnapshot.IsSuccess);

        Assert.True(labelsOnSnapshot.IsSuccess);

        Assert.Equal([LabelId(0)], labelsOnSnapshot.Value.Select(static label => label.LabelId));

        await transaction.DisposeAsync();

        await connection.CloseAsync();

        enrollment.Dispose();

        Result healthyOnNewSnapshot = await fixture.CreateGuard().RequireHealthyAsync(
            CancellationToken.None);

        Assert.True(healthyOnNewSnapshot.IsFailure);

        Assert.Equal(0, fixture.Drain.ActiveCount);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Complete_preflight_refuses_duplicate_producers_and_mismatched_active_work(
        bool duplicateProducer)
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.SeedInterleavedLabelsAsync(3, includeExistingWorkItem: !duplicateProducer);

        if (duplicateProducer)
        {

            await fixture.AddDuplicateManagedProducerAsync(index: 1);

        }
        else
        {

            await fixture.MismatchExistingWorkRevisionAsync();

        }

        Guid dataset = await fixture.ReadDatasetGenerationAsync();

        (CovenantClosedPeriodTestAuthority period, _) = await fixture.ClosedPeriodAsync();

        await using CovenantClosedPeriodTestAuthority closedPeriod = period;

        Result<CovenantErasureInventorySummary> preflight = await fixture.CreateSource()
            .PreflightBeforeCanonicalAsync(
                CovenantExclusiveOperation.CovenantReset,
                dataset,
                period.Authority,
                CancellationToken.None);

        Assert.True(preflight.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualArtifactErasureRequired, preflight.Error.Code);

        Assert.Equal(0, fixture.Drain.ActiveCount);

    }

    [Fact]
    public async Task Caller_cancellation_after_enrollment_closes_disposes_and_unregisters_the_handle()
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.SeedInterleavedLabelsAsync(1, includeExistingWorkItem: false);

        using CancellationTokenSource cancellation = new();

        fixture.OrdinaryConnections.BlockNextOpen();

        CovenantErasureInventorySource source = fixture.CreateSource();

        // Through the source read rather than the preflight, and the change is forced rather than
        // chosen. This asserts the lifecycle of the *ordinary* owned snapshot — enrolled, cancelled,
        // closed, disposed, unregistered — and after the inventory moved inside the closed period the
        // launch-time source read is the only caller that still takes that route. The preflight now
        // opens a maintenance snapshot instead, which has a different owner and a different teardown,
        // so pointing this at the preflight would silently stop testing the thing it is named for.
        Task<Result<CovenantOfflineTransitionSourceState>> reading =
            source.ReadOfflineTransitionSourceStateAsync(cancellation.Token);

        await fixture.OrdinaryConnections.OpenBlocked;

        cancellation.Cancel();

        fixture.OrdinaryConnections.AllowOpen();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);

        _ = Assert.Single(fixture.OrdinaryConnections.Opened);

        Assert.Equal(0, fixture.Drain.ActiveCount);

        Assert.Equal(0, fixture.OrdinaryConnections.LiveLeaseCount);

        Assert.Equal(
            ConnectionState.Closed,
            fixture.OrdinaryConnections.LastConnection!.State);

    }

    [Fact]
    public async Task Owning_snapshot_holds_one_fresh_read_only_ordinary_lease_through_final_read()
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.SeedInterleavedLabelsAsync(1, includeExistingWorkItem: false);

        RecordingFreshOrdinaryConnectionFactory connections =
            await fixture.CreateOrdinaryConnectionsAsync();

        CovenantErasureInventorySource source = fixture.CreateOrdinarySource(connections);

        using ScopedConsumerPause pause = new(
            "CovenantErasureInventorySource.WithOwnedSnapshotAsync");

        // The launch-time source read, for the same reason the cancellation test above uses it: it is
        // the one remaining caller of the ordinary owned snapshot whose lease lifetime this asserts.
        Task<Result<CovenantOfflineTransitionSourceState>> reading =
            source.ReadOfflineTransitionSourceStateAsync(CancellationToken.None);

        Task entered = pause.WaitUntilEnteredAsync();

        try
        {

            Task first = await Task.WhenAny(entered, reading);

            Assert.Same(entered, first);

            await entered;

            Assert.Equal(1, connections.LiveLeaseCount);

            Assert.Equal(
                [GrimoireOrdinaryFreshConnectionKind.ReadOnly],
                connections.Kinds);

            Assert.Equal(ConnectionState.Open, connections.LastConnection!.State);

        }
        finally
        {

            pause.Release();

            _ = await reading.WaitAsync(TimeSpan.FromSeconds(10));

        }

        Result<CovenantOfflineTransitionSourceState> result = await reading;

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(0, connections.LiveLeaseCount);

        Assert.Equal(ConnectionState.Closed, connections.LastConnection!.State);

    }

    private static Guid LabelId(int index) =>
        Guid.Parse($"00000000-0000-4000-8000-{index + 1:X12}");

    private static Guid ArtifactId(int index) =>
        Guid.Parse($"10000000-0000-4000-8000-{index + 1:X12}");

    private static Guid WriteOperationId(int index) =>
        Guid.Parse($"20000000-0000-4000-8000-{index + 1:X12}");

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    /// <summary>
    /// The offline-transition source read returns all four members of the tuple a launch binds to.
    /// </summary>
    /// <remarks>
    /// The generation alone was enough while phase authority lived in the database, because a resumed
    /// erasure could ask the row it was resuming from what it had already done. A journal-driven
    /// transition has no such row, so the only way to tell its own canonical commit from an unrelated
    /// one is to have committed to the exact epoch tuple beforehand — and a reader that returned three
    /// of the four would leave the fourth to be inferred at the one moment inference is fatal.
    /// </remarks>
    [Fact]
    public async Task Offline_transition_source_read_returns_the_generation_and_all_three_epochs()
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.SetCanonicalEpochsAsync(accelerator: 7L, keyReclamation: 11L, envelopeKey: 13L);

        Guid expected = await fixture.ReadDatasetGenerationAsync();

        CovenantErasureInventorySource source = fixture.CreateSource();

        Result<CovenantOfflineTransitionSourceState> read = await source
            .ReadOfflineTransitionSourceStateAsync(CancellationToken.None);

        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : null);

        Assert.Equal(expected, read.Value.DatasetGeneration);

        Assert.Equal(7UL, read.Value.AcceleratorEpoch);

        Assert.Equal(11UL, read.Value.KeyReclamationEpoch);

        Assert.Equal(13UL, read.Value.EnvelopeKeyEpoch);

    }

    /// <summary>
    /// An epoch that cannot be advanced is refused here, before admission has closed.
    /// </summary>
    /// <remarks>
    /// The persisted columns refuse a decrease and refuse any change at all once saturated, so a
    /// target preselected from a ceiling source would be rejected by the database after the transition
    /// had already stopped ordinary access — the one moment there is no safe answer left. Refusing at
    /// the read costs an operator a message; refusing at the write costs them a closed Grimoire.
    ///
    /// <para>Saturation is the arm a well-formed database can actually reach. The reader refuses a
    /// zero epoch too, but the column's own check constraint and the trigger's refusal to move one
    /// backward mean no schema-respecting write can produce one, so that arm stays defensive rather
    /// than exercised — a test that dropped the trigger to reach it would be proving something about
    /// a database this product cannot produce.</para>
    /// </remarks>
    [Theory]
    [InlineData(long.MaxValue, 4L, 5L)]
    [InlineData(4L, long.MaxValue, 5L)]
    [InlineData(4L, 5L, long.MaxValue)]
    public async Task Offline_transition_source_read_refuses_an_epoch_it_cannot_preselect_a_successor_for(
        long accelerator,
        long keyReclamation,
        long envelopeKey)
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.SetCanonicalEpochsAsync(accelerator, keyReclamation, envelopeKey);

        CovenantErasureInventorySource source = fixture.CreateSource();

        Result<CovenantOfflineTransitionSourceState> read = await source
            .ReadOfflineTransitionSourceStateAsync(CancellationToken.None);

        Assert.True(read.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, read.Error.Code);

    }

    /// <summary>
    /// A canonical singleton that is not there is a refusal rather than a default tuple.
    /// </summary>
    [Fact]
    public async Task Offline_transition_source_read_refuses_an_absent_canonical_singleton()
    {

        await using InventoryFixture fixture = await InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.DeleteCanonicalSingletonAsync();

        CovenantErasureInventorySource source = fixture.CreateSource();

        Result<CovenantOfflineTransitionSourceState> read = await source
            .ReadOfflineTransitionSourceStateAsync(CancellationToken.None);

        Assert.True(read.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, read.Error.Code);

    }

    internal sealed class InventoryFixture : IAsyncDisposable
    {

        private static readonly Guid ProvenanceGeneration =
            new("CCCCCCCC-3333-4333-8333-333333333333");

        private const string Now = "2026-08-20T00:00:00.0000000Z";

        private readonly CovenantSchemaScratchDatabase _database;

        private InventoryFixture(
            CovenantSchemaScratchDatabase database,
            CountingConnectionFactory factory,
            TrackingConnectionDrain drain,
            RecordingFreshOrdinaryConnectionFactory ordinaryConnections)
        {

            _database = database;

            Factory = factory;

            Drain = drain;

            OrdinaryConnections = ordinaryConnections;

        }

        internal CountingConnectionFactory Factory { get; }

        internal TrackingConnectionDrain Drain { get; }

        internal RecordingFreshOrdinaryConnectionFactory OrdinaryConnections { get; }

        internal static async Task<InventoryFixture> CreateAsync(bool healthyCatalog)
        {

            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase
                .CreateAsync(CancellationToken.None);

            try
            {

                await database.InstallCoreObjectsAsync(
                    [
                        "artifact_sensitivity",
                        "managed_file_write_intents",
                        "local_erasure_work_items",
                        "external_disclosure_state",
                        "capability_cleanup_state",
                    ],
                    CancellationToken.None);

                if (healthyCatalog)
                {

                    await database.InstallHealthyCovenantCatalogAsync(
                        withAccelerator: false,
                        CancellationToken.None);

                }
                else
                {

                    await database.InstallCanonicalAsync(CancellationToken.None);

                }

                CountingConnectionFactory factory = new(database.MaintenanceConnections());

                TrackingConnectionDrain drain = new(new CovenantConnectionDrain());

                RecordingFreshOrdinaryConnectionFactory ordinaryConnections =
                    new(database.Connection.ConnectionString);

                return new InventoryFixture(database, factory, drain, ordinaryConnections);

            }
            catch
            {

                await database.DisposeAsync();

                throw;

            }

        }

        internal CovenantHealthyCatalogErasureGuard CreateGuard() =>
            new(
                OrdinaryConnections,
                new GrimoireSchemaManifestInspector(
                    GrimoireSchemaTierOwnershipRegistry.CreateDefault()));

        internal CovenantErasureInventorySource CreateSource() =>
            new(
                OrdinaryConnections,
                CreateGuard(),
                new CovenantManagedFileErasureRequestReader(),
                new CovenantDisclosureExposureReader());

        internal async Task<RecordingFreshOrdinaryConnectionFactory>
            CreateOrdinaryConnectionsAsync()
        {

            await using SqliteConnection template = await Factory.OpenReadOnlyAsync(
                CancellationToken.None);

            string connectionString = template.ConnectionString;

            await template.CloseAsync();

            return new RecordingFreshOrdinaryConnectionFactory(connectionString);

        }

        internal CovenantErasureInventorySource CreateOrdinarySource(
            RecordingFreshOrdinaryConnectionFactory connections) =>
            new(
                connections,
                CreateOrdinaryGuard(connections),
                new CovenantManagedFileErasureRequestReader(),
                new CovenantDisclosureExposureReader());

        private CovenantHealthyCatalogErasureGuard CreateOrdinaryGuard(
            RecordingFreshOrdinaryConnectionFactory connections) =>
            new(
                connections,
                new GrimoireSchemaManifestInspector(
                    GrimoireSchemaTierOwnershipRegistry.CreateDefault()));

        internal async Task SeedInterleavedLabelsAsync(
            int count,
            bool includeExistingWorkItem)
        {

            await using SqliteTransaction transaction = _database.Connection.BeginTransaction(deferred: false);

            for (int index = 0; index < count; index++)
            {

                bool managed = (index & 1) == 1;

                ArtifactSensitivityLabel label = new(
                    LabelId(index),
                    managed
                        ? SensitiveArtifactKind.ManagedWorkspaceFile
                        : SensitiveArtifactKind.SearchProjection,
                    ArtifactId(index),
                    sessionId: null,
                    campaignId: null,
                    turnId: null,
                    artifactRevision: 7,
                    new CovenantDigest(DigestBytes(index)),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([ProvenanceGeneration]),
                    producingPlanDigest: null,
                    producingAdmissionDigest: null,
                    producingMaintenanceReceiptDigest: null,
                    DateTimeOffset.Parse(Now));

                await InsertLabelAsync(transaction, label);

                if (managed)
                {

                    await InsertManagedProducerAsync(transaction, index, label);

                }

            }

            if (includeExistingWorkItem)
            {

                await InsertExistingWorkItemAsync(transaction, index: 1);

            }

            await transaction.CommitAsync(CancellationToken.None);

        }

        internal async Task DeleteManagedProducerAsync(Guid labelId)
        {

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText =
                "DELETE FROM managed_file_write_intents WHERE SensitivityLabelId = $label;";

            _ = command.Parameters.AddWithValue("$label", Format(labelId));

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        internal async Task DamageCatalogAndAddDatabaseLabelAsync(int index)
        {

            await using SqliteTransaction transaction = _database.Connection.BeginTransaction(deferred: false);

            await using (SqliteCommand damage = _database.Connection.CreateCommand())
            {

                damage.Transaction = transaction;

                damage.CommandText = """
                    UPDATE grimoire_feature_schemas
                    SET HealthCode = 1,
                        HealthDetailCode = 'damaged'
                    WHERE FamilyCode = 1 AND TransactionTierCode = 1;
                    """;

                Assert.Equal(1, await damage.ExecuteNonQueryAsync(CancellationToken.None));

            }

            await InsertLabelAsync(transaction, DatabaseLabel(index));

            await transaction.CommitAsync(CancellationToken.None);

        }

        internal async Task AddDuplicateManagedProducerAsync(int index)
        {

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO managed_file_write_intents (
                    WriteOperationId, StableEffectIdentityDigest, ArtifactId, SensitivityLabelId,
                    SensitivityLabelDigest, PendingArtifactSensitivityLabel, DurableLocationEvidence,
                    ExpectedContentHash, ExpectedContentLength, CreatedChildPhysicalIdentityDigest,
                    FinalOwnershipEvidence, PhaseCode, Revision, RetryCount, CreatedAtUtc, UpdatedAtUtc)
                SELECT $duplicate, $effect, ArtifactId, SensitivityLabelId, SensitivityLabelDigest,
                       NULL, DurableLocationEvidence, ExpectedContentHash, ExpectedContentLength,
                       CreatedChildPhysicalIdentityDigest, FinalOwnershipEvidence, 7, Revision,
                       RetryCount, CreatedAtUtc, UpdatedAtUtc
                FROM managed_file_write_intents
                WHERE WriteOperationId = $source;
                """;

            _ = command.Parameters.AddWithValue("$duplicate", Format(Guid.NewGuid()));

            _ = command.Parameters.AddWithValue("$effect", DigestBytes(index + 2000));

            _ = command.Parameters.AddWithValue("$source", Format(WriteOperationId(index)));

            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));

        }

        internal async Task MismatchExistingWorkRevisionAsync()
        {

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                UPDATE local_erasure_work_items
                SET ExpectedSourceRevision = ExpectedSourceRevision + 1
                WHERE WorkItemId = $work;
                """;

            _ = command.Parameters.AddWithValue("$work", Format(ExistingWorkItemId));

            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));

        }

        internal async Task CorruptManagedProducerEvidenceAsync(bool durableLocation)
        {

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = durableLocation
                ? "UPDATE managed_file_write_intents SET DurableLocationEvidence = $malformed;"
                : "UPDATE managed_file_write_intents SET FinalOwnershipEvidence = $malformed;";

            _ = command.Parameters.AddWithValue("$malformed", new byte[] { 0x7F });

            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));

        }

        internal async Task<Guid> ReadDatasetGenerationAsync()
        {

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

            object? value = await command.ExecuteScalarAsync(CancellationToken.None);

            return new Guid(Assert.IsType<byte[]>(value));

        }

        internal async Task SetCanonicalEpochsAsync(
            long accelerator,
            long keyReclamation,
            long envelopeKey)
        {

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                UPDATE covenant_state
                SET AcceleratorEpoch = $accelerator,
                    KeyReclamationEpoch = $keyReclamation,
                    EnvelopeKeyEpoch = $envelopeKey,
                    UpdatedAtUtc = $now
                WHERE StateKey = 1;
                """;

            command.Parameters.AddWithValue("$accelerator", accelerator);

            command.Parameters.AddWithValue("$keyReclamation", keyReclamation);

            command.Parameters.AddWithValue("$envelopeKey", envelopeKey);

            command.Parameters.AddWithValue("$now", Now);

            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));

        }

        internal async Task DeleteCanonicalSingletonAsync()
        {

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = "DELETE FROM covenant_state WHERE StateKey = 1;";

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        internal async Task<CovenantCanonicalDatasetTransition> PreselectAsync(
            CancellationToken cancellationToken)
        {

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                SELECT DatasetGeneration, AcceleratorEpoch, KeyReclamationEpoch, EnvelopeKeyEpoch
                FROM covenant_state
                WHERE StateKey = 1;
                """;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken));

            CovenantOfflineTransitionEpochsV1 source = new(
                (ulong)reader.GetInt64(1),
                (ulong)reader.GetInt64(2),
                (ulong)reader.GetInt64(3));

            return new CovenantCanonicalDatasetTransition(
                new Guid(reader.GetFieldValue<byte[]>(0)),
                source,
                Guid.NewGuid(),
                new CovenantOfflineTransitionEpochsV1(
                    source.AcceleratorEpoch + 1,
                    source.KeyReclamationEpoch + 1,
                    source.EnvelopeKeyEpoch + 1));

        }

        internal Task ClosePrimaryConnectionAsync() => _database.Connection.CloseAsync();

        /// <summary>The scratch file this fixture's inventory reads, for a gate that must resolve to it.</summary>
        internal string DatabasePath => _database.DatabasePath;

        /// <summary>
        /// Closes a real admission gate over this fixture's file and counts what the inventory opens
        /// through it.
        /// </summary>
        /// <remarks>
        /// Every inventory read except the launch-time source read now happens inside a closed
        /// period, so the accounting this suite exists to do — one owned snapshot per call, released
        /// before a kernel could run — moved with it. The counter wraps the production factory rather
        /// than replacing it, so what is being counted is still a real maintenance open.
        ///
        /// <para>The primary connection is deliberately left out of the drain. The snapshots here are
        /// read-only and coexist with it happily, and the one test that goes on to run a canonical
        /// erasure closes it by hand first — which is the moment its exclusive lock actually starts
        /// to matter.</para>
        /// </remarks>
        internal async Task<(CovenantClosedPeriodTestAuthority Period, CountingMaintenanceFactory Opens)>
            ClosedPeriodAsync()
        {

            CountingMaintenanceFactory? counter = null;

            CovenantClosedPeriodTestAuthority period = await CovenantClosedPeriodTestAuthority.CloseAsync(
                _database.DatabasePath,
                CovenantSchemaScratchDatabase.ScratchPassphrase,
                CovenantSqliteConnectionInitializer.Instance,
                Drain,
                inner => counter = new CountingMaintenanceFactory(inner));

            return (period, counter!);

        }

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        /// <summary>Counts the closed-period opens the inventory performs, and their release.</summary>
        internal sealed class CountingMaintenanceFactory(IGrimoireMaintenanceConnectionFactory inner)
            : IGrimoireMaintenanceConnectionFactory
        {

            /// <summary>How many maintenance snapshots the inventory has opened.</summary>
            internal int Opened { get; private set; }

            /// <summary>How many of those are still open.</summary>
            internal int LiveLeaseCount { get; private set; }

            /// <summary>The connection the most recent snapshot ran on.</summary>
            internal SqliteConnection? LastConnection { get; private set; }

            public async Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalInventorySnapshotAsync(
                IGrimoireMaintenanceConnectionCapability capability,
                IGrimoireMaintenanceIoLane lane,
                CancellationToken cancellationToken)
            {

                Opened++;

                Result<IGrimoireMaintenanceConnectionLease> opened = await inner
                    .OpenJournalInventorySnapshotAsync(capability, lane, cancellationToken)
                    .ConfigureAwait(false);

                if (opened.IsFailure)
                {

                    return opened;

                }

                LiveLeaseCount++;

                LastConnection = opened.Value.Connection;

                return Result<IGrimoireMaintenanceConnectionLease>.Success(
                    new CountingLease(opened.Value, this));

            }

            public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCanonicalErasureAsync(
                IGrimoireMaintenanceConnectionCapability capability,
                IGrimoireMaintenanceIoLane lane,
                CancellationToken cancellationToken) =>
                inner.OpenJournalCanonicalErasureAsync(capability, lane, cancellationToken);

            public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalWalTruncationAsync(
                IGrimoireMaintenanceConnectionCapability capability,
                IGrimoireMaintenanceIoLane lane,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException("The inventory truncates no journal.");

            public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCompactionAsync(
                IGrimoireMaintenanceConnectionCapability capability,
                IGrimoireMaintenanceIoLane lane,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException("The inventory compacts nothing.");

            public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalExportVerificationAsync(
                IGrimoireMaintenanceConnectionCapability capability,
                IGrimoireMaintenanceIoLane lane,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException("The inventory verifies no export.");

            public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalAcceleratorInitializationAsync(
                IGrimoireMaintenanceConnectionCapability capability,
                IGrimoireMaintenanceIoLane lane,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException("The inventory initializes no accelerator.");

            public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCandidateReopenAsync(
                IGrimoireMaintenanceConnectionCapability capability,
                IGrimoireMaintenanceIoLane lane,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException("The inventory reopens no candidate.");

            private sealed class CountingLease(
                IGrimoireMaintenanceConnectionLease inner,
                CountingMaintenanceFactory owner) : IGrimoireMaintenanceConnectionLease
            {

                public SqliteConnection Connection => inner.Connection;

                public async ValueTask DisposeAsync()
                {

                    await inner.DisposeAsync().ConfigureAwait(false);

                    owner.LiveLeaseCount--;

                }

            }

        }

        private static async Task InsertLabelAsync(
            SqliteTransaction transaction,
            ArtifactSensitivityLabel label)
        {

            await using SqliteCommand command = transaction.Connection!.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO artifact_sensitivity (
                    LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                    ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                    ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                    ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                    CreatedAtUtc)
                VALUES (
                    $label, $kind, $artifact, $sensitivity, $mode, $generations, NULL, NULL, NULL, NULL,
                    $revision, $content, $sensitivityDigest, NULL, NULL, NULL, $labelDigest, $created);
                """;

            _ = command.Parameters.AddWithValue("$label", Format(label.LabelId));

            _ = command.Parameters.AddWithValue("$kind", (long)label.ArtifactKind);

            _ = command.Parameters.AddWithValue("$artifact", Format(label.ArtifactId));

            _ = command.Parameters.AddWithValue("$sensitivity", (long)label.Sensitivity);

            _ = command.Parameters.AddWithValue("$mode", (long)label.Provenance.Mode);

            _ = command.Parameters.AddWithValue("$generations", label.Provenance.ToCanonicalExactBytes());

            _ = command.Parameters.AddWithValue("$revision", checked((long)label.ArtifactRevision));

            _ = command.Parameters.AddWithValue("$content", label.ArtifactContentDigest.Bytes);

            _ = command.Parameters.AddWithValue("$sensitivityDigest", label.SensitivityDigest.Bytes);

            _ = command.Parameters.AddWithValue("$labelDigest", label.LabelDigest.Bytes);

            _ = command.Parameters.AddWithValue("$created", Now);

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        private static async Task InsertManagedProducerAsync(
            SqliteTransaction transaction,
            int index,
            ArtifactSensitivityLabel label)
        {

            ManagedFileDurableLocationEvidence target = new(
                new CovenantDigest(DigestBytes(index + 3000)),
                pathRevision: 1,
                [],
                new CovenantDigest(DigestBytes(index + 4000)),
                $"artifact-{index}.bin");

            ManagedFileWriteDurableLocationEvidence location = new(
                target,
                $"artifact-{index}.tmp");

            ManagedFileOwnershipEvidence ownership = new(
                new CovenantDigest(DigestBytes(index + 5000)),
                new CovenantDigest(DigestBytes(index + 6000)),
                contentLength: 1);

            await using SqliteCommand command = transaction.Connection!.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO managed_file_write_intents (
                    WriteOperationId, StableEffectIdentityDigest, ArtifactId, SensitivityLabelId,
                    SensitivityLabelDigest, PendingArtifactSensitivityLabel, DurableLocationEvidence,
                    ExpectedContentHash, ExpectedContentLength, CreatedChildPhysicalIdentityDigest,
                    FinalOwnershipEvidence, PhaseCode, Revision, RetryCount, CreatedAtUtc, UpdatedAtUtc)
                VALUES (
                    $write, $effect, $artifact, $label, $labelDigest, NULL, $location, zeroblob(32),
                    0, zeroblob(32), $ownership, 7, 7, 0, $now, $now);
                """;

            _ = command.Parameters.AddWithValue("$write", Format(WriteOperationId(index)));

            _ = command.Parameters.AddWithValue("$effect", DigestBytes(index + 1000));

            _ = command.Parameters.AddWithValue("$artifact", Format(label.ArtifactId));

            _ = command.Parameters.AddWithValue("$label", Format(label.LabelId));

            _ = command.Parameters.AddWithValue("$labelDigest", label.LabelDigest.Bytes);

            _ = command.Parameters.AddWithValue(
                "$location",
                ManagedFileEvidenceCodec.EncodeWriteLocation(location));

            _ = command.Parameters.AddWithValue(
                "$ownership",
                ManagedFileEvidenceCodec.EncodeOwnership(ownership));

            _ = command.Parameters.AddWithValue("$now", Now);

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        private static async Task InsertExistingWorkItemAsync(
            SqliteTransaction transaction,
            int index)
        {

            await using SqliteCommand command = transaction.Connection!.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO local_erasure_work_items (
                    WorkItemId, ErasureOperationId, SourceWriteOperationId, ExpectedSourceRevision,
                    ArtifactId, SourceSensitivityLabelId, DurableLocationEvidence,
                    ExpectedOwnershipEvidence, StateCode, DeletionEvidenceCode, CheckpointRevision,
                    RetryCount, CreatedAtUtc, UpdatedAtUtc)
                VALUES (
                    $work, $operation, $write, 7, $artifact, $label, zeroblob(64), zeroblob(64),
                    1, NULL, 0, 0, $now, $now);
                """;

            _ = command.Parameters.AddWithValue("$work", Format(ExistingWorkItemId));

            _ = command.Parameters.AddWithValue("$operation", Format(Guid.NewGuid()));

            _ = command.Parameters.AddWithValue("$write", Format(WriteOperationId(index)));

            _ = command.Parameters.AddWithValue("$artifact", Format(ArtifactId(index)));

            _ = command.Parameters.AddWithValue("$label", Format(LabelId(index)));

            _ = command.Parameters.AddWithValue("$now", Now);

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        private static byte[] DigestBytes(int value)
        {

            byte[] bytes = new byte[32];

            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), value + 1);

            return bytes;

        }

        private static ArtifactSensitivityLabel DatabaseLabel(int index) =>
            new(
                LabelId(index),
                SensitiveArtifactKind.SearchProjection,
                ArtifactId(index),
                sessionId: null,
                campaignId: null,
                turnId: null,
                artifactRevision: 7,
                new CovenantDigest(DigestBytes(index)),
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([ProvenanceGeneration]),
                producingPlanDigest: null,
                producingAdmissionDigest: null,
                producingMaintenanceReceiptDigest: null,
                DateTimeOffset.Parse(Now));

    }

    internal sealed class CountingConnectionFactory(IDesignTimeGrimoireConnectionFactory inner)
        : IDesignTimeGrimoireConnectionFactory
    {

        private int _readOnlyOpenCount;

        public string DatabasePath => inner.DatabasePath;

        internal int ReadOnlyOpenCount => Volatile.Read(ref _readOnlyOpenCount);

        internal SqliteConnection? LastReadOnlyConnection { get; private set; }

        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
            inner.OpenAsync(cancellationToken);

        public async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
        {

            _ = Interlocked.Increment(ref _readOnlyOpenCount);

            SqliteConnection connection = await inner.OpenReadOnlyAsync(cancellationToken);

            LastReadOnlyConnection = connection;

            return connection;

        }

        public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(CancellationToken cancellationToken) =>
            inner.OpenSidecarFreeReadOnlyAsync(cancellationToken);

        public Task<SqliteConnection> OpenSideFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            inner.OpenSideFileAsync(path, cancellationToken);

        public Task AttachSideFileAsync(
            SqliteConnection connection,
            string alias,
            string path,
            CancellationToken cancellationToken) =>
            inner.AttachSideFileAsync(connection, alias, path, cancellationToken);

    }

    internal sealed class TrackingConnectionDrain(ICovenantConnectionDrain inner)
        : ICovenantConnectionDrain
    {

        private int _activeCount;

        private int _maximumActiveCount;

        internal int ActiveCount => Volatile.Read(ref _activeCount);

        internal int MaximumActiveCount => Volatile.Read(ref _maximumActiveCount);

        public IDisposable Register(SqliteConnection connection)
        {

            IDisposable enrollment = inner.Register(connection);

            int active = Interlocked.Increment(ref _activeCount);

            int maximum;

            do
            {

                maximum = Volatile.Read(ref _maximumActiveCount);

                if (active <= maximum)
                {

                    break;

                }

            }

            while (Interlocked.CompareExchange(ref _maximumActiveCount, active, maximum) != maximum);

            return new TrackingEnrollment(this, enrollment);

        }

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            inner.DrainAsync(cancellationToken);

        public Result ClearExactPoolAfterClose(SqliteConnection connection) =>
            inner.ClearExactPoolAfterClose(connection);

        private sealed class TrackingEnrollment(
            TrackingConnectionDrain owner,
            IDisposable inner) : IDisposable
        {

            private int _disposed;

            public void Dispose()
            {

                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {

                    return;

                }

                inner.Dispose();

                _ = Interlocked.Decrement(ref owner._activeCount);

            }

        }

    }

}
