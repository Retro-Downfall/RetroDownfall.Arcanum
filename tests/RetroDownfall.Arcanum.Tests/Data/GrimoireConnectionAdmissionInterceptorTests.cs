using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection(RetroDownfall.Arcanum.Tests.Collections.SqliteConnectionPoolCollection.Name)]
public sealed class GrimoireConnectionAdmissionInterceptorTests
{

    [Fact]
    public async Task Closed_admission_refuses_before_the_provider_open()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 1);

        RecordingConnectionDrain drain = new();

        await using TrackingSqliteConnection connection = new(ConnectionString);

        await using ProbeDbContext context = CreateContext(connection, gate, drain);

        _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(
            () => context.Database.OpenConnectionAsync());

        Assert.Equal(0, connection.ProviderOpenCount);

        Assert.Equal(0, drain.RegisterCount);

        Assert.Equal(0, drain.DisposeCount);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_that_loses_admission_closes_and_clears_its_exact_pool_before_refusal(
        bool asynchronous)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        List<string> releaseOrder = [];

        Task<Result<IGrimoireExclusiveClosedLease>>? closingAdmission = null;

        GatedOpenSqliteConnection connection = new(ConnectionString, releaseOrder);

        RecordingConnectionDrain drain = new(cleared =>
        {

            Assert.Same(connection, cleared);

            Assert.Equal(ConnectionState.Closed, cleared.State);

            Assert.NotNull(closingAdmission);

            Assert.False(closingAdmission!.IsCompleted);

            releaseOrder.Add("clear");

        });

        RecordingConnectionInitializer initializer = new();

        await using (connection.ConfigureAwait(false))
        {

            await using ProbeDbContext context = CreateContext(
                connection,
                gate,
                drain,
                initializer);

            Task opening = asynchronous
                ? context.Database.OpenConnectionAsync()
                : Task.Run(context.Database.OpenConnection);

            await connection.OpenEntered;

            await using IGrimoireClosingOwner closing = Begin(gate, 2);

            Result requestsDrained = await gate.DrainRequestAndWorkAsync(
                closing,
                CancellationToken.None);

            Assert.True(
                requestsDrained.IsSuccess,
                requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

            closingAdmission = gate
                .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
                .AsTask();

            Assert.False(closingAdmission.IsCompleted);

            connection.AllowOpen();

            _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(() => opening);

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(1, connection.PhysicalCloseCount);

            Assert.Equal(0, initializer.CallCount);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(1, drain.ExactPoolClearCount);

            Assert.Equal(["close", "clear"], releaseOrder);

            Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

            Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

            await using IGrimoireExclusiveClosedLease lease = closed.Value;

        }

    }

    [Fact]
    public async Task Closure_during_post_open_initializer_closes_before_ticket_completion()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        BlockingConnectionInitializer initializer = new();

        await using TrackingSqliteConnection connection = new(ConnectionString);

        await using ProbeDbContext context = CreateContext(
            connection,
            gate,
            drain,
            initializer);

        Task opening = context.Database.OpenConnectionAsync();

        await initializer.Entered;

        await using IGrimoireClosingOwner closing = Begin(gate, 20);

        Result requestsDrained = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(
            requestsDrained.IsSuccess,
            requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

        Task<Result<IGrimoireExclusiveClosedLease>> closingAdmission = gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
            .AsTask();

        Assert.False(closingAdmission.IsCompleted);

        initializer.AllowCompletion();

        _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(() => opening);

        Assert.Equal(1, initializer.CallCount);

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Equal(1, connection.PhysicalCloseCount);

        Assert.Equal(1, drain.RegisterCount);

        Assert.Equal(1, drain.DisposeCount);

        Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Initializer_termination_closes_and_clears_exact_pool_before_ticket_completion(
        bool asynchronous,
        bool canceled)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        List<string> releaseOrder = [];

        Task<Result<IGrimoireExclusiveClosedLease>>? closingAdmission = null;

        TrackingSqliteConnection connection = new(ConnectionString, releaseOrder);

        RecordingConnectionDrain drain = new(cleared =>
        {

            Assert.Same(connection, cleared);

            Assert.Equal(ConnectionState.Closed, cleared.State);

            Assert.NotNull(closingAdmission);

            Assert.False(closingAdmission!.IsCompleted);

            releaseOrder.Add("clear");

        });

        TerminatingConnectionInitializer initializer = new();

        ProbeDbContext context = CreateContext(connection, gate, drain, initializer);

        try
        {

            Task opening = asynchronous
                ? context.Database.OpenConnectionAsync()
                : Task.Run(context.Database.OpenConnection);

            await initializer.Entered;

            await using IGrimoireClosingOwner closing = Begin(gate, 21);

            Result requestsDrained = await gate.DrainRequestAndWorkAsync(
                closing,
                CancellationToken.None);

            Assert.True(
                requestsDrained.IsSuccess,
                requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

            closingAdmission = gate
                .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
                .AsTask();

            Assert.False(closingAdmission.IsCompleted);

            initializer.Terminate(canceled);

            if (canceled)
            {

                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);

            }
            else
            {

                _ = await Assert.ThrowsAsync<InvalidOperationException>(() => opening);

            }

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(1, connection.PhysicalCloseCount);

            Assert.Equal(1, drain.ExactPoolClearCount);

            Assert.Equal(["close", "clear"], releaseOrder);

            Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

            Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

            await using IGrimoireExclusiveClosedLease lease = closed.Value;

        }
        finally
        {

            await context.DisposeAsync();

            await connection.DisposeAsync();

        }

    }

    [Fact]
    public async Task Drain_enrolment_begins_only_after_successful_post_open_revalidation()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        await using GatedOpenSqliteConnection connection = new(ConnectionString);

        await using ProbeDbContext context = CreateContext(connection, gate, drain);

        Task opening = context.Database.OpenConnectionAsync();

        await connection.OpenEntered;

        Assert.Equal(0, drain.RegisterCount);

        Assert.Equal(0, drain.ActiveCount);

        connection.AllowOpen();

        await opening;

        Assert.Equal(1, drain.RegisterCount);

        Assert.Equal(1, drain.ActiveCount);

        await context.Database.CloseConnectionAsync();

        Assert.Equal(0, drain.ActiveCount);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_failure_callbacks_release_the_open_ticket_exactly_once(bool asynchronous)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        await using FailingOpenSqliteConnection connection = new(ConnectionString);

        ProbeDbContext context = CreateContext(connection, gate, drain);

        try
        {

            if (asynchronous)
            {

                _ = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => context.Database.OpenConnectionAsync());

            }
            else
            {

                _ = Assert.Throws<InvalidOperationException>(
                    context.Database.OpenConnection);

            }

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 3);

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(0, drain.ExactPoolClearCount);

        }
        finally
        {

            await context.DisposeAsync();

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_open_initializer_failure_closes_and_releases_before_context_disposal(
        bool asynchronous)
    {

        await AssertPostOpenTerminationCleansBeforeDisposalAsync(
            asynchronous,
            ownerSeed: 5);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_cancellation_after_native_open_closes_and_releases_before_context_disposal(
        bool asynchronous)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        await using CanceledAfterOpenSqliteConnection connection = new(ConnectionString);

        ProbeDbContext context = CreateContext(connection, gate, drain);

        try
        {

            if (asynchronous)
            {

                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => context.Database.OpenConnectionAsync());

            }
            else
            {

                _ = Assert.ThrowsAny<OperationCanceledException>(
                    context.Database.OpenConnection);

            }

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.True(connection.NativeOpenCompleted);

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 6);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(1, drain.ExactPoolClearCount);

        }
        finally
        {

            await context.DisposeAsync();

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_cancellation_releases_the_ticket_before_context_disposal(
        bool asynchronous)
    {

        GrimoireConnectionAdmissionGate gate = new(
            TimeProvider.System,
            TimeSpan.FromMilliseconds(100));

        RecordingConnectionDrain drain = new();

        await using CanceledOpenSqliteConnection connection = new(ConnectionString);

        ProbeDbContext context = CreateContext(connection, gate, drain);

        try
        {

            if (asynchronous)
            {

                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => context.Database.OpenConnectionAsync());

            }
            else
            {

                _ = Assert.ThrowsAny<OperationCanceledException>(
                    context.Database.OpenConnection);

            }

            Assert.Equal(ConnectionState.Closed, connection.State);

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 7);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(0, drain.ExactPoolClearCount);

        }
        finally
        {

            await context.DisposeAsync();

        }

    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Close_and_dispose_callbacks_release_enrolment_exactly_once(
        bool asynchronous,
        bool disposeWhileOpen)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        TrackingSqliteConnection connection = new(ConnectionString);

        ProbeDbContext context = CreateContext(connection, gate, drain);

        try
        {

            if (asynchronous)
            {

                await context.Database.OpenConnectionAsync();

            }
            else
            {

                context.Database.OpenConnection();

            }

            Assert.Equal(1, drain.ActiveCount);

            if (disposeWhileOpen)
            {

                if (asynchronous)
                {

                    await context.DisposeAsync();

                }
                else
                {

                    context.Dispose();

                }

            }
            else if (asynchronous)
            {

                await context.Database.CloseConnectionAsync();

                await context.DisposeAsync();

            }
            else
            {

                context.Database.CloseConnection();

                context.Dispose();

            }

            Assert.Equal(1, drain.RegisterCount);

            Assert.Equal(1, drain.DisposeCount);

            Assert.Equal(0, drain.ActiveCount);

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 4);

        }
        finally
        {

            await context.DisposeAsync();

            await connection.DisposeAsync();

        }

    }

    private const string ConnectionString = "Data Source=:memory:;Pooling=False";

    private static ProbeDbContext CreateContext(
        SqliteConnection connection,
        IGrimoireConnectionAdmissionGate gate,
        ICovenantConnectionDrain drain,
        ICovenantSqliteConnectionInitializer? initializer = null)
    {

        DbContextOptions<ProbeDbContext> options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseSqlite(connection, contextOwnsConnection: true)
            .AddInterceptors(
                new CovenantConnectionEnrolmentInterceptor(
                    new GrimoireOrdinaryConnectionLifecycle(gate, drain),
                    initializer ?? NoOpConnectionInitializer.Instance))
            .Options;

        return new ProbeDbContext(options);

    }

    private static async Task AssertPostOpenTerminationCleansBeforeDisposalAsync(
        bool asynchronous,
        byte ownerSeed)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        TrackingSqliteConnection connection = new(ConnectionString);

        ProbeDbContext context = CreateContext(
            connection,
            gate,
            drain,
            new ThrowingConnectionInitializer());

        try
        {

            if (asynchronous)
            {

                _ = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => context.Database.OpenConnectionAsync());

            }
            else
            {

                _ = Assert.Throws<InvalidOperationException>(
                    context.Database.OpenConnection);

            }

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(1, connection.PhysicalCloseCount);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(0, drain.ActiveCount);

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(
                gate,
                ownerSeed);

        }
        finally
        {

            await context.DisposeAsync();

            await connection.DisposeAsync();

        }

    }

    private static async Task<IGrimoireExclusiveClosedLease> CloseAdmissionAsync(
        GrimoireConnectionAdmissionGate gate,
        byte ownerSeed)
    {

        await using IGrimoireClosingOwner closing = Begin(gate, ownerSeed);

        Result requestsDrained = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(
            requestsDrained.IsSuccess,
            requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

        Result<IGrimoireExclusiveClosedLease> closed = await gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        return closed.Value;

    }

    private static IGrimoireClosingOwner Begin(
        GrimoireConnectionAdmissionGate gate,
        byte ownerSeed)
    {

        CovenantExclusiveRecoveryOwner owner = new(
            Guid.Parse($"00000000-0000-0000-0000-{ownerSeed:D12}"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat(ownerSeed, 32).ToArray()));

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(owner);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        return begun.Value;

    }

    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options)
        : DbContext(options)
    {
    }

    private sealed class TrackingSqliteConnection : SqliteConnection
    {

        internal TrackingSqliteConnection(
            string connectionString,
            List<string>? releaseOrder = null)
            : base(connectionString)
        {

            StateChange += (_, args) =>
            {

                if (args.OriginalState != ConnectionState.Closed
                    && args.CurrentState == ConnectionState.Closed)
                {

                    PhysicalCloseCount++;

                    releaseOrder?.Add("close");

                }

            };

        }

        internal int ProviderOpenCount { get; private set; }

        internal int PhysicalCloseCount { get; private set; }

        public override void Open()
        {

            ProviderOpenCount++;

            base.Open();

        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {

            ProviderOpenCount++;

            return base.OpenAsync(cancellationToken);

        }

    }

    private sealed class GatedOpenSqliteConnection : SqliteConnection
    {

        private readonly TaskCompletionSource _openEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowOpen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal GatedOpenSqliteConnection(
            string connectionString,
            List<string>? releaseOrder = null)
            : base(connectionString)
        {

            StateChange += (_, args) =>
            {

                if (args.OriginalState != ConnectionState.Closed
                    && args.CurrentState == ConnectionState.Closed)
                {

                    PhysicalCloseCount++;

                    releaseOrder?.Add("close");

                }

            };

        }

        internal Task OpenEntered => _openEntered.Task;

        internal int PhysicalCloseCount { get; private set; }

        internal void AllowOpen() =>
            _allowOpen.TrySetResult();

        public override void Open()
        {

            _openEntered.TrySetResult();

            _allowOpen.Task.GetAwaiter().GetResult();

            base.Open();

        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {

            _openEntered.TrySetResult();

            await _allowOpen.Task.WaitAsync(cancellationToken);

            await base.OpenAsync(cancellationToken);

        }

    }

    private sealed class FailingOpenSqliteConnection(string connectionString)
        : SqliteConnection(connectionString)
    {

        public override void Open() =>
            throw new InvalidOperationException("provider open failed");

        public override Task OpenAsync(CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("provider open failed"));

    }

    private sealed class CanceledOpenSqliteConnection(string connectionString)
        : SqliteConnection(connectionString)
    {

        private readonly CancellationTokenSource _canceled = CreateCanceledSource();

        public override void Open() =>
            throw new OperationCanceledException(_canceled.Token);

        public override Task OpenAsync(CancellationToken cancellationToken) =>
            Task.FromCanceled(_canceled.Token);

        private static CancellationTokenSource CreateCanceledSource()
        {

            CancellationTokenSource source = new();

            source.Cancel();

            return source;

        }

    }

    private sealed class CanceledAfterOpenSqliteConnection(string connectionString)
        : SqliteConnection(connectionString)
    {

        internal bool NativeOpenCompleted { get; private set; }

        public override void Open()
        {

            base.Open();

            NativeOpenCompleted = true;

            throw new OperationCanceledException("provider canceled after native open");

        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {

            await base.OpenAsync(cancellationToken);

            NativeOpenCompleted = true;

            throw new OperationCanceledException("provider canceled after native open");

        }

    }

    private sealed class NoOpConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        internal static NoOpConnectionInitializer Instance { get; } = new();

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            _ = Interlocked.Increment(ref _callCount);

            return ValueTask.CompletedTask;

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class BlockingConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal Task Entered => _entered.Task;

        internal void AllowCompletion() => _allowCompletion.TrySetResult();

        public async ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            _ = Interlocked.Increment(ref _callCount);

            _entered.TrySetResult();

            await _allowCompletion.Task.WaitAsync(cancellationToken);

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class ThrowingConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(
                new InvalidOperationException("post-open initializer failed"));

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class TerminatingConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _terminated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Terminate(bool canceled)
        {

            Exception failure = canceled
                ? new OperationCanceledException("post-open initializer canceled")
                : new InvalidOperationException("post-open initializer failed");

            _terminated.TrySetException(failure);

        }

        public async ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            _entered.TrySetResult();

            await _terminated.Task;

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingConnectionDrain(
        Action<SqliteConnection>? exactPoolClear = null) : ICovenantConnectionDrain
    {

        private int _activeCount;

        private int _disposeCount;

        private int _registerCount;

        private int _exactPoolClearCount;

        internal int ActiveCount => Volatile.Read(ref _activeCount);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal int RegisterCount => Volatile.Read(ref _registerCount);

        internal int ExactPoolClearCount => Volatile.Read(ref _exactPoolClearCount);

        public IDisposable Register(SqliteConnection connection)
        {

            ArgumentNullException.ThrowIfNull(connection);

            _ = Interlocked.Increment(ref _registerCount);

            _ = Interlocked.Increment(ref _activeCount);

            return new Registration(this);

        }

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Result ClearExactPoolAfterClose(SqliteConnection connection)
        {

            _ = Interlocked.Increment(ref _exactPoolClearCount);

            exactPoolClear?.Invoke(connection);

            return Result.Success();

        }

        private sealed class Registration(RecordingConnectionDrain owner) : IDisposable
        {

            private int _disposed;

            public void Dispose()
            {

                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {

                    return;

                }

                _ = Interlocked.Increment(ref owner._disposeCount);

                _ = Interlocked.Decrement(ref owner._activeCount);

            }

        }

    }

}
