using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Hosting;

[Collection("Grimoire")]
public sealed partial class LongRestSweepSchedulingTests
{
    [Fact]
    public async Task Accepted_clean_requests_all_complete_when_hints_overflow_an_occupied_worker()
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        CanonicalCampaignContext[] campaigns = await h.CreateCampaignsAsync();

        Guid first = await h.RequestAsync(CanonicalCampaignContext.GlobalOnly);

        TaskCompletionSource entered = NewSignal();

        TaskCompletionSource release = NewSignal();

        int paused = 0;

        h.F.Handler.InsideSend = async () =>
        {
            h.SetProviderAnswer();

            if (Interlocked.Exchange(ref paused, 1) == 0)
            {
                entered.TrySetResult();

                await release.Task;
            }
        };

        try
        {
            await h.StartAsync();

            // Setup barrier: the first real coordinator is occupied in its actual HTTP transport.
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException("Setup failed to reach the real HTTP transport; this is not the scheduling-liveness RED.");
            }

            LongRunningOperation running = await h.ReadAsync(first);

            Assert.Equal(LongRunningOperationState.Running, running.State);

            Assert.Equal(1, running.AttemptCount);

            Assert.Equal(1, running.CheckpointVersion);

            Guid second = await h.RequestAsync(campaigns[0]);

            Guid third = await h.RequestAsync(campaigns[1]);

            Assert.Equal(3, new[] { first, second, third }.Distinct().Count());

            await h.AssertPendingAsync(second);

            await h.AssertPendingAsync(third);

            h.Expect(first, second, third);

            release.TrySetResult();

            await h.WaitAndStopAsync();

