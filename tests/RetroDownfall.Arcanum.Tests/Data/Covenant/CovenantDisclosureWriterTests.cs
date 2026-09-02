using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The process-wide warm disclosure writer, including its real SQLCipher connection lifecycle.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CovenantDisclosureWriterTests
{

    private static readonly Guid Installation = new("11111111-2222-3333-4444-555555555555");

    private static readonly Guid TurnId = new("66666666-7777-8888-9999-aaaaaaaaaaaa");

    private static readonly Guid BootId = new("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");

    private static CancellationToken Token => CancellationToken.None;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Initial_lazy_open_refuses_unhealthy_or_empty_published_availability_before_opening(
        bool healthy,
        bool emptyDataset)
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync();

        harness.Availability!.Publish(
            healthy ? CovenantCapabilityState.Healthy : CovenantCapabilityState.Unavailable,
            emptyDataset ? Guid.Empty : harness.DatasetGeneration);

        Result<CovenantDisclosureReceipt> acknowledged = await harness.Subject.AcknowledgeAsync(
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            Token);

        Assert.True(acknowledged.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, acknowledged.Error.Code);

        Assert.Empty(harness.FreshConnections.Opened);

        Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);

    }

    [Fact]
    public async Task Initial_lazy_open_refuses_a_mismatched_database_dataset_and_cleans_the_candidate()
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync();

        harness.Availability!.Publish(CovenantCapabilityState.Healthy, Guid.NewGuid());

        Result<CovenantDisclosureReceipt> acknowledged = await harness.Subject.AcknowledgeAsync(
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            Token);

        Assert.True(acknowledged.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, acknowledged.Error.Code);

        SqliteConnection candidate = Assert.Single(harness.FreshConnections.Opened);

        Assert.Equal(ConnectionState.Closed, candidate.State);

        Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);

        Result<CovenantDisclosureReceipt> stillClosed = await harness.Subject.AcknowledgeAsync(
            Draft(2),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            Token);

        Assert.True(stillClosed.IsFailure);

        Assert.Single(harness.FreshConnections.Opened);

    }

    [Fact]
    public async Task Candidate_is_admitted_as_one_warm_read_write_ordinary_lease_after_proof()
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync();

        Result<CovenantDisclosureReceipt> acknowledged = await harness.Subject.AcknowledgeAsync(
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            Token);

        Assert.True(acknowledged.IsSuccess, acknowledged.IsFailure ? acknowledged.Error.Message : null);

        Assert.Equal(
            [GrimoireOrdinaryFreshConnectionKind.ReadWrite],
            harness.FreshConnections.Kinds);

        Assert.Equal(1, harness.FreshConnections.LiveLeaseCount);

    }

    [Fact]
    public async Task Quiesce_closes_admission_before_waiting_for_the_inflight_commit()
    {

        BlockingTransactionWriter transaction = new();

        await using WriterHarness harness = await WriterHarness.CreateAsync(transaction);

        Task<Result<CovenantDisclosureReceipt>> inFlight = harness.AcknowledgeAsync(1);

        await transaction.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        SqliteConnection warm = Assert.Single(harness.FreshConnections.Opened);

        Task<Result> quiesce = harness.Subject.QuiesceAsync(Token).AsTask();

        Result<CovenantDisclosureReceipt> rejected = await harness.Subject.AcknowledgeAsync(
            Draft(2),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            Token);

        Assert.True(rejected.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, rejected.Error.Code);

        Assert.False(quiesce.IsCompleted);

        transaction.Release();

        Assert.True((await inFlight).IsSuccess);

        Assert.True((await quiesce).IsSuccess);

        Assert.Equal(ConnectionState.Closed, warm.State);

        Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);

    }

    [Fact]
    public async Task Reopen_is_idempotent_without_a_duplicate_connection_or_enrolment()
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync();

        Assert.True((await harness.Subject.QuiesceAsync(Token)).IsSuccess);

        Assert.True((await harness.Subject.ReopenAsync(Token)).IsSuccess);

        SqliteConnection warm = Assert.Single(harness.FreshConnections.Opened);

        Assert.True((await harness.Subject.ReopenAsync(Token)).IsSuccess);

        Assert.Same(warm, Assert.Single(harness.FreshConnections.Opened));

        Assert.Equal(ConnectionState.Open, warm.State);

        Assert.Equal(1, harness.FreshConnections.LiveLeaseCount);

    }

    [Fact]
    public async Task Reopen_after_a_cancelled_quiesce_waits_for_admitted_work_and_uses_a_fresh_handle()
    {

        BlockingTransactionWriter transaction = new();

        await using WriterHarness harness = await WriterHarness.CreateAsync(transaction);

        Task<Result<CovenantDisclosureReceipt>> inFlight = harness.AcknowledgeAsync(1);

        await transaction.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        SqliteConnection oldWarm = Assert.Single(harness.FreshConnections.Opened);

        using CancellationTokenSource interrupted = new();

        Task<Result> quiesce = harness.Subject.QuiesceAsync(interrupted.Token).AsTask();

        interrupted.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quiesce);

        Task<Result> reopen = harness.Subject.ReopenAsync(Token).AsTask();

        Assert.False(reopen.IsCompleted);

        transaction.Release();

        Assert.True((await inFlight).IsSuccess);

        Assert.True((await reopen).IsSuccess);

        SqliteConnection fresh = Assert.Single(
            harness.FreshConnections.Opened,
            connection => !ReferenceEquals(connection, oldWarm));

        Assert.Equal(ConnectionState.Closed, oldWarm.State);

        Assert.Equal(ConnectionState.Open, fresh.State);

        Assert.Equal(1, harness.FreshConnections.LiveLeaseCount);

        Assert.True((await harness.AcknowledgeAsync(2)).IsSuccess);

    }

    [Fact]
    public async Task Quiesce_requested_during_reopen_closes_the_candidate_and_keeps_admission_closed()
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync();

        Assert.True((await harness.Subject.QuiesceAsync(Token)).IsSuccess);

        harness.FreshConnections.BlockNextOpen();

        Task<Result> reopen = Task.Run(
            async () => await harness.Subject.ReopenAsync(Token).ConfigureAwait(false));

        await harness.FreshConnections.OpenBlocked;

        SqliteConnection candidate = Assert.Single(harness.FreshConnections.Opened);

        Task<Result> quiesce = harness.Subject.QuiesceAsync(Token).AsTask();

        Assert.False(quiesce.IsCompleted);

        harness.FreshConnections.AllowOpen();

        Result reopened = await reopen;

        Assert.True(reopened.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, reopened.Error.Code);

        Assert.True((await quiesce).IsSuccess);

        Assert.Equal(ConnectionState.Closed, candidate.State);

        Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);

        Assert.True((await harness.AcknowledgeAsync(1)).IsFailure);

    }

    [Fact]
    public async Task Dispose_requested_during_reopen_closes_the_candidate_and_keeps_admission_closed()
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync();

        Assert.True((await harness.Subject.QuiesceAsync(Token)).IsSuccess);

        harness.FreshConnections.BlockNextOpen();

        Task<Result> reopen = Task.Run(
            async () => await harness.Subject.ReopenAsync(Token).ConfigureAwait(false));

        await harness.FreshConnections.OpenBlocked;

        SqliteConnection candidate = Assert.Single(harness.FreshConnections.Opened);

        Task dispose = harness.Subject.DisposeAsync().AsTask();

        Assert.False(dispose.IsCompleted);

        harness.FreshConnections.AllowOpen();

        Result reopened = await reopen;

        Assert.True(reopened.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, reopened.Error.Code);

        await dispose;

        Assert.Equal(ConnectionState.Closed, candidate.State);

        Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);

        Assert.True((await harness.AcknowledgeAsync(1)).IsFailure);

    }

    [Fact]
    public async Task Disposal_is_idempotent_and_waits_for_admitted_work_before_closing_the_handle()
    {

        BlockingTransactionWriter transaction = new();

        await using WriterHarness harness = await WriterHarness.CreateAsync(transaction);

        Task<Result<CovenantDisclosureReceipt>> inFlight = harness.AcknowledgeAsync(1);

        await transaction.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        SqliteConnection warm = Assert.Single(harness.FreshConnections.Opened);

        Task first = harness.Subject.DisposeAsync().AsTask();

        Task second = harness.Subject.DisposeAsync().AsTask();

        Result<CovenantDisclosureReceipt> rejected = await harness.AcknowledgeAsync(2);

        Assert.True(rejected.IsFailure);

        Assert.False(first.IsCompleted);

        Assert.False(second.IsCompleted);

        transaction.Release();

        Assert.True((await inFlight).IsSuccess);

        await Task.WhenAll(first, second);

        Assert.Equal(ConnectionState.Closed, warm.State);

        Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);

        await harness.Subject.DisposeAsync();

    }

    [Fact]
    public async Task Warm_writer_owns_one_read_write_ordinary_lease_until_physical_close_precedes_release()
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync();

        Assert.DoesNotContain(
            typeof(CovenantDisclosureWriter)
                .GetConstructors(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic)
                .SelectMany(static constructor => constructor.GetParameters()),
            static parameter =>
                parameter.ParameterType == typeof(IStoppedHostGrimoireConnectionFactory)
                || parameter.ParameterType == typeof(ICovenantConnectionDrain));

        Result<CovenantDisclosureReceipt> acknowledged = await harness.AcknowledgeAsync(1);

        Assert.True(acknowledged.IsSuccess, acknowledged.IsFailure ? acknowledged.Error.Message : null);

        Assert.Equal(
            [GrimoireOrdinaryFreshConnectionKind.ReadWrite],
            harness.FreshConnections.Kinds);

        SqliteConnection warm = Assert.Single(harness.FreshConnections.Opened);

        Assert.Equal(ConnectionState.Open, warm.State);

        Assert.Equal(1, harness.FreshConnections.LiveLeaseCount);

        harness.FreshConnections.BlockNextRelease();

        Task<Result> quiesce = harness.Subject.QuiesceAsync(Token).AsTask();

        await harness.FreshConnections.ReleaseEntered.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(quiesce.IsCompleted);

        Assert.Equal(ConnectionState.Closed, warm.State);

        Assert.Equal(ConnectionState.Closed, harness.FreshConnections.StateAtRelease);

        Assert.Equal(1, harness.FreshConnections.LiveLeaseCount);

        Result drained = await harness.FreshConnections.DrainAsync(Token);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Equal(1, harness.FreshConnections.LiveLeaseCount);

        harness.FreshConnections.AllowRelease();

        Assert.True((await quiesce.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);

    }

    [Fact]
    public async Task An_ordinary_open_failure_keeps_the_writer_closed_without_a_lease()
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync();

        harness.FreshConnections.RefuseNextOpen();

        Result<CovenantDisclosureReceipt> acknowledged = await harness.AcknowledgeAsync(1);

        Assert.True(acknowledged.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, acknowledged.Error.Code);

        Assert.Empty(harness.FreshConnections.Opened);

        Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);

    }

    [Fact]
    public async Task Real_writer_restores_the_unchanged_old_generation_on_a_fresh_handle()
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync(
            new CovenantDisclosureTransactionWriter(BootId),
            productionAvailability: true);

        Assert.True((await harness.AcknowledgeAsync(1)).IsSuccess);

        SqliteConnection oldWarm = Assert.Single(harness.FreshConnections.Opened);

        Assert.True((await harness.Subject.QuiesceAsync(Token)).IsSuccess);

        Assert.True((await harness.Subject.ReopenAsync(Token)).IsSuccess);

        SqliteConnection fresh = Assert.Single(
            harness.FreshConnections.Opened,
            connection => !ReferenceEquals(connection, oldWarm));

        Assert.Equal(ConnectionState.Closed, oldWarm.State);

        Assert.True((await harness.AcknowledgeAsync(2)).IsSuccess);

        Assert.Equal(2, await CountReceiptsAsync(fresh));

    }

    [Fact]
    public async Task Real_writer_restarts_against_the_fresh_generation_after_canonical_replacement()
    {

        await using WriterHarness harness = await WriterHarness.CreateAsync(
            new CovenantDisclosureTransactionWriter(BootId),
            productionAvailability: true);

        Assert.True((await harness.AcknowledgeAsync(1)).IsSuccess);

        Assert.True((await harness.Subject.QuiesceAsync(Token)).IsSuccess);

        CovenantCanonicalErasureTransaction erasure = new(
            harness.Fixture.V3Connections(),
            CovenantSqliteConnectionInitializer.Instance,
            harness.Fixture.Drain,
            TimeProvider.System);

        Result<Guid> applied = await erasure.ApplyAsync(
            CovenantExclusiveOperation.CovenantReset,
            CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
            Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        _ = harness.PublishedAvailability!.PublishCanonicalState(
            applied.Value,
            canonicalSequence: 0,
            coreCampaignDeletionSequence: 0,
            rebuildRequired: true,
            CovenantHealthTransition.Reset);

        Assert.False(File.Exists(harness.Fixture.DatabasePath + "-wal"));

        Assert.False(File.Exists(harness.Fixture.DatabasePath + "-shm"));

        Assert.True((await harness.Subject.ReopenAsync(Token)).IsSuccess);

        SqliteConnection fresh = harness.FreshConnections.Opened[^1];

        Assert.True((await harness.AcknowledgeAsync(2)).IsSuccess);

        Assert.Equal(applied.Value, await ReadDatasetGenerationAsync(fresh));

        Assert.Equal(2, await CountReceiptsAsync(fresh));

    }

    [Fact]
    public async Task Both_public_writer_interfaces_resolve_to_the_same_singleton()
    {

        ServiceCollection services = [];

        services.AddArcanumGrimoireForCli();

        await using ServiceProvider provider = services.BuildServiceProvider();

        ICovenantDisclosureJournal journal =
            provider.GetRequiredService<ICovenantDisclosureJournal>();

        ICovenantDisclosureWriterLifecycle lifecycle =
            provider.GetRequiredService<ICovenantDisclosureWriterLifecycle>();

        Assert.Same(journal, lifecycle);

        Assert.IsType<CovenantDisclosureWriter>(journal);

    }

    private static readonly GenerationProvenance Provenance =
        GenerationProvenance.CreateExact([CovenantTask6Fixture.DatasetGeneration]);

    private static readonly ProviderCallSensitivity Sensitivity = new(
        ContentSensitivity.CovenantDerived,
        Provenance,
        CovenantDigests.Sensitivity(new SensitivityDigestInput(
            ContentSensitivity.CovenantDerived,
            Provenance.Mode,
            Provenance.ExactGenerationIds,
            Provenance.BloomBits)));

    private static CovenantDisclosureDraft Draft(byte effectSeed) =>
        new(
            Installation,
            CovenantDisclosureSubjectKind.Turn,
            TurnId,
            CovenantTask6Fixture.D(effectSeed),
            CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantTask6Fixture.D(80),
            Sensitivity.Digest,
            null,
            CovenantTask6Fixture.D(82),
            null,
            1_700_000_000_000);

    private static async Task<long> CountReceiptsAsync(SqliteConnection connection)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM external_disclosure_receipts;";

        object? value = await command.ExecuteScalarAsync(Token);

        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

    }

    private static async Task<Guid> ReadDatasetGenerationAsync(SqliteConnection connection)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

        byte[] value = Assert.IsType<byte[]>(await command.ExecuteScalarAsync(Token));

        return new Guid(value);

    }

    private static CovenantAvailability HealthyAvailability(Guid datasetGeneration)
    {

        const string CanonicalFingerprint =
            "sha256-1111111111111111111111111111111111111111111111111111111111111111";

        const string AcceleratorFingerprint =
            "sha256-2222222222222222222222222222222222222222222222222222222222222222";

        const string SourceFingerprint =
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        static GrimoireSchemaTierInstallResult HealthyTier(
            GrimoireSchemaTransactionTier tier,
            string source,
            string installed) =>
            new(
                tier,
                SchemaVersion: 1,
                GrimoireSchemaTierHealth.Healthy,
                source,
                installed,
                DiagnosticCode: null);

        CovenantAvailability availability = new();

        _ = availability.PublishSchema(
            new GrimoireSchemaInstallResult(
                HealthyTier(GrimoireSchemaTransactionTier.Core, SourceFingerprint, CanonicalFingerprint),
                HealthyTier(
                    GrimoireSchemaTransactionTier.CovenantCanonical,
                    SourceFingerprint,
                    CanonicalFingerprint),
                HealthyTier(
                    GrimoireSchemaTransactionTier.CovenantAccelerator,
                    SourceFingerprint,
                    AcceleratorFingerprint)),
            CovenantHealthTransition.Bootstrap);

        _ = availability.PublishCanonicalState(
            datasetGeneration,
            canonicalSequence: 0,
            coreCampaignDeletionSequence: 0,
            rebuildRequired: false,
            CovenantHealthTransition.Bootstrap);

        return availability;

    }

    private sealed class WriterHarness : IAsyncDisposable
    {

        private WriterHarness(
            CovenantCanonicalErasureFixture fixture,
            Guid datasetGeneration,
            RecordingFreshOrdinaryConnectionFactory freshConnections,
            MutableAvailability? availability,
            CovenantAvailability? publishedAvailability,
            CovenantDisclosureWriter subject)
        {

            Fixture = fixture;

            DatasetGeneration = datasetGeneration;

            FreshConnections = freshConnections;

            Availability = availability;

            PublishedAvailability = publishedAvailability;

            Subject = subject;

        }

        internal CovenantCanonicalErasureFixture Fixture { get; }

        internal Guid DatasetGeneration { get; }

        internal RecordingFreshOrdinaryConnectionFactory FreshConnections { get; }

        internal MutableAvailability? Availability { get; }

        internal CovenantAvailability? PublishedAvailability { get; }

        internal CovenantDisclosureWriter Subject { get; }

        internal static async Task<WriterHarness> CreateAsync(
            ICovenantDisclosureTransactionWriter? transaction = null,
            bool productionAvailability = false)
        {

            CovenantCanonicalErasureFixture fixture =
                await CovenantCanonicalErasureFixture.CreateAsync(Token);

            try
            {

                Guid datasetGeneration = Assert.IsType<Guid>(
                    await fixture.ReadDatasetGenerationAsync(Token));

                await using SqliteConnection freshTemplate = await fixture.Connections()
                    .OpenAsync(Token);

                string freshConnectionString = freshTemplate.ConnectionString;

                await freshTemplate.CloseAsync();

                RecordingFreshOrdinaryConnectionFactory freshConnections = new(
                    freshConnectionString);

                MutableAvailability? mutable = productionAvailability
                    ? null
                    : new MutableAvailability(datasetGeneration);

                CovenantAvailability? published = productionAvailability
                    ? HealthyAvailability(datasetGeneration)
                    : null;

                ICovenantAvailability writerAvailability = published is not null
                    ? published
                    : mutable!;

                ICovenantDisclosureTransactionWriter resolvedTransaction =
                    transaction ?? new ImmediateTransactionWriter();

                CovenantDisclosureWriter subject = new(
                    freshConnections,
                    writerAvailability,
                    resolvedTransaction);

                return new WriterHarness(
                    fixture,
                    datasetGeneration,
                    freshConnections,
                    mutable,
                    published,
                    subject);

            }
            catch
            {

                await fixture.DisposeAsync();

                throw;

            }

        }

        internal Task<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(byte effectSeed) =>
            Subject.AcknowledgeAsync(
                Draft(effectSeed),
                CovenantDisclosureEffectCategory.ProviderDispatch,
                Sensitivity,
                Token).AsTask();

        public async ValueTask DisposeAsync()
        {

            await Subject.DisposeAsync();

            await Fixture.DisposeAsync();

        }

    }

    private sealed class MutableAvailability(Guid datasetGeneration) : ICovenantAvailability
    {

        private CovenantAvailabilitySnapshot _current = Snapshot(
            CovenantCapabilityState.Healthy,
            datasetGeneration);

        public CovenantAvailabilitySnapshot Current => Volatile.Read(ref _current);

        internal void Publish(CovenantCapabilityState canonical, Guid? datasetGeneration)
        {

            CovenantAvailabilitySnapshot current = Current;

            Volatile.Write(
                ref _current,
                current with
                {

                    Generation = current.Generation + 1,

                    Canonical = canonical,

                    DatasetGeneration = datasetGeneration,

                });

        }

        private static CovenantAvailabilitySnapshot Snapshot(
            CovenantCapabilityState canonical,
            Guid? datasetGeneration) =>
            new(
                Generation: 1,
                FeatureEnabled: true,
                canonical,
                CanonicalSchemaVersion: 1,
                CanonicalInstalledFingerprint: "fingerprint",
                Accelerator: CovenantCapabilityState.Healthy,
                AcceleratorSchemaVersion: 1,
                AcceleratorInstalledFingerprint: "fingerprint",
                datasetGeneration,
                CanonicalSequence: 0,
                CoreCampaignDeletionSequence: 0,
                AppliedDatasetGeneration: datasetGeneration,
                AppliedSequence: 0,
                AppliedCampaignDeletionSequence: 0,
                AcceleratorEpoch: 1,
                CovenantFtsSynchronizationState.Synchronized,
                RebuildRequired: false,
                CovenantHealthTransition.Bootstrap,
                CanonicalDiagnosticCode: null,
                AcceleratorDiagnosticCode: null);

    }

    private sealed class ImmediateTransactionWriter : ICovenantDisclosureTransactionWriter
    {

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            SqliteConnection connection,
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                Result<CovenantDisclosureReceipt>.Success(
                    new CovenantDisclosureReceipt(draft, allocatedSubjectOrdinal: 1)));

    }

    private sealed class BlockingTransactionWriter : ICovenantDisclosureTransactionWriter
    {

        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _ = _release.TrySetResult(true);

        public async ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            SqliteConnection connection,
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken)
        {

            _ = _entered.TrySetResult(true);

            await _release.Task.WaitAsync(cancellationToken);

            return Result<CovenantDisclosureReceipt>.Success(
                new CovenantDisclosureReceipt(draft, allocatedSubjectOrdinal: 1));

        }

    }

}
