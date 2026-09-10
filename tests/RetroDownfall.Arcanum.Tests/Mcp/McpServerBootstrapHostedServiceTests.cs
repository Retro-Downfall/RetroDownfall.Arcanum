using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpServerBootstrapHostedServiceTests
{
    [Fact]
    public async Task BlockingStartIsPreReadiness()
    {
        CoordinatorDouble coordinator = new();

        TestHostApplicationLifetime lifetime = new();

        McpServerBootstrapHostedService service = CreateService(
            coordinator,
            lifetime,
            blocksStartup: true);

        Task start = service.StartAsync(CancellationToken.None);

        await coordinator.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(start.IsCompleted);

        Assert.Equal(
            [McpGlobalInitializationAuthority.PreReadinessStartup],
            coordinator.Authorities);

        coordinator.ReleaseInitialization();

        await start.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task NonblockingWaiterIsObserved()
    {
        CoordinatorDouble coordinator = new();

        TestHostApplicationLifetime lifetime = new();

        McpServerBootstrapHostedService service = CreateService(
            coordinator,
            lifetime,
            blocksStartup: false);

        await service.StartAsync(CancellationToken.None);

        await coordinator.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            [McpGlobalInitializationAuthority.OrdinaryHostedWork],
            coordinator.Authorities);

        Task stop = service.StopAsync(CancellationToken.None);

        await Task.Delay(50);

        Assert.False(stop.IsCompleted);

        coordinator.ReleaseInitialization();

        await stop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, coordinator.StopCount);
    }

    [Fact]
    public async Task StopObservesWaiterEvenWhenTeardownFails()
    {
        CoordinatorDouble coordinator = new()
        {
            StopFailure = new InvalidOperationException("synthetic teardown failure"),
        };

        TestHostApplicationLifetime lifetime = new();

        McpServerBootstrapHostedService service = CreateService(
            coordinator,
            lifetime,
            blocksStartup: false);

        await service.StartAsync(CancellationToken.None);

        await coordinator.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(30));

        Task stop = service.StopAsync(CancellationToken.None);

        await Task.Delay(50);

        Assert.False(stop.IsCompleted);

        coordinator.ReleaseInitialization();

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stop.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("synthetic teardown failure", failure.Message);
    }

    [Fact]
    public async Task ApplicationStoppingCancellationIsObservedWithoutStopFailure()
    {
        CoordinatorDouble coordinator = new(
            waitForCancellation: true);

        TestHostApplicationLifetime lifetime = new();

        McpServerBootstrapHostedService service = CreateService(
            coordinator,
            lifetime,
            blocksStartup: false);

        await service.StartAsync(CancellationToken.None);

        await coordinator.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(30));

        lifetime.StopApplication();

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, coordinator.StopCount);
    }

    [Fact]
    public async Task RepeatedStartAndStopReuseOneOwnedOperation()
    {
        CoordinatorDouble coordinator = new();

        TestHostApplicationLifetime lifetime = new();

        McpServerBootstrapHostedService service = CreateService(
            coordinator,
            lifetime,
            blocksStartup: false);

        await service.StartAsync(CancellationToken.None);

        await service.StartAsync(CancellationToken.None);

        await coordinator.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(30));

        coordinator.ReleaseInitialization();

        Task firstStop = service.StopAsync(CancellationToken.None);

        Task secondStop = service.StopAsync(CancellationToken.None);

        await Task.WhenAll(firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(coordinator.Authorities);

        Assert.Equal(1, coordinator.StopCount);
    }

    [Fact]
    public async Task BlockingCallerCancellationDoesNotChangeChosenAuthority()
    {
        CoordinatorDouble coordinator = new(
            waitForCancellation: true);

        TestHostApplicationLifetime lifetime = new();

        McpServerBootstrapHostedService service = CreateService(
            coordinator,
            lifetime,
            blocksStartup: true);

        using CancellationTokenSource caller = new();

        Task start = service.StartAsync(caller.Token);

        await coordinator.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(30));

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => start.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(
            [McpGlobalInitializationAuthority.PreReadinessStartup],
            coordinator.Authorities);

        coordinator.ReleaseInitialization();
    }

    [Fact]
    public async Task CancelledBlockingStartCanRejoinTheSameInitializer()
    {
        CoordinatorDouble coordinator = new();

        TestHostApplicationLifetime lifetime = new();

        McpServerBootstrapHostedService service = CreateService(
            coordinator,
            lifetime,
            blocksStartup: true);

        using CancellationTokenSource firstCaller = new();

        Task first = service.StartAsync(firstCaller.Token);

        await coordinator.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(30));

        firstCaller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => first.WaitAsync(TimeSpan.FromSeconds(30)));

        Task retry = service.StartAsync(CancellationToken.None);

        Assert.False(retry.IsCompleted);

        coordinator.ReleaseInitialization();

        await retry.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(coordinator.Authorities);

        Assert.Equal(
            McpGlobalInitializationAuthority.PreReadinessStartup,
            coordinator.Authorities[0]);
    }

    private static McpServerBootstrapHostedService CreateService(
        IMcpGlobalInitializationCoordinator coordinator,
        IHostApplicationLifetime lifetime,
        bool blocksStartup)
    {
        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                Mcp = new McpIntegrationSettings
                {
                    BootstrapBlocksStartup = blocksStartup,
                },
            },
        };

        return new McpServerBootstrapHostedService(
            coordinator,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            lifetime,
            NullLogger<McpServerBootstrapHostedService>.Instance);
    }

    private sealed class CoordinatorDouble(
        bool waitForCancellation = false) : IMcpGlobalInitializationCoordinator
    {
        private readonly object _gate = new();

        private readonly List<McpGlobalInitializationAuthority> _authorities = [];

        private readonly TaskCompletionSource _initializationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseInitialization = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _stopCount;

        internal Exception? StopFailure { get; init; }

        internal Task InitializationStarted => _initializationStarted.Task;

        internal int StopCount => Volatile.Read(ref _stopCount);

        internal IReadOnlyList<McpGlobalInitializationAuthority> Authorities
        {
            get
            {
                lock (_gate)
                {
                    return _authorities.ToArray();
                }
            }
        }

        public async Task InitializeGlobalAsync(
            McpGlobalInitializationAuthority authority,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _authorities.Add(authority);
            }

            _initializationStarted.TrySetResult();

            if (waitForCancellation)
            {
                Task cancellation = Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);

                Task completed = await Task.WhenAny(
                    cancellation,
                    _releaseInitialization.Task);

                await completed;

                return;
            }

            await _releaseInitialization.Task.WaitAsync(cancellationToken);
        }

        public Task StopAllAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _stopCount);

            return StopFailure is null
                ? Task.CompletedTask
                : Task.FromException(StopFailure);
        }

        internal void ReleaseInitialization() =>
            _releaseInitialization.TrySetResult();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();

        private readonly CancellationTokenSource _stopping = new();

        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
        }
    }
}
