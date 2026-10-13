using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed partial class LongRestMaintenanceTests
{
    [Fact]
    public async Task Production_issuance_accepts_only_the_exact_gate_wrapper_and_durable_pending_claim()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        var issued = await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None);

        Assert.True(issued.IsSuccess, issued.IsFailure ? issued.Error.Code : "");

        Assert.Same(f.Authority, issued.Value.Authority);

        await using CovenantTurnLease forged = new(new ForgedRegistration(f.Authority.Snapshot));

        Assert.True((await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, forged, CancellationToken.None)).IsFailure);

        await f.H.ExecuteAsync("UPDATE session_turn_claims SET ExecutorId = $executor;", ("$executor", Guid.NewGuid().ToString("D").ToUpperInvariant()));

        Assert.True((await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None)).IsFailure);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task Production_maintenance_captures_then_bills_exact_protected_summary_and_embedding()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync();

        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation,
            f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None));

        Assert.True((await capability.ConsumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None)).IsSuccess);

        var capture = await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None);

        Assert.True(capture.IsSuccess, capture.IsFailure ? capture.Error.Code : "");

        Assert.Equal(0, f.Handler.SendCount);

        var fold = await f.Maintenance.ProcessAsync(f.Operation, "fixture-worker", capability, CancellationToken.None);

        Assert.True(fold.IsSuccess, fold.IsFailure ? fold.Error.Code : "");

        Assert.Equal(2, f.Handler.SendCount);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects WHERE StateCode = 4;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));
    }

    private sealed class ForgedRegistration(CovenantOperationLeaseSnapshot snapshot) : ICovenantLeaseRegistration
    {
        public CovenantOperationLeaseSnapshot Snapshot => snapshot;

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }

    internal sealed class MaintenanceFixture : IAsyncDisposable
    {
        internal LongRestSweepHarness H = null!;

        internal LongRestConfigurationEpochTests.NotifyingOptions Options = null!;

        internal LongRestConfigurationEpoch Epoch = null!;

        internal CovenantRuntimeGenerationProvider Runtime = new();

        internal FakeCovenantAvailability Availability = new();

        internal Action? PaidRecorded { get; set; }

        internal Action? Completing { get; set; }

        internal Result<LongRestProviderResult>? ProviderResult { get; set; }

        internal Func<Task>? ProviderReturned { get; set; }

        internal bool RepeatDispatch;

        internal List<LongRestProviderPayload> Payloads = [];

        internal GenerationProvenance Provenance = null!;

        internal GrimoireConnectionAdmissionGate Admission = new(TimeProvider.System);

        internal RetroDownfall.Arcanum.Tests.Support.RecordingGrimoireWorkAdmissionGate RecordedAdmission = null!;

        internal Func<LongRestDispatchAttempt, CancellationToken, Task>? AfterAttemptValidated;

        internal Func<ILongRestAttemptStore, ILongRestAttemptStore>? DecorateIndependentAttempts;

        internal Func<ILongRunningOperationStore, ILongRunningOperationStore>? DecorateIndependentOperations;

        internal Action? IndependentScopeDisposed;

        internal Action<CancellationToken>? DispatchObserved;

        internal TimeProvider Clock = TimeProvider.System;

        internal int IndependentFactsResolved;

        internal System.Collections.Concurrent.ConcurrentBag<ArcanumDbContext> IndependentContexts = [];

        internal CovenantTurnLease Authority = null!;

        internal LongRestMaintenance Maintenance = null!;

        internal LongRestWorkAdmission WorkAdmission = null!;

        internal SessionTurnClaimLease Claim = null!;

        internal ArcanumInvocationContext Invocation = null!;

        internal CanonicalCampaignContext Scope = CanonicalCampaignContext.GlobalOnly;

        internal LongRestSweepScope Partition;

        internal Guid Operation = Guid.NewGuid();

        internal Guid SourceSession;

        internal string Source = Guid.NewGuid().ToString("D");

        internal LongRestProviderFixture.CountingProvider Handler = new();

        private LongRestProviderClientFactory? _clientFactory;

        internal ServiceProvider Services = null!;

        internal DurableJournal Journal = null!;

        internal LongRunningOperationStore Operations = null!;

        internal LongRestSweepCoordinator Coordinator = null!;

        internal CovenantOperationGate Gate = null!;

        internal Guid? CampaignId;

        internal static async Task<MaintenanceFixture> CreateAsync(bool ownWorker = true, TimeProvider? clock = null, bool campaign = false,
            bool protectedSources = true, bool exact = false, int sourceCount = 2)
        {
            MaintenanceFixture f = new();

            f.Clock = clock ?? TimeProvider.System;

            f.RecordedAdmission = new(f.Admission);

            f.WorkAdmission = new(f.RecordedAdmission);

            f.H = await LongRestSweepHarness.CreateAsync();

            f.Options = new(LongRestConfigurationEpochTests.Settings());

            f.Epoch = new(f.Options, Guid.NewGuid());

            f.CampaignId = campaign ? Guid.NewGuid() : null;

            f.SourceSession = await f.H.SessionAsync(f.CampaignId);

            if (campaign)
            {
                f.Scope = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(f.CampaignId!.Value),
                    (long)(await f.H.ScalarAsync("SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;"))!, 1, null, null);
            }

            await f.InitializeRuntimeAsync();

            await f.H.SeedAsync(f.Source, "Alpha observation.", f.SourceSession);

            Guid second = Guid.NewGuid();

            Guid secondSession = exact ? await f.H.SessionAsync(f.CampaignId) : f.SourceSession;

            await f.H.SeedAsync(second.ToString("D"), exact ? "Alpha observation." : "Beta observation.", secondSession);

            List<Guid> additional = [];

            for (int index = 2; index < sourceCount; index++)
            {
                Guid id = Guid.NewGuid();

                additional.Add(id);

                await f.H.SeedAsync(id.ToString("D"), $"Observation number {index}.", f.SourceSession);
            }

            Assert.Equal((long)sourceCount, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sources;"));

            GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

            f.Provenance = provenance;

            CovenantDigest? partition = null;

            if (protectedSources)
            {
                partition = await f.H.ProtectAsync(Guid.Parse(f.Source), provenance);

                await f.H.ProtectAsync(second, provenance);

                foreach (Guid id in additional) await f.H.ProtectAsync(id, provenance);
            }

            f.Partition = LongRestSweepScope.Create(new(campaign ? LongRestSweepScopeKind.Campaign : LongRestSweepScopeKind.Global, f.CampaignId), partition).Value;

            await f.H.ExecuteAsync("INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 0, '2026-10-10T00:00:00.0000000Z', 'sweep');", ("$id", f.Operation.ToString("N")));

            LongRestSweepStore sweeps = new(f.H.Saga.Context, f.Clock);

            LongRestSweepHarness.Require(await sweeps.BeginOrResumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None));

            FixedCovenantConnectionSource connections = new(f.H.Connection);

            LongRestLiveFenceReader facts = new(f.H.Saga.Context, f.Runtime, f.RecordedAdmission, f.Epoch,
                new CampaignAvailabilityReader(connections), new CampaignPathIdentityReader(connections, null!));

            LongRestSweepFence fence = LongRestSweepHarness.Require(await facts.CaptureAsync(f.Scope, f.Epoch.Capture().Stamp, null, CancellationToken.None));

            LongRestSweepHarness.Require(await f.H.Store.StartOrResumeAsync(f.Operation, f.Partition, fence, ((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.Value, CancellationToken.None));

            Guid session = await f.H.SessionAsync(f.CampaignId);

            f.Claim = LongRestSweepHarness.Require(await new SessionTurnClaimStore(new FixedCovenantConnectionSource(f.H.Connection), new CovenantQuotaGuard(), fence.Configuration.BootId)
                .AcquireAsync(new(Guid.NewGuid(), 1, Guid.NewGuid(), session, SessionTurnSurface.Intelligence, fence.Configuration.ValueDigest,
                    fence.Configuration.ValueDigest, null, 0, 0), CancellationToken.None));

            f.Invocation = ArcanumInvocationContext.Create(ArcanumExecutionSurface.SessionBackedOperatorTurn, f.Scope,
                InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools,
                CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), f.Authority.Snapshot.RuntimeAuthorityGeneration, f.Authority.Snapshot.AuthorityEpoch)).Value;

            LongRestAttemptStore attempts = new(f.H.Saga.Context, f.Clock);

            ServiceCollection services = [];

            services.AddScoped(_ => new IndependentScopeWitness(f.IndependentScopeDisposed));

            services.AddScoped(provider =>
            {
                provider.GetRequiredService<IndependentScopeWitness>();

                ArcanumDbContext db = f.H.Saga.CreateSiblingContext();

                f.IndependentContexts.Add(db);

                return db;
            });

            services.AddScoped(provider =>
            {
                Interlocked.Increment(ref f.IndependentFactsResolved);

                ArcanumDbContext db = provider.GetRequiredService<ArcanumDbContext>();

                FixedCovenantConnectionSource independent = new((SqliteConnection)db.Database.GetDbConnection());

                return new LongRestLiveFenceReader(db, f.Runtime, f.RecordedAdmission, f.Epoch,
                    new CampaignAvailabilityReader(independent), new CampaignPathIdentityReader(independent, null!));
            });

            services.AddScoped<ILongRestAttemptStore>(provider =>
            {
                ILongRestAttemptStore store = new LongRestAttemptStore(provider.GetRequiredService<ArcanumDbContext>(), f.Clock);

                return f.DecorateIndependentAttempts?.Invoke(store) ?? store;
            });

            services.AddScoped<ILongRunningOperationStore>(provider =>
            {
                ArcanumDbContext db = provider.GetRequiredService<ArcanumDbContext>();

                ILongRunningOperationStore store = new LongRunningOperationStore(db, TestOrdinaryConnectionFactory.For(db));

                return f.DecorateIndependentOperations?.Invoke(store) ?? store;
            });

            services.AddScoped<ILongRestSweepCoordinator>(_ => f.Coordinator);

            services.AddScoped(_ => f.WorkAdmission);

            f.Services = services.BuildServiceProvider();

            f.Journal = new(f);

            f.Compose();

            if (ownWorker)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;

                Assert.True((await f.Operations.TryAcquireLeaseAsync(f.Operation, "fixture-worker", now, now.AddMinutes(5))).Acquired);

                LongRestSweep committed = LongRestSweepHarness.Require(await sweeps.BeginOrResumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None));

                LongRestSweepCheckpoint checkpoint = new(1, f.Operation, f.Partition.Kind, f.Partition.CampaignId,
                    committed.GenerationId, committed.Phase, committed.Revision, committed.CommittedItemCursor,
                    committed.CommittedWorkDigest, f.Partition.ProtectedPartitionDigest);

                Assert.True(await f.Operations.SaveCheckpointAsync(f.Operation, "fixture-worker", 0, 1,
                    LongRestSweepCheckpointCodec.Encode(checkpoint).Value, null, "Long Rest", now));
            }

            return f;
        }

        private async Task InitializeRuntimeAsync()
        {
            FakeCovenantAuthorityProvider authority = new();

            CovenantEnvelopeMasterKeyProvider keys = new(Runtime);

            using CovenantPreparedEnvelopeKeyGeneration prepared = LongRestSweepHarness.Require(keys.PrepareInitial(
                "long-rest-isolated-fixture-master-material"u8.ToArray(), new(authority.Current!.InstallationIdentity, 1, 1, 1, CovenantOperationGateFixture.DatasetGeneration)));

            var availability = Runtime.PublishAvailability(_ => new FakeCovenantAvailability().Current);

            Assert.True(Runtime.Initialize(Runtime.Current, prepared, authority.Current, availability).IsSuccess);

            Availability.Bind(Runtime, keys);

            FakeCovenantCampaignScopeProbe campaignProbe = new();

            if (CampaignId is { } campaign) campaignProbe.Set(campaign, CovenantCampaignScopeState.Live);

            Gate = new(Runtime, campaignProbe, NullLogger<CovenantOperationGate>.Instance, TimeSpan.FromSeconds(5));

            Authority = LongRestSweepHarness.Require(await Gate.AcquireTurnAsync(Scope, CancellationToken.None));
        }

        private void Compose()
        {
            FixedCovenantConnectionSource connections = new(H.Connection);

            LongRestLiveFenceReader facts = new(H.Saga.Context, Runtime, RecordedAdmission, Epoch,
                new CampaignAvailabilityReader(connections), new CampaignPathIdentityReader(connections, null!));

            LongRestSweepStore sweeps = new(H.Saga.Context, Clock);

            ILongRestAttemptStore attempts = new PaidFenceAttemptStore(new LongRestAttemptStore(H.Saga.Context, Clock),
                (attempt, token) => AfterAttemptValidated?.Invoke(attempt, token) ?? Task.CompletedTask);

            ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

            _clientFactory ??= new(new LongRestProviderFixture.NoKeys(), Handler);

            LongRestProvider provider = new(_clientFactory,
                new ModelCallExecutor(estimator, Options), Epoch, new PaidWriter(new TurnRunWriter(H.Saga.Context), () => PaidRecorded?.Invoke(), () => Completing?.Invoke()),
                new BudgetReservationService(H.Saga.Context, Options));

            Maintenance = new(Epoch, H.Store, sweeps, attempts, facts, Gate, new ObservedProvider(provider, result => ProviderResult = result, () => RepeatDispatch, payload => Payloads.Add(payload), ct => DispatchObserved?.Invoke(ct), () => ProviderReturned?.Invoke() ?? Task.CompletedTask), estimator, Journal, WorkAdmission,
                Services.GetRequiredService<IServiceScopeFactory>(), Clock);

            Operations = new(H.Saga.Context, TestOrdinaryConnectionFactory.For(H.Saga.Context));

            Coordinator = new(Operations, sweeps, H.Store, Epoch, facts, Maintenance, Gate,
                new LongRunningOperationOwnership(), new LongRestSweepQueue(), Services.GetRequiredService<IServiceScopeFactory>(), Clock, WorkAdmission);
        }

        internal async Task RebootAsync()
        {
            Guid priorBoot = Epoch.Capture().Stamp.BootId;

            Epoch.Dispose();

            await Authority.DisposeAsync();

            Runtime.Dispose();

            Runtime = new();

            Epoch = new(Options, Guid.NewGuid());

            Assert.NotEqual(priorBoot, Epoch.Capture().Stamp.BootId);

            await InitializeRuntimeAsync();

            Guid session = await H.SessionAsync(CampaignId);

            CovenantDigest value = Epoch.Capture().Stamp.ValueDigest;

            Claim = LongRestSweepHarness.Require(await new SessionTurnClaimStore(new FixedCovenantConnectionSource(H.Connection), new CovenantQuotaGuard(), Epoch.Capture().Stamp.BootId)
                .AcquireAsync(new(Guid.NewGuid(), 1, Guid.NewGuid(), session, SessionTurnSurface.Intelligence, value, value, null, 0, 0), CancellationToken.None));

            Invocation = ArcanumInvocationContext.Create(ArcanumExecutionSurface.SessionBackedOperatorTurn, Scope,
                InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools,
                CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), Authority.Snapshot.RuntimeAuthorityGeneration, Authority.Snapshot.AuthorityEpoch)).Value;

            await H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z';");

            Compose();
        }

        public async ValueTask DisposeAsync()
        {
            _clientFactory?.Dispose();

            Epoch.Dispose();

            await Authority.DisposeAsync();

            Runtime.Dispose();

            await Services.DisposeAsync();

            await H.DisposeAsync();
        }

        private sealed class IndependentScopeWitness(Action? disposed) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                disposed?.Invoke();

                return ValueTask.CompletedTask;
            }
        }

        internal sealed class DurableJournal(MaintenanceFixture fixture) : ICovenantDisclosureJournal
        {
            internal Func<Task>? AfterCommit { get; set; }

            public async ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(CovenantDisclosureDraft draft,
                CovenantDisclosureEffectCategory category, ProviderCallSensitivity sensitivity, CancellationToken cancellationToken)
            {
                var receipt = await new CovenantDisclosureTransactionWriter(fixture.Epoch.Capture().Stamp.BootId)
                    .AcknowledgeAsync(fixture.H.Connection, draft, category, sensitivity, cancellationToken);

                if (AfterCommit is not null) await AfterCommit();

                return receipt;
            }
        }
    }
}
