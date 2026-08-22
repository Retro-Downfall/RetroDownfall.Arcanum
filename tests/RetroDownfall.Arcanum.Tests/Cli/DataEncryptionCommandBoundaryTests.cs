using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class DataEncryptionCommandBoundaryTests
{

    [Fact]
    public async Task Every_verb_resolves_calls_and_disposes_storage_inside_the_exclusive_boundary()
    {

        BoundaryState state = new();

        ServiceCollection services = new();

        services.AddSingleton(state);

        services.AddScoped<BoundaryDisposalProbe>();

        services.AddScoped<IBlobEncryptionLifecycleService, RecordingLifecycleService>();

        await using ServiceProvider provider = services.BuildServiceProvider();

        FakeExclusiveInitialization initialization = new(
            state,
            provider.GetRequiredService<IServiceScopeFactory>(),
            refuse: false);

        DataEncryptionCommands command = new(initialization);

        Assert.Equal(0, await command.Status(CancellationToken.None));

        Assert.Equal(0, await command.Migrate(1, 0, CancellationToken.None));

        Assert.Equal(0, await command.Verify(1, 0, CancellationToken.None));

        Assert.Equal(0, await command.RotateKey(1, 0, CancellationToken.None));

        Assert.Equal(4, initialization.RunCount);

        Assert.Equal(4, state.ServiceCallCount);

        Assert.Equal(4, state.ScopeDisposalCount);

        Assert.Equal(0, state.BoundaryViolationCount);

    }

    [Fact]
    public async Task Exclusive_refusal_prevents_scope_creation_and_storage_access()
    {

        BoundaryState state = new();

        CountingScopeFactory scopeFactory = new();

        FakeExclusiveInitialization initialization = new(
            state,
            scopeFactory,
            refuse: true);

        DataEncryptionCommands command = new(initialization);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            command.Status(CancellationToken.None));

        Assert.Equal(1, initialization.RunCount);

        Assert.Equal(0, scopeFactory.CreateCount);

        Assert.Equal(0, state.ServiceCallCount);

    }

    [Fact]
    public void Data_encryption_never_bootstraps_or_resolves_local_services_outside_the_runner()
    {

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            static candidate => candidate.IsExactOwner(
                "src/RetroDownfall.Arcanum.Cli/Commands/DataEncryptionCommands.cs"));

        Assert.False(source.Names(".EnsureInitializedAsync("));

        Assert.False(source.Names("IServiceScopeFactory"));

        Assert.False(source.Names(".CreateAsyncScope("));

        Assert.True(source.Names(".RunExclusiveWithBootstrapAsync("));

    }

    private sealed class FakeExclusiveInitialization(
        BoundaryState state,
        IServiceScopeFactory scopeFactory,
        bool refuse) : IGrimoireCliInitialization
    {

        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);

        public async Task<T> RunExclusiveAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {

            Interlocked.Increment(ref _runCount);

            if (refuse)
            {

                throw new InvalidOperationException("Exclusive ownership was refused.");

            }

            Assert.Equal(0, Interlocked.Exchange(ref state.InsideBoundary, 1));

            try
            {

                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                return await operation(scope.ServiceProvider, cancellationToken);

            }
            finally
            {

                Assert.Equal(1, Interlocked.Exchange(ref state.InsideBoundary, 0));

            }

        }

        public Task<T> RunExclusiveWithBootstrapAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) =>
            RunExclusiveAsync(operation, cancellationToken);

    }

    private sealed class RecordingLifecycleService(
        BoundaryState state,
        BoundaryDisposalProbe disposalProbe) : IBlobEncryptionLifecycleService
    {

        private readonly BoundaryDisposalProbe _disposalProbe = disposalProbe;

        public Task<BlobEncryptionStatus> GetStatusAsync(
            CancellationToken cancellationToken = default)
        {

            RecordCall();

            return Task.FromResult(
                new BlobEncryptionStatus(0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>()));

        }

        public Task<BlobEncryptionOperationResult> MigrateAsync(
            int maxConcurrency,
            long maxBytesPerSecond,
            CancellationToken cancellationToken = default) => Operation();

        public Task<BlobEncryptionOperationResult> VerifyAsync(
            int maxConcurrency,
            long maxBytesPerSecond,
            CancellationToken cancellationToken = default) => Operation();

        public Task<BlobEncryptionOperationResult> RotateKeyAsync(
            int maxConcurrency,
            long maxBytesPerSecond,
            CancellationToken cancellationToken = default) => Operation();

        private Task<BlobEncryptionOperationResult> Operation()
        {

            RecordCall();

            return Task.FromResult(
                new BlobEncryptionOperationResult(
                    Guid.Empty,
                    0,
                    0,
                    0,
                    0,
                    0,
                    new Dictionary<BlobEncryptionVerificationIssue, int>()));

        }

        private void RecordCall()
        {

            _ = _disposalProbe;

            if (Volatile.Read(ref state.InsideBoundary) != 1)
            {

                Interlocked.Increment(ref state.BoundaryViolationCount);

            }

            Interlocked.Increment(ref state.ServiceCallCount);

        }

    }

    private sealed class BoundaryDisposalProbe(BoundaryState state) : IAsyncDisposable
    {

        public ValueTask DisposeAsync()
        {

            if (Volatile.Read(ref state.InsideBoundary) != 1)
            {

                Interlocked.Increment(ref state.BoundaryViolationCount);

            }

            Interlocked.Increment(ref state.ScopeDisposalCount);

            return ValueTask.CompletedTask;

        }

    }

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {

        private int _createCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public IServiceScope CreateScope()
        {

            Interlocked.Increment(ref _createCount);

            throw new InvalidOperationException("A refused operation must not create a scope.");

        }

    }

    private sealed class BoundaryState
    {

        public int InsideBoundary;

        public int ServiceCallCount;

        public int ScopeDisposalCount;

        public int BoundaryViolationCount;

    }

}
