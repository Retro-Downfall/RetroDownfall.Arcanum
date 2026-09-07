using Microsoft.Data.Sqlite;

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
            new CovenantCleanupWorker()));

        services.AddScoped(_ => new CovenantSearchOutboxCoordinator(
            CovenantOperationGateFixture.CreateGate(refusing),
            new UnreachableConnectionSource(),
            new CovenantSearchOutboxWorker()));

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
            if (logLevel == LogLevel.Error)
            {
                _ = Interlocked.Increment(ref _errors);
            }
        }
    }

}
