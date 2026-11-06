using System.Collections.Concurrent;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildMaintenanceHostedServiceTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Maintenance_revocation_is_quiet_retains_work_through_scope_disposal_and_resumes_after_reopen()
    {
        ManualWallClockTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = new(clock);

        ControlledConnections connections = new();

        await using ServiceProvider provider = Services(gate, connections, clock);

        BlockingAsyncServiceScopeFactory scopes = new(provider);

        RecordingLogger logger = new();

        using GuildMaintenanceHostedService service = Service(scopes, gate, clock, logger);

        IGrimoireExclusiveClosedLease? closed = null;

        await service.StartAsync(CancellationToken.None);

        try
        {
            CancellationToken passToken = await connections.FirstOpened.Task.WaitAsync(BoundedWait);

            Task<IGrimoireExclusiveClosedLease> closing = PeriodicHostMaintenance.CloseAsync(gate);

            await scopes.DisposalReached.Task.WaitAsync(BoundedWait);

            Assert.True(passToken.IsCancellationRequested);

            Assert.False(closing.IsCompleted);

            scopes.AllowDisposal.TrySetResult();

            closed = await closing.WaitAsync(BoundedWait);

            await clock.WhenArmedTimersAsync(static count => count == 1, BoundedWait);

            Assert.Empty(logger.Entries);

            Assert.Equal(TimeSpan.FromMinutes(1), clock.NextDueIn);

            await PeriodicHostMaintenance.ReopenAsync(closed);

            closed = null;

            clock.Advance(TimeSpan.FromMinutes(1));

            CancellationToken resumedToken = await connections.SecondOpened.Task.WaitAsync(BoundedWait);

            Assert.False(resumedToken.IsCancellationRequested);

            Assert.Equal(2, scopes.Created);
        }
        finally
        {
            scopes.AllowDisposal.TrySetResult();

            await service.StopAsync(CancellationToken.None).WaitAsync(BoundedWait);

            if (closed is not null)
            {
                await PeriodicHostMaintenance.ReopenAsync(closed);
            }
        }

        Assert.Empty(logger.Entries);

        Assert.Equal(0, clock.ArmedTimers);
    }

    [Fact]
    public async Task Closed_admission_is_quiet_creates_no_scope_and_resumes_after_reopen()
    {
        ManualWallClockTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = new(clock);

        ControlledConnections connections = new();

        await using ServiceProvider provider = Services(gate, connections, clock);

        CountingServiceScopeFactory scopes = new(provider.GetRequiredService<IServiceScopeFactory>());

        RecordingLogger logger = new();

        using GuildMaintenanceHostedService service = Service(scopes, gate, clock, logger);

        IGrimoireExclusiveClosedLease? closed = await PeriodicHostMaintenance.CloseAsync(gate);

        await service.StartAsync(CancellationToken.None);

        try
        {
            await clock.WhenArmedTimersAsync(static count => count == 1, BoundedWait);

            Assert.Equal(0, scopes.Created);

            Assert.False(connections.FirstOpened.Task.IsCompleted);

            Assert.Empty(logger.Entries);

            Assert.Equal(TimeSpan.FromMinutes(1), clock.NextDueIn);

            await PeriodicHostMaintenance.ReopenAsync(closed);

            closed = null;

            clock.Advance(TimeSpan.FromMinutes(1));

            CancellationToken resumedToken = await connections.FirstOpened.Task.WaitAsync(BoundedWait);

            Assert.False(resumedToken.IsCancellationRequested);

            Assert.Equal(1, scopes.Created);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(BoundedWait);

            if (closed is not null)
            {
                await PeriodicHostMaintenance.ReopenAsync(closed);
            }
        }

        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("work")]
    [InlineData("effect")]
    public async Task Coordinator_admission_refusal_is_a_typed_maintenance_deferral_before_connection_access(string frontier)
    {
        ManualWallClockTimeProvider clock = new();

        GrimoireConnectionAdmissionGate inner = new(clock);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        ControlledConnections connections = new();

        IGrimoireClosingOwner? closing = null;

        void BeginClosing()
        {
            Result<IGrimoireClosingOwner> begun = inner.BeginOrResumeExclusive(PeriodicHostMaintenance.Owner());

            Assert.True(begun.IsSuccess, begun.Error.Message);

            closing = begun.Value;
        }

        if (frontier == "work")
        {
            BeginClosing();
        }
        else
        {
            gate.BeforeEffectGroupAdmission = BeginClosing;
        }

        await using ServiceProvider provider = Services(gate, connections, clock);

        try
        {
            Result<GuildCleanupOutcome> outcome = await provider.GetRequiredService<GuildOwnerCleanupCoordinator>()
                .RunBatchAsync(64, 64, CancellationToken.None);

            Assert.True(outcome.IsFailure);

            Assert.Equal(ErrorCodes.Guild.MaintenanceDeferred, outcome.Error.Code);

            Assert.False(connections.FirstOpened.Task.IsCompleted);
        }
        finally
        {
            if (closing is not null)
            {
                await using IGrimoireClosingOwner held = closing;

                Result drained = await inner.DrainRequestAndWorkAsync(held, CancellationToken.None).AsTask().WaitAsync(BoundedWait);

                Assert.True(drained.IsSuccess, drained.Error.Message);

                Result<IGrimoireExclusiveClosedLease> closed = await inner.CloseConnectionAdmissionAsync(held, CancellationToken.None);

                Assert.True(closed.IsSuccess, closed.Error.Message);

                await PeriodicHostMaintenance.ReopenAsync(closed.Value);
            }
        }
    }

    [Theory]
    [InlineData("unrelated-cancellation", "Guild owner cleanup deferred after TaskCanceledException.")]
    [InlineData("unavailable-storage", "Guild owner cleanup deferred: Guild.Unavailable.")]
    public async Task Failures_without_caller_or_maintenance_cancellation_still_warn(string failure, string expectedMessage)
    {
        ManualWallClockTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = new(clock);

        ControlledConnections connections = new(failure);

        await using ServiceProvider provider = Services(gate, connections, clock);

        RecordingLogger logger = new();

        using GuildMaintenanceHostedService service = Service(provider.GetRequiredService<IServiceScopeFactory>(), gate, clock, logger);

        await service.StartAsync(CancellationToken.None);

        try
        {
            CancellationToken passToken = await connections.FirstOpened.Task.WaitAsync(BoundedWait);

            await clock.WhenArmedTimersAsync(static count => count == 1, BoundedWait);

            Assert.False(passToken.IsCancellationRequested);

            Assert.Equal((LogLevel.Warning, expectedMessage), Assert.Single(logger.Entries));

            Assert.Equal(TimeSpan.FromMinutes(1), clock.NextDueIn);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(BoundedWait);
        }
    }

    private static ServiceProvider Services(
        IGrimoireConnectionAdmissionGate gate,
        IGrimoireOrdinaryConnectionFactory connections,
        TimeProvider clock)
    {
        ServiceCollection services = [];

        services.AddSingleton<IOperatorAuthorityContextIssuer>(new OperatorAuthorityContextIssuer(new UnavailableAuthority()));

        services.AddSingleton(new GuildOwnerCleanupCoordinator(
            connections,
            gate,
            new RefusingSensitivePurger(new Error(ErrorCodes.Covenant.Unavailable, "No purge is authorized in this fixture.")),
            new UnreadableLabeledArtifactGuard(),
            [],
            clock));

        return services.BuildServiceProvider();
    }

    private static GuildMaintenanceHostedService Service(
        IServiceScopeFactory scopes,
        IGrimoireConnectionAdmissionGate gate,
        TimeProvider clock,
        ILogger<GuildMaintenanceHostedService> logger)
    {
        GrimoireDbReadiness readiness = new();

        readiness.MarkReady();

        return new GuildMaintenanceHostedService(scopes, gate, readiness, clock, logger);
    }

    private sealed class UnavailableAuthority : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current => null;
    }

    /// <summary>Pauses the real coordinator at its first asynchronous connection boundary, before database access.</summary>
    private sealed class ControlledConnections(string? failure = null) : IGrimoireOrdinaryConnectionFactory
    {
        private readonly TaskCompletionSource<Result<IGrimoireOrdinaryConnectionLease>> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        internal TaskCompletionSource<CancellationToken> FirstOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<CancellationToken> SecondOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The cleanup coordinator must open a fresh connection.");

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            Assert.Equal(GrimoireOrdinaryFreshConnectionKind.ReadWrite, kind);

            int ordinal = Interlocked.Increment(ref _calls);

            (ordinal == 1 ? FirstOpened : SecondOpened).TrySetResult(cancellationToken);

            if (failure == "unrelated-cancellation")
            {
                throw new TaskCanceledException("A dependency cancelled independently of host or maintenance.");
            }

            if (failure == "unavailable-storage")
            {
                return new Error(ErrorCodes.Guild.Unavailable, "The Guild tier is unavailable.");
            }

            return await _never.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger<GuildMaintenanceHostedService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        internal IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                _entries.Enqueue((logLevel, formatter(state, exception)));
            }
        }
    }
}
