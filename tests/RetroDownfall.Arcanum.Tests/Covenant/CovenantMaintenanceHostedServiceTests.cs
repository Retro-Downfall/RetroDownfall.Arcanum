using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Tests.Data.Covenant;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The two decisions the maintenance driver makes before any sweep does anything.
/// </summary>
/// <remarks>
/// Both were asserted only in the service's own remarks, which is the shape this feature has been
/// bitten by repeatedly: a comment describing behaviour that nothing enters. They are cheap to enter,
/// because the pass is one internal method that reports what it decided.
/// </remarks>
public sealed class CovenantMaintenanceHostedServiceTests
{
    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_search_outbox_rebuild_with_no_projection_writes_is_a_warning()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(CovenantScope.Global, null, "missing.version",
            CovenantLane.Confirmed, CovenantOperation.Set, "Body.", Token);

        await using (SqliteCommand command = fixture.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE covenant_search_outbox SET DesiredVersionId = '00000000-0000-4000-8000-000000000001';";

            _ = await command.ExecuteNonQueryAsync(Token);
        }

        FakeCovenantAvailability availability = await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token);

        ServiceCollection services = new();

        await using ArcanumDbContext db = new(
            new DbContextOptionsBuilder<ArcanumDbContext>().UseSqlite(fixture.Connection).Options, null!, null!);

        services.AddSingleton(db);

        services.AddScoped(_ => new CovenantSearchOutboxCoordinator(
            CovenantOperationGateFixture.CreateGate(availability),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantSearchOutboxWorker(),
            DetachedAvailabilityRepublisher.Create()));

        await using ServiceProvider provider = services.BuildServiceProvider();

        CountingLogger<CovenantMaintenanceHostedService> logger = new();

        CovenantMaintenanceHostedService service = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), availability, TimeProvider.System, logger);

        Assert.True(await service.RunOnceAsync(Token));

        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public async Task Refused_sweeps_warn_once_per_code_without_content()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        FakeCovenantAvailability refusing = new();

        refusing.Mutate(static current => current with { Canonical = CovenantCapabilityState.Unavailable });

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(refusing, authority);

        ServiceCollection services = new();

        await using ArcanumDbContext db = new(
            new DbContextOptionsBuilder<ArcanumDbContext>().UseSqlite(fixture.Connection).Options, null!, null!);

        services.AddSingleton(db);

        services.AddScoped(_ => new CovenantOwnerCleanupCoordinator(
            gate, new UnreachableConnectionSource(),
            new CovenantCleanupWorker(), DetachedAvailabilityRepublisher.Create()));

        services.AddScoped(_ => new CovenantSearchOutboxCoordinator(
            gate, new UnreachableConnectionSource(),
            new CovenantSearchOutboxWorker(), DetachedAvailabilityRepublisher.Create()));

        services.AddScoped(_ => new CovenantTurnReceiptCompactionCoordinator(
            gate, new UnreachableConnectionSource(),
            new CovenantTurnReceiptCompactor()));

        await using ServiceProvider provider = services.BuildServiceProvider();

        TestCapturingLogger<CovenantMaintenanceHostedService> logger = new();

        CovenantMaintenanceHostedService service = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), new FakeCovenantAvailability(), TimeProvider.System, logger);

        Assert.True(await service.RunOnceAsync(Token));

        Assert.Equal(3, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));

        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));

        Assert.Equal(
            new[] { "owner cleanup", "search outbox", "turn receipt compaction" }
                .Select(sweep => $"Covenant {sweep} sweep refused: Covenant.Unavailable."),
            logger.Entries.Select(entry => entry.Message));

        Assert.True(await service.RunOnceAsync(Token));

        Assert.Equal(3, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));

        authority.Clear();

        Assert.True(await service.RunOnceAsync(Token));

        Assert.Equal(6, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task A_pass_does_nothing_while_the_feature_is_off()
    {
        FakeCovenantAvailability availability = new();

        availability.Mutate(static current => current with { FeatureEnabled = false });

        // No scope is created and no coordinator is resolved, which is why the provider below holds
        // none: resolving one would throw and the pass would not come back false.
        Assert.False(await Service(availability).RunOnceAsync(Token));
    }

    [Fact]
    public async Task A_pass_does_nothing_while_the_canonical_tier_is_unhealthy()
    {
        FakeCovenantAvailability availability = new();

        availability.Mutate(static current => current with { Canonical = CovenantCapabilityState.Unavailable });

        // Re-read every pass rather than decided at boot. An installation whose tier goes down while
        // the process lives must stop sweeping it, not keep opening transactions against it.
        Assert.False(await Service(availability).RunOnceAsync(Token));
    }

    [Fact]
    public async Task One_sweep_refusing_does_not_stop_the_pass()
    {
        ServiceCollection services = new();

        // A real gate over an unavailable tier, so every acquisition refuses the way it refuses in
        // service. The connection source is never reached, because the refusal lands before it — which
        // is itself worth stating: a sweep that opened a transaction it then could not use would hold
        // a connection for nothing on every pass of a degraded installation.
        FakeCovenantAvailability refusing = new();

        refusing.Mutate(static current => current with { Canonical = CovenantCapabilityState.Unavailable });

        services.AddScoped(_ => new CovenantOwnerCleanupCoordinator(
            CovenantOperationGateFixture.CreateGate(refusing),
            new UnreachableConnectionSource(),
            new CovenantCleanupWorker(),
            DetachedAvailabilityRepublisher.Create()));

        services.AddScoped(_ => new CovenantSearchOutboxCoordinator(
            CovenantOperationGateFixture.CreateGate(refusing),
            new UnreachableConnectionSource(),
            new CovenantSearchOutboxWorker(),
            DetachedAvailabilityRepublisher.Create()));

        services.AddScoped(_ => new CovenantTurnReceiptCompactionCoordinator(
            CovenantOperationGateFixture.CreateGate(refusing),
            new UnreachableConnectionSource(),
            new CovenantTurnReceiptCompactor()));

        CovenantMaintenanceHostedService service = new(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new GrimoireConnectionAdmissionGate(TimeProvider.System),
            new FakeCovenantAvailability(),
            TimeProvider.System,
            NullLogger<CovenantMaintenanceHostedService>.Instance);

        Assert.True(await service.RunOnceAsync(Token));
    }

    [Fact]
    public async Task CovenantPassDenialCreatesNoScope()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(admission);

        await using ServiceProvider provider = services.BuildServiceProvider();

        CountingServiceScopeFactory scopes = new(
            provider.GetRequiredService<IServiceScopeFactory>());

        CountingLogger<CovenantMaintenanceHostedService> logger = new();

        CovenantMaintenanceHostedService service =
            ActivatorUtilities.CreateInstance<CovenantMaintenanceHostedService>(
                provider,
                scopes,
                new FakeCovenantAvailability(),
                TimeProvider.System,
                logger);

        IGrimoireExclusiveClosedLease closed = await PeriodicHostMaintenance.CloseAsync(inner);

        Assert.False(await service.RunOnceAsync(Token));

        Assert.Equal(0, scopes.Created);

        Assert.Equal([GrimoireWorkKind.CovenantMaintenance], admission.RequestedWorkKinds);

        Assert.Equal(0, logger.Errors);

        await PeriodicHostMaintenance.ReopenAsync(closed);
    }

    [Fact]
    public async Task AdmittedCovenantPassDrainsAllThreeSweepsAndScopeDisposal()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(admission);

        await using ServiceProvider provider = services.BuildServiceProvider();

        BlockingAsyncServiceScopeFactory scopes = new(provider);

        CountingLogger<CovenantMaintenanceHostedService> logger = new();

        CovenantMaintenanceHostedService service =
            ActivatorUtilities.CreateInstance<CovenantMaintenanceHostedService>(
                provider,
                scopes,
                new FakeCovenantAvailability(),
                TimeProvider.System,
                logger);

        Task<bool> pass = service.RunOnceAsync(Token);

        await scopes.DisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Result<IGrimoireClosingOwner> beginning = inner.BeginOrResumeExclusive(
            PeriodicHostMaintenance.Owner());

        Assert.True(beginning.IsSuccess, beginning.Error.Message);

        Task<Result> drain = inner.DrainRequestAndWorkAsync(
            beginning.Value,
            CancellationToken.None).AsTask();

        bool drainedBeforeScopeDisposal = drain.IsCompleted;

        scopes.AllowDisposal.TrySetResult();

        Assert.True(await pass.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(drainedBeforeScopeDisposal);

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.Equal(3, logger.Errors);

        Result<IGrimoireExclusiveClosedLease> closed = await inner.CloseConnectionAdmissionAsync(
            beginning.Value,
            CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await PeriodicHostMaintenance.ReopenAsync(closed.Value);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unhealthy")]
    [InlineData("denied")]
    [InlineData("failure")]
    public async Task No_progress_or_unavailable_scheduled_passes_do_not_start_a_fast_retry_loop(string condition)
    {
        FakeCovenantAvailability availability = new();

        if (condition == "disabled") availability.Mutate(current => current with { FeatureEnabled = false });

        if (condition == "unhealthy") availability.Mutate(current => current with { Canonical = CovenantCapabilityState.Unavailable });

        GrimoireConnectionAdmissionGate admission = new(TimeProvider.System);

        IGrimoireExclusiveClosedLease? closed = condition == "denied" ? await PeriodicHostMaintenance.CloseAsync(admission) : null;

        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();

        CountingServiceScopeFactory scopes = new(provider.GetRequiredService<IServiceScopeFactory>());

        ManualWallClockTimeProvider clock = new();

        CountingLogger<CovenantMaintenanceHostedService> logger = new();

        using CovenantMaintenanceHostedService service = new(scopes, admission, availability, clock, logger);

        await service.StartAsync(Token);

        try
        {
            await clock.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(TimeSpan.FromMinutes(condition == "failure" ? 5 : 1), clock.NextDueIn);

            Assert.Equal(condition == "failure" ? 1 : 0, scopes.Created);

            Assert.Equal(condition == "failure" ? 3 : 0, logger.Errors);
        }
        finally
        {
            await service.StopAsync(Token);

            if (closed is not null) await PeriodicHostMaintenance.ReopenAsync(closed);
        }

        Assert.Equal(0, clock.ArmedTimers);

        int scopesAfterStop = scopes.Created;

        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(scopesAfterStop, scopes.Created);
    }

    private static CovenantMaintenanceHostedService Service(ICovenantAvailability availability) =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new GrimoireConnectionAdmissionGate(TimeProvider.System),
            availability,
            TimeProvider.System,
            NullLogger<CovenantMaintenanceHostedService>.Instance);

    /// <summary>
    /// A connection source that fails the test if a sweep reaches it after its lease was refused.
    /// </summary>
    private sealed class UnreachableConnectionSource : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A refused sweep opened a connection it could not use.");

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A refused sweep opened a connection it could not use.");
    }

    private sealed class CountingLogger<TCategory> : ILogger<TCategory>
    {
        private int _errors;

        internal int Warnings { get; private set; }

        internal int Errors => Volatile.Read(ref _errors);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }

            if (logLevel == LogLevel.Error)
            {
                _ = Interlocked.Increment(ref _errors);
            }
        }
    }
}
