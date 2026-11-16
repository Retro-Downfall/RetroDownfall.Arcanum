using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Refused_initial_observation_requires_cadence_before_recovery_retry()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("DROP TABLE guild_quests");

        using TracedIdleConnections traced = new(f.Connections);

        await using IdleScopeHarness scope = new(f, traced);

        Task attempt = scope.Run();

        await attempt.WaitAsync(TimeSpan.FromSeconds(5));

        object disposition = attempt.GetType().GetProperty("Result")!.GetValue(attempt)!;

        Assert.False((bool)disposition.GetType().GetProperty("RestartImmediately")!.GetValue(disposition)!);

        Assert.Equal(0, f.Connections.LiveFreshLeaseCount);
    }

    [Fact]
    public async Task Unrelated_observer_open_cancellation_propagates_and_releases_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        BlockingObserverConnections connections = new(f.Connections, unexpectedCancellation: true);

        await using IdleScopeHarness scope = new(f, connections);

        Task attempt = scope.Run();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);

        var started = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(60));

        Assert.True(started.IsSuccess, started.Error.Message);

        await using IGrimoireClosingOwner closing = started.Value;

        Result drained = await f.Gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(drained.IsSuccess, drained.Error.Message);
    }

    [Fact]
    public async Task Maintenance_during_observer_open_is_refused_and_releases_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        BlockingObserverConnections connections = new(f.Connections);

        await using IdleScopeHarness scope = new(f, connections);

        Task attempt = scope.Run();

        await connections.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var started = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(59));

        Assert.True(started.IsSuccess, started.Error.Message);

        await using IGrimoireClosingOwner closing = started.Value;

        await attempt.WaitAsync(TimeSpan.FromSeconds(5));

        Result drained = await f.Gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(drained.IsSuccess, drained.Error.Message);
    }

    private sealed class IdleScopeHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private readonly GuildLocalQuestScheduler _scheduler;

        private readonly Fixture _fixture;

        private readonly ManualTimerTimeProvider _time = new();

        private readonly PeriodicTimer _cadence;

        private readonly CancellationTokenSource _stop = new();

        private Task? _attempt;

        internal IdleScopeHarness(Fixture fixture, IGrimoireOrdinaryConnectionFactory connections)
        {
            _fixture = fixture;

            fixture.Settings.CurrentValue.Features.Guild = false;

            _services = IdleHistoryServices(fixture);

            _scheduler = new(_services.GetRequiredService<IServiceScopeFactory>(), fixture.Settings, connections,
                new LongRunningOperationOwnership(), _time, NullLogger<GuildLocalQuestScheduler>.Instance,
                new SchedulerReadiness(false), fixture.Gate);

            _cadence = new(TimeSpan.FromSeconds(1), _time);
        }

        internal Task Run()
        {
            var method = typeof(GuildLocalQuestScheduler).GetMethod("ObserveIdleAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

            return _attempt = (Task)method.Invoke(_scheduler, [_cadence, _fixture.Settings.CurrentValue, 0L, _fixture.Gate.CurrentGeneration, _stop.Token])!;
        }

        internal void Advance() => _time.Advance(TimeSpan.FromSeconds(1));

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();

            if (_attempt is { IsCompleted: false })
            {
                try { await _attempt; }
                catch (OperationCanceledException) { }
            }

            _cadence.Dispose();

            _stop.Dispose();

            _scheduler.Dispose();

            await _services.DisposeAsync();
        }
    }

    private sealed class BlockingObserverConnections(FixtureOrdinaryConnectionFactory inner, bool unexpectedCancellation = false) : IGrimoireOrdinaryConnectionFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) => inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();

            if (unexpectedCancellation) throw new OperationCanceledException();

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return await inner.OpenFreshAsync(kind, cancellationToken);
        }
    }

    [Fact]
    public async Task Observer_disposal_fault_stops_hosted_siblings_and_releases_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        RevokingIdleOptions options = new(f.Settings);

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), options,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), f.Gate);

        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(10));

        await scheduler.StartAsync(default);

        try
        {
            await traced.WaitForInitialProofAsync(watchdog.Token);

            traced.AfterNextDispose(() => throw new InvalidOperationException("Synthetic observer disposal fault."));

            options.OnNextRead(() => throw new InvalidOperationException("Synthetic options fault."));

            cadence.Advance(TimeSpan.FromSeconds(1));

            await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.ExecuteTask!.WaitAsync(watchdog.Token));

            Assert.Equal(0, f.Connections.LiveFreshLeaseCount);

            var started = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(58));

            Assert.True(started.IsSuccess, started.Error.Message);

            await using IGrimoireClosingOwner closing = started.Value;

            Result drained = await f.Gate.DrainRequestAndWorkAsync(closing, watchdog.Token);

            Assert.True(drained.IsSuccess, drained.Error.Message);
        }
        finally
        {
            await scheduler.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Observer_revoked_during_retirement_waits_for_reopen_without_polling()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        RevokingIdleOptions options = new(f.Settings);

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        RecordingGrimoireWorkAdmissionGate admission = new(f.Gate);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), options,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), admission);

        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(5));

        IGrimoireClosingOwner? closing = null;

        TaskCompletionSource revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.StartAsync(default);

        try
        {
            await traced.WaitForInitialProofAsync(watchdog.Token);

            traced.OnNextDispose(() =>
            {
                var started = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(57));

                Assert.True(started.IsSuccess, started.Error.Message);

                closing = started.Value;

                revoked.TrySetResult();
            });

            options.OnNextRead(() => new ArcanumSettings { Features = new() { Guild = false } });

            cadence.Advance(TimeSpan.FromSeconds(1));

            await revoked.Task.WaitAsync(watchdog.Token);

            await WaitForIdleLifecycleGenerationWaitAsync(admission, watchdog.Token);

            Assert.Equal(0, f.Connections.LiveFreshLeaseCount);
        }
        finally
        {
            await scheduler.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

            if (closing is not null) await closing.DisposeAsync();
        }
    }

    [Fact]
    public async Task Observer_revoked_during_currentness_check_waits_for_reopen_without_polling()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        RevokingIdleOptions options = new(f.Settings);

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        RecordingGrimoireWorkAdmissionGate admission = new(f.Gate);

        await using ServiceProvider services = IdleHistoryServices(f);

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), options,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), admission);

        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(5));

        IGrimoireClosingOwner? closing = null;

        TaskCompletionSource revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.StartAsync(default);

        try
        {
            await traced.WaitForInitialProofAsync(watchdog.Token);

            options.OnNextRead(() =>
            {
                var started = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(56));

                Assert.True(started.IsSuccess, started.Error.Message);

                closing = started.Value;

                revoked.TrySetResult();

                return new ArcanumSettings { Features = new() { Guild = false } };
            });

            cadence.Advance(TimeSpan.FromSeconds(1));

            await revoked.Task.WaitAsync(watchdog.Token);

            await WaitForIdleLifecycleGenerationWaitAsync(admission, watchdog.Token);

            Assert.NotNull(closing);

            Assert.Equal(0, f.Connections.LiveFreshLeaseCount);
        }
        finally
        {
            await scheduler.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

            if (closing is not null) await closing.DisposeAsync();
        }
    }

    private sealed class RevokingIdleOptions(IOptionsMonitor<ArcanumSettings> inner) : IOptionsMonitor<ArcanumSettings>
    {
        private Func<ArcanumSettings>? _next;

        internal void OnNextRead(Func<ArcanumSettings> action) => Volatile.Write(ref _next, action);

        public ArcanumSettings CurrentValue => Interlocked.Exchange(ref _next, null)?.Invoke() ?? inner.CurrentValue;

        public ArcanumSettings Get(string? name) => inner.Get(name);

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => inner.OnChange(listener);
    }

    [Fact]
    public async Task Failed_observer_construction_releases_work_even_when_connection_disposal_throws()
    {
        await using Fixture f = await Fixture.CreateAsync();

        ClosedFaultingObserverConnections connections = new(f.Connections);

        await using IdleScopeHarness scope = new(f, connections);

        Task attempt = scope.Run();

        await Assert.ThrowsAsync<InvalidOperationException>(() => attempt);

        Assert.True(connections.Disposed);

        var started = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(55));

        Assert.True(started.IsSuccess, started.Error.Message);

        await using IGrimoireClosingOwner closing = started.Value;

        Result drained = await f.Gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(drained.IsSuccess, drained.Error.Message);
    }

    private sealed class ClosedFaultingObserverConnections(FixtureOrdinaryConnectionFactory inner) : IGrimoireOrdinaryConnectionFactory
    {
        internal bool Disposed { get; private set; }

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) => inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            Result<IGrimoireOrdinaryConnectionLease> opened = await inner.OpenFreshAsync(kind, cancellationToken);

            if (opened.IsFailure) return opened.Error;

            await opened.Value.Connection.CloseAsync();

            return new FaultingLease(opened.Value, this);
        }

        private sealed class FaultingLease(IGrimoireOrdinaryConnectionLease inner, ClosedFaultingObserverConnections owner) : IGrimoireOrdinaryConnectionLease
        {
            public SqliteConnection Connection => inner.Connection;

            public void Dispose() => throw new NotSupportedException();

            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();

                owner.Disposed = true;

                throw new InvalidOperationException("Synthetic observer disposal fault.");
            }
        }
    }

    [Fact]
    public async Task Maintenance_interrupts_real_busy_observer_without_waiting_for_provider_timeout()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection writer = await f.OpenAsync();

        await using SqliteCommand command = writer.CreateCommand();

        command.CommandText = "PRAGMA journal_mode=DELETE";

        Assert.Equal("delete", (await command.ExecuteScalarAsync())?.ToString(), ignoreCase: true);

        SqliteConnectionStringBuilder probeSettings = new(writer.ConnectionString) { Mode = SqliteOpenMode.ReadOnly };

        await using SqliteConnection probe = new(probeSettings.ToString());

        await probe.OpenAsync();

        probe.DefaultTimeout = 1;

        using TracedIdleConnections traced = new(f.Connections);

        await using IdleScopeHarness scope = new(f, traced);

        Task attempt = scope.Run();

        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(10));

        await traced.WaitForInitialProofAsync(watchdog.Token);

        command.CommandText = $"INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES('{Guid.NewGuid().ToString("D").ToUpperInvariant()}','2026-11-16T00:00:00Z','2026-11-16T00:00:00Z')";

        await command.ExecuteNonQueryAsync();

        command.CommandText = "BEGIN EXCLUSIVE";

        await command.ExecuteNonQueryAsync();

        Task? tick = null;

        try
        {
            await using SqliteCommand lockedRead = probe.CreateCommand();

            lockedRead.CommandText = "SELECT count(*) FROM guild_quests";

            SqliteException busy = Assert.Throws<SqliteException>(() => lockedRead.ExecuteScalar());

            Assert.Contains(busy.SqliteErrorCode, new[] { 5, 6 });

            tick = Task.Run(scope.Advance);

            using CancellationTokenSource busyWatchdog = new(TimeSpan.FromSeconds(5));

            await traced.WaitForNativeBusyAsync(busyWatchdog.Token);

            Assert.Equal(1, f.Connections.LiveFreshLeaseCount);

            var started = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(54));

            Assert.True(started.IsSuccess, started.Error.Message);

            await using IGrimoireClosingOwner closing = started.Value;

            await attempt.WaitAsync(TimeSpan.FromSeconds(5));

            Result drained = await f.Gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(drained.IsSuccess, drained.Error.Message);

            Assert.Equal(0, f.Connections.LiveFreshLeaseCount);
        }
        finally
        {
            command.CommandText = "ROLLBACK";

            await command.ExecuteNonQueryAsync();

            if (tick is not null) await tick;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_settled_scheduler_reuses_its_observer_across_twenty_acknowledged_ticks(bool activeVm)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Guild = false;

        ManualTimerTimeProvider cadence = new();

        using TracedIdleConnections traced = new(f.Connections);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store,
            new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        ServiceCollection registration = new();

        registration.AddSingleton(f.Store);

        registration.AddSingleton(f.Policy);

        registration.AddSingleton(cohorts);

        await using ServiceProvider services = registration.BuildServiceProvider();

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            traced, new LongRunningOperationOwnership(), cadence, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), f.Gate);

        await scheduler.StartAsync(default);

        Task? activeTick = null;

        try
        {
            using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

            await cadence.FirstTimerCreated.WaitAsync(watchdog.Token);

            await traced.WaitForInitialProofAsync(watchdog.Token);

            int opens = f.Connections.Kinds.Count;

            for (int tick = 0; tick < 20; tick++)
            {
                long before = traced.CompletedBoundaryStatements;

                cadence.Advance(TimeSpan.FromSeconds(1));

                await traced.WaitForBoundaryAfterAsync(before, watchdog.Token);

                Assert.Equal(opens, f.Connections.Kinds.Count);

                Assert.Equal(1, traced.LastBoundaryAutocommit);
            }

            for (int tick = 0; tick < 5; tick++)
            {
                long before = traced.CompletedStamps;

                await f.ExecuteAsync($"INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES('{Guid.NewGuid().ToString("D").ToUpperInvariant()}','2026-11-16T00:00:00Z','2026-11-16T00:00:00Z')");

                cadence.Advance(TimeSpan.FromSeconds(1));

                // Changed stamp, pre-census stamp and post-census stamp all complete on the
                // retained handle before this renewal is acknowledged.
                await traced.WaitForStampCountAsync(before + 3, watchdog.Token);

                Assert.Equal(opens, f.Connections.Kinds.Count);

                Assert.Equal(1, traced.LastBoundaryAutocommit);
            }

            if (activeVm)
            {
                traced.ArmActiveVm();

                await f.ExecuteAsync($"INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES('{Guid.NewGuid().ToString("D").ToUpperInvariant()}','2026-11-16T00:00:00Z','2026-11-16T00:00:00Z')");

                activeTick = Task.Run(() => cadence.Advance(TimeSpan.FromSeconds(1)));

                await traced.ActiveVmEntered.WaitAsync(watchdog.Token);

                Assert.Equal(1, f.Connections.LiveFreshLeaseCount);
            }

            var started = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(53));

            Assert.True(started.IsSuccess, started.Error.Message);

            await using IGrimoireClosingOwner closing = started.Value;

            Task<Result> drain = f.Gate.DrainRequestAndWorkAsync(closing, watchdog.Token).AsTask();

            if (activeVm) Assert.False(drain.IsCompleted);

            traced.ReleaseActiveVm();

            if (activeTick is not null) await activeTick.WaitAsync(watchdog.Token);

            Result drained = await drain.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(drained.IsSuccess, drained.Error.Message);

            Assert.Equal(0, f.Connections.LiveFreshLeaseCount);

            if (activeVm) Assert.Equal(1, traced.InterruptObservedDuringVm);
        }
        finally
        {
            traced.ReleaseActiveVm();

            if (activeTick is not null) await activeTick;

            await scheduler.StopAsync(default);
        }
    }

    private sealed class TracedIdleConnections(FixtureOrdinaryConnectionFactory inner) : IGrimoireOrdinaryConnectionFactory, IDisposable
    {
        private Action? _nextDispose;

        private Action? _afterDispose;

        internal void OnNextDispose(Action action) => Volatile.Write(ref _nextDispose, action);

        internal void AfterNextDispose(Action action) => Volatile.Write(ref _afterDispose, action);
        private readonly TaskCompletionSource _activeVmEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ManualResetEventSlim _activeVmRelease = new();

        private int _activeVmArmed;

        private int _interruptObservedDuringVm;

        private readonly object _observerLock = new();

        private SQLitePCL.sqlite3? _observerHandle;
        private long _completedBoundaryStatements;

        private long _completedStamps;

        private long _lastStampDataVersion;

        private int _lastBoundaryAutocommit;

        internal long CompletedBoundaryStatements => Interlocked.Read(ref _completedBoundaryStatements);

        internal long CompletedStamps => Interlocked.Read(ref _completedStamps);

        internal long LastStampDataVersion => Interlocked.Read(ref _lastStampDataVersion);

        internal int LastBoundaryAutocommit => Volatile.Read(ref _lastBoundaryAutocommit);

        internal Task ActiveVmEntered => _activeVmEntered.Task;

        internal int InterruptObservedDuringVm => Volatile.Read(ref _interruptObservedDuringVm);

        internal int? ObserverErrorCode()
        {
            lock (_observerLock)
                return _observerHandle is { } handle ? SQLitePCL.raw.sqlite3_errcode(handle) : null;
        }

        private void RememberObserver(SQLitePCL.sqlite3 handle)
        {
            lock (_observerLock) _observerHandle = handle;
        }

        private void ForgetObserver(SQLitePCL.sqlite3 handle)
        {
            lock (_observerLock)
                if (ReferenceEquals(_observerHandle, handle)) _observerHandle = null;
        }

        internal void ArmActiveVm() => Volatile.Write(ref _activeVmArmed, 1);

        internal void ReleaseActiveVm() => _activeVmRelease.Set();

        public void Dispose() => _activeVmRelease.Dispose();

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) => inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            Result<IGrimoireOrdinaryConnectionLease> opened = await inner.OpenFreshAsync(kind, cancellationToken);

            return opened.IsFailure ? opened.Error : new TracedLease(opened.Value, this);
        }

        internal async Task WaitForBoundaryAfterAsync(long before, CancellationToken ct)
        {
            while (CompletedBoundaryStatements <= before) await Task.Delay(10, ct);
        }

        internal async Task WaitForNativeBusyAsync(CancellationToken ct)
        {
            while (true)
            {
                // sqlite3_errcode may itself wait on the connection mutex while a native
                // statement is busy. Keep the watchdog independent of that native call;
                // the lock in ObserverErrorCode still joins it before handle disposal.
                int? code = await Task.Run(ObserverErrorCode).WaitAsync(ct);

                if (code is 5 or 6) return;

                if (code is null) throw new InvalidOperationException("The observer closed before its native busy state was observed.");

                await Task.Delay(1, ct);
            }
        }

        internal async Task WaitForInitialProofAsync(CancellationToken ct)
        {
            await WaitForStampCountAsync(2, ct);
        }

        internal async Task WaitForStampCountAsync(long count, CancellationToken ct)
        {
            while (CompletedStamps < count) await Task.Delay(10, ct);
        }

        private sealed class TracedLease : IGrimoireOrdinaryConnectionLease
        {
            private readonly IGrimoireOrdinaryConnectionLease _inner;

            private readonly TracedIdleConnections _owner;

            private readonly TraceCallback _callback;

            private readonly ProgressCallback _progress;

            private int _inCensus;

            internal TracedLease(IGrimoireOrdinaryConnectionLease inner, TracedIdleConnections owner)
            {
                _inner = inner;

                _owner = owner;

                _callback = (kind, _, statement, _) =>
                {
                    string sql = Marshal.PtrToStringUTF8(StatementSql(statement)) ?? string.Empty;

                    if (kind == 1 && sql.StartsWith("SELECT CASE WHEN", StringComparison.Ordinal))
                        Volatile.Write(ref _inCensus, 1);

                    if (kind == 4 && sql.Contains("pragma_data_version", StringComparison.OrdinalIgnoreCase))
                        Interlocked.Exchange(ref owner._lastStampDataVersion, ColumnInt64(statement, 1));

                    if (kind == 2)
                    {
                        if (sql.StartsWith("SELECT CASE WHEN", StringComparison.Ordinal)) Volatile.Write(ref _inCensus, 0);

                        if (sql.Contains("pragma_data_version", StringComparison.OrdinalIgnoreCase)
                            || sql.StartsWith("SELECT Id FROM (", StringComparison.Ordinal))
                        {
                            Volatile.Write(ref owner._lastBoundaryAutocommit, SQLitePCL.raw.sqlite3_get_autocommit(Connection.Handle));

                            if (sql.Contains("pragma_data_version", StringComparison.OrdinalIgnoreCase))
                            {
                                owner.RememberObserver(Connection.Handle!);

                                Interlocked.Increment(ref owner._completedStamps);
                            }

                            Interlocked.Increment(ref owner._completedBoundaryStatements);
                        }
                    }

                    return 0;
                };

                _progress = _ =>
                {
                    if (Volatile.Read(ref _inCensus) == 1 && Interlocked.CompareExchange(ref owner._activeVmArmed, 0, 1) == 1)
                    {
                        owner._activeVmEntered.TrySetResult();

                        owner._activeVmRelease.Wait(TimeSpan.FromSeconds(30));

                        Volatile.Write(ref owner._interruptObservedDuringVm, IsInterruptedNative(Connection.Handle!.DangerousGetHandle()));
                    }

                    return 0;
                };

                Assert.Equal(0, TraceNative(Connection.Handle!.DangerousGetHandle(), 7, _callback, 0));

                ProgressNative(Connection.Handle.DangerousGetHandle(), 1, _progress, 0);
            }

            public SqliteConnection Connection => _inner.Connection;

            public void Dispose()
            {
                _owner.ForgetObserver(Connection.Handle!);

                ProgressNative(Connection.Handle!.DangerousGetHandle(), 0, null, 0);

                Assert.Equal(0, TraceNative(Connection.Handle!.DangerousGetHandle(), 0, null, 0));

                _inner.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref _owner._nextDispose, null)?.Invoke();

                _owner.ForgetObserver(Connection.Handle!);

                ProgressNative(Connection.Handle!.DangerousGetHandle(), 0, null, 0);

                Assert.Equal(0, TraceNative(Connection.Handle!.DangerousGetHandle(), 0, null, 0));

                await _inner.DisposeAsync();

                Interlocked.Exchange(ref _owner._afterDispose, null)?.Invoke();
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TraceCallback(uint kind, nint context, nint statement, nint data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ProgressCallback(nint context);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_trace_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int TraceNative(nint connection, uint mask, TraceCallback? callback, nint context);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_sql", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint StatementSql(nint statement);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
        private static extern long ColumnInt64(nint statement, int column);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_progress_handler", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ProgressNative(nint connection, int steps, ProgressCallback? callback, nint context);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_is_interrupted", CallingConvention = CallingConvention.Cdecl)]
        private static extern int IsInterruptedNative(nint connection);
    }
}