            await h.AssertCompletedAsync();
        }
        finally
        {
            release.TrySetResult();

            await h.StopAsync();
        }
    }

    [Fact]
    public async Task Fresh_worker_discovers_unsignalled_clean_pending_work_and_leaves_protected_identity_unclaimed()
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        Guid clean = await h.RequestAsync(CanonicalCampaignContext.GlobalOnly);

        Guid protectedOperation = LongRestSweepHarness.Require(await h.Coordinator.RequestAsync(
            h.F.Scope, h.F.Partition, CancellationToken.None)).Id;

        Assert.NotEqual(clean, protectedOperation);

        await h.AssertPendingAsync(clean);

        LongRunningOperation protectedBefore = await h.AssertPendingAsync(protectedOperation);

        var stored = LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(protectedOperation, CancellationToken.None));

        ArtifactSensitivityLabel label = LongRestSweepHarness.Require(await ArtifactSensitivityLedger.ReadLabelWithinAsync(
            h.F.H.Connection, null, SensitiveArtifactKind.Saga, Guid.Parse(h.F.Source), CancellationToken.None))!;

        Assert.NotNull(label);

        Assert.NotEqual(Guid.Empty, label.LabelId);

        Assert.Equal(ContentSensitivity.CovenantDerived, label.Sensitivity);

        Assert.False(label.Provenance.IsEmpty);

        Assert.Equal(h.F.Provenance, label.Provenance);

        Assert.Equal(label.SensitivityDigest, stored.Scope.ProtectedPartitionDigest);

        Assert.Equal(h.F.Partition, stored.Scope);

        long claimsBefore = await h.CountAsync("SELECT count(*) FROM session_turn_claims;");

        long receiptsBefore = await h.CountAsync("SELECT count(*) FROM external_disclosure_receipts;");

        Assert.Equal(0L, receiptsBefore);

        // Retain the database and valid runtime, but discard every volatile queue identity.
        h.RecreateWorker();

        h.Expect(clean);

        await h.StartAsync();

        await h.WaitAndStopAsync();

        LongRunningOperation protectedAfter = await h.ReadAsync(protectedOperation);

        Assert.Equal(protectedBefore.State, protectedAfter.State);

        Assert.Equal(0, protectedAfter.AttemptCount);

        Assert.Equal(0, protectedAfter.CheckpointVersion);

        Assert.Null(protectedAfter.CheckpointPayload);

        Assert.Null(protectedAfter.LeaseOwner);

        Assert.Equal(protectedBefore.Revision, protectedAfter.Revision);

        Assert.Equal(protectedBefore.IdempotencyClaimId, protectedAfter.IdempotencyClaimId);

        Assert.Equal(stored, LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(protectedOperation, CancellationToken.None)));

        Assert.Null(LongRestSweepHarness.Require(await h.F.H.Store.ReadGenerationMetadataAsync(protectedOperation, CancellationToken.None)));

        Assert.Equal(claimsBefore, await h.CountAsync("SELECT count(*) FROM session_turn_claims;"));

        Assert.Equal(receiptsBefore, await h.CountAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        await h.AssertCompletedAsync();
    }

    [Fact]
    public async Task Disabled_dispatch_writes_nothing_and_reenabled_request_survives_a_stale_queued_hint()
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        CanonicalCampaignContext[] campaigns = await h.CreateCampaignsAsync();

        Guid old = await h.RequestAsync(CanonicalCampaignContext.GlobalOnly);

        var disabled = LongRestConfigurationEpochTests.Settings();

        disabled.Features.LongRest = false;

        h.F.Options.Set(disabled);

        long writes = await h.CountAsync("SELECT total_changes();");

        await h.Service.RunOneAsync(old, CancellationToken.None);

        Assert.Equal(writes, await h.CountAsync("SELECT total_changes();"));

        Assert.Equal(0, h.ScopesCreated);

        Assert.Equal(0, h.F.Handler.SendCount);

        await h.AssertPendingAsync(old);

        h.F.Options.Set(LongRestConfigurationEpochTests.Settings());

        Guid fresh = await h.RequestAsync(campaigns[0]);

        Assert.NotEqual(old, fresh);

        await h.AssertPendingAsync(fresh);

        h.Expect(fresh);

        await h.StartAsync();

        await h.WaitAndStopAsync();

        await h.AssertCompletedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_worker_leaves_stale_configuration_pending_unchanged_and_completes_current_scope(bool previousBoot)
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        CanonicalCampaignContext[] campaigns = await h.CreateCampaignsAsync();

        Guid stale = await h.RequestAsync(CanonicalCampaignContext.GlobalOnly);

        await h.AssertPendingAsync(stale);

        LongRestConfigurationStamp oldStamp = h.F.Epoch.Capture().Stamp;

        if (previousBoot)
        {
            await h.F.RebootAsync();

            // RebootAsync expires every fixture lease. This never-leased request must retain its real null deadline.
            await h.F.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = NULL WHERE Id = $id;",
                ("$id", stale.ToString("N")));

            Assert.NotEqual(oldStamp.BootId, h.F.Epoch.Capture().Stamp.BootId);
        }
        else
        {
            var disabled = LongRestConfigurationEpochTests.Settings();

            disabled.Features.LongRest = false;

            h.F.Options.Set(disabled);

            h.F.Options.Set(LongRestConfigurationEpochTests.Settings());

            Assert.Equal(oldStamp.BootId, h.F.Epoch.Capture().Stamp.BootId);

            Assert.NotEqual(oldStamp.Epoch, h.F.Epoch.Capture().Stamp.Epoch);
        }

        Assert.Equal(oldStamp.ValueDigest, h.F.Epoch.Capture().Stamp.ValueDigest);

        h.RecreateWorker();

        Guid current = await h.RequestAsync(campaigns[0]);

        LongRunningOperation before = await h.AssertPendingAsync(stale);

        Assert.Null(before.LeaseExpiresAt);

        var storedBefore = LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(stale, CancellationToken.None));

        Assert.Equal(oldStamp, storedBefore.Configuration);

        Assert.Null(storedBefore.Scope.ProtectedPartitionDigest);

        // Drop both original and current volatile hints; discovery must discriminate persisted stamps.
        h.RecreateWorker();

        h.Expect(current);

        await h.StartAsync();

        await h.WaitAndStopAsync();

        Assert.Equal(before, await h.ReadAsync(stale));

        Assert.Equal(storedBefore, LongRestSweepHarness.Require(await h.Facts.ReadScopeAsync(stale, CancellationToken.None)));

        Assert.Null(LongRestSweepHarness.Require(await h.F.H.Store.ReadGenerationMetadataAsync(stale, CancellationToken.None)));

        await h.AssertCompletedAsync();
    }

    [Fact]
    public async Task Real_ownership_refusal_of_first_pending_identity_does_not_starve_the_later_identity()
    {
        await using SchedulingHarness h = await SchedulingHarness.CreateAsync();

        CanonicalCampaignContext[] campaigns = await h.CreateCampaignsAsync();

        Guid first = await h.RequestAsync(campaigns[0]);

        Guid second = await h.RequestAsync(campaigns[1]);

        Guid[] ordered = new[] { first, second }.OrderBy(id => id.ToString("N"), StringComparer.Ordinal).ToArray();

        h.RecreateWorker();

        Assert.True(h.Ownership.TryClaim(ordered[0], out Guid held));

        LongRunningOperation refusedBefore = await h.AssertPendingAsync(ordered[0]);

        try
        {
            h.Expect(ordered[1]);

            await h.StartAsync();

            await h.WaitAndStopAsync();

            Assert.True(h.Ownership.IsClaimedBy(ordered[0], held));

            Assert.Equal(refusedBefore, await h.ReadAsync(ordered[0]));

            Assert.Null(LongRestSweepHarness.Require(await h.F.H.Store.ReadGenerationMetadataAsync(ordered[0], CancellationToken.None)));

            await h.AssertCompletedAsync();
        }
        finally
        {
            Assert.True(h.Ownership.Release(ordered[0], held));
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class SchedulingHarness : IAsyncDisposable
    {
        internal LongRestMaintenanceTests.MaintenanceFixture F { get; }

        internal LongRestLiveFenceReader Facts { get; private set; } = null!;

        private LongRestSweepStore Sweeps { get; set; } = null!;

        internal LongRunningOperationOwnership Ownership { get; } = new();

        private LongRestSweepQueue Queue { get; set; } = null!;

        internal LongRestSweepCoordinator Coordinator { get; private set; } = null!;

        internal LongRestSweepHostedService Service { get; private set; } = null!;

        private WorkerLog Log { get; } = new();

        private TaskCompletionSource Completed { get; } = NewSignal();

        private Guid[] Expected { get; set; } = [];

        private Exception? ObservationError { get; set; }

        internal int ScopesCreated;

        private bool Started;

        private bool CompletionObserved;

        private SchedulingHarness(LongRestMaintenanceTests.MaintenanceFixture fixture)
        {
            F = fixture;

            RecreateWorker();

            F.Handler.InsideSend = () => { SetProviderAnswer(); return Task.CompletedTask; };
        }

        internal static async Task<SchedulingHarness> CreateAsync()
        {
            var fixture = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

            try
            {
                // Remove only the fixture's synthetic running operation and its cascading sweep/generation.
                // The real protected source labels remain; every operation under test is admitted by RequestAsync.
                await fixture.H.ExecuteAsync("DELETE FROM LongRunningOperations WHERE Id = $id;",
                    ("$id", fixture.Operation.ToString("N")));

                await fixture.H.SeedAsync(Guid.NewGuid().ToString("D"), "Clean first observation.");

                await fixture.H.SeedAsync(Guid.NewGuid().ToString("D"), "Clean second observation.");

                return new(fixture);
            }
            catch
            {
                await fixture.DisposeAsync();

                throw;
            }
        }

        internal async Task<CanonicalCampaignContext[]> CreateCampaignsAsync()
        {
            Guid[] ids = [CovenantOperationGateFixture.CampaignOne, CovenantOperationGateFixture.CampaignTwo];

            foreach (Guid id in ids) await F.H.SessionAsync(id);

            long registry = await CountAsync("SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;");

            return ids.Select(id => CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(id),
                registry, 1, null, null)).ToArray();
        }

        internal void SetProviderAnswer()
        {
            F.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(F.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(F.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";
        }

        internal void RecreateWorker()
        {
            Assert.False(Started);

            Service?.Dispose();

            FixedCovenantConnectionSource connections = new(F.H.Connection);

            Facts = new(F.H.Saga.Context, F.Runtime, F.Admission, F.Epoch,
                new CampaignAvailabilityReader(connections), new CampaignPathIdentityReader(connections, null!));

            Sweeps = new(F.H.Saga.Context, F.Clock);

            Queue = new();

            Coordinator = new(F.Operations, Sweeps, F.H.Store, F.Epoch, Facts, F.Maintenance, F.Gate,
                Ownership, Queue, F.Services.GetRequiredService<IServiceScopeFactory>(), F.Clock, F.RecordedAdmission);

            Service = new(new BorrowedScopes(this), Queue, F.Epoch, F.Admission, F.Clock, Log);
        }

        internal async Task<Guid> RequestAsync(CanonicalCampaignContext scope) =>
            LongRestSweepHarness.Require(await Coordinator.RequestAsync(scope, CancellationToken.None)).Id;

        internal async Task<LongRunningOperation> ReadAsync(Guid operation)
        {
            LongRunningOperation? row = await F.Operations.GetAsync(operation);

            Assert.NotNull(row);

            return row!;
        }

        internal async Task<LongRunningOperation> AssertPendingAsync(Guid operation)
        {
            LongRunningOperation row = await ReadAsync(operation);

            Assert.Equal(LongRunningOperationState.Pending, row.State);

            Assert.Equal(0, row.AttemptCount);

            Assert.Equal(0, row.CheckpointVersion);

            Assert.Null(row.CheckpointPayload);

            Assert.Null(row.LeaseOwner);

            return row;
        }

        internal async Task<long> CountAsync(string sql) => (long)(await F.H.ScalarAsync(sql))!;

        internal void Expect(params Guid[] operations) => Expected = operations;

        internal async Task StartAsync(IHostedService? hosted = null)
        {
            Started = true;

            await (hosted ?? Service).StartAsync(CancellationToken.None);
        }

        internal async Task StopAsync()
        {
            if (!Started) return;

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

            await Service.StopAsync(deadline.Token);

            if (Service.ExecuteTask is { } execution) await execution.WaitAsync(deadline.Token);

            Started = false;
        }

        internal async Task WaitAndStopAsync()
        {
            try
            {
                await Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

                CompletionObserved = true;
            }
            catch (TimeoutException)
            {
                // A watchdog only: no sleeps/polling or scheduler timing decides the test arrangement.
            }
            finally
            {
                await StopAsync();
            }

            Assert.True(ObservationError is null, $"Fixture observation failed: {ObservationError}");

            Assert.True(Log.Errors.IsEmpty, $"Worker error before liveness assertion: {string.Join("; ", Log.Errors)}");
        }

        internal async Task AssertCompletedAsync()
        {
            foreach (Guid operation in Expected)
            {
                LongRunningOperation row = await ReadAsync(operation);

                Assert.True(row.State == LongRunningOperationState.Completed,
                    $"Accepted clean operation {operation} did not complete; durable state={row.State}, attempts={row.AttemptCount}, " +
                    $"checkpoint={row.CheckpointVersion}, error={row.TerminalErrorCode ?? "(none)"}, watchdogExpired={!CompletionObserved}.");

                Assert.Equal(1, row.AttemptCount);

                Assert.Equal(1, row.CheckpointVersion);

                Assert.NotNull(row.CheckpointPayload);

                Assert.Null(row.LeaseOwner);
            }

            Assert.True(CompletionObserved, "Rows completed, but the test scope-disposal observer did not signal.");
        }

        private async ValueTask ObserveAsync()
        {
            try
            {
                if (Expected.Length == 0) return;

                foreach (Guid operation in Expected)
                {
                    if ((await ReadAsync(operation)).State != LongRunningOperationState.Completed) return;
                }

                Completed.TrySetResult();
            }
            catch (Exception error)
            {
                ObservationError = error;

                Completed.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();

            Service.Dispose();

            await F.DisposeAsync();
        }

        // These scopes borrow real objects; the fixture retains lifetime ownership. No coordinator/store is stubbed.
        private sealed class BorrowedScopes(SchedulingHarness owner) : IServiceScopeFactory
        {
            public IServiceScope CreateScope()
            {
                Interlocked.Increment(ref owner.ScopesCreated);

                return new BorrowedScope(owner);
            }
        }

        private sealed class BorrowedScope(SchedulingHarness owner) : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type type)
            {
                if (type == typeof(ILongRestAdmittedSweep)) return owner.Coordinator;

                if (type == typeof(ILongRunningOperationStore)) return owner.F.Operations;

                if (type == typeof(ILongRestSweepStore)) return owner.Sweeps;

                if (type == typeof(ILongRestDiscoveryStore)) return owner.F.H.Store;

                if (type == typeof(ILongRestConfigurationProvider)) return owner.F.Epoch;

                if (type == typeof(LongRestLiveFenceReader)) return owner.Facts;

                return null;
            }

            public void Dispose() => throw new InvalidOperationException("Async scope disposal is required.");

            public ValueTask DisposeAsync() => owner.ObserveAsync();
        }

        private sealed class WorkerLog : ILoggerFactory, ILogger
        {
            internal ConcurrentQueue<string> Errors { get; } = new();

            public ILogger CreateLogger(string categoryName) => this;

            public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

            public void Dispose() { }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error) Errors.Enqueue(formatter(state, exception));
            }
        }
    }
}
