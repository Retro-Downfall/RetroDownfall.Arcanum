using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
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

    }

    [Fact]
    public async Task Open_that_loses_admission_is_physically_closed_before_refusal_completes()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        await using GatedOpenSqliteConnection connection = new(ConnectionString);

        await using ProbeDbContext context = CreateContext(connection, gate, drain);

        Task opening = context.Database.OpenConnectionAsync();

        await connection.OpenEntered;

        await using IGrimoireClosingOwner closing = Begin(gate, 2);

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

        connection.AllowOpen();

        _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(() => opening);

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Equal(1, connection.PhysicalCloseCount);

        Assert.Equal(0, drain.RegisterCount);

        Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

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

            await context.DisposeAsync();

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 3);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

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
        ICovenantConnectionDrain drain)
    {

        DbContextOptions<ProbeDbContext> options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseSqlite(connection, contextOwnsConnection: true)
            .AddInterceptors(new CovenantConnectionEnrolmentInterceptor(gate, drain))
            .Options;

        return new ProbeDbContext(options);

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

    private sealed class TrackingSqliteConnection(string connectionString)
        : SqliteConnection(connectionString)
    {

        internal int ProviderOpenCount { get; private set; }

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

    private sealed class GatedOpenSqliteConnection(string connectionString)
        : SqliteConnection(connectionString)
    {

        private readonly TaskCompletionSource _openEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowOpen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task OpenEntered => _openEntered.Task;

        internal int PhysicalCloseCount { get; private set; }

        internal void AllowOpen() =>
            _allowOpen.TrySetResult();

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {

            _openEntered.TrySetResult();

            await _allowOpen.Task.WaitAsync(cancellationToken);

            await base.OpenAsync(cancellationToken);

        }

        public override Task CloseAsync()
        {

            PhysicalCloseCount++;

            return base.CloseAsync();

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

    private sealed class RecordingConnectionDrain : ICovenantConnectionDrain
    {

        private int _activeCount;

        private int _disposeCount;

        private int _registerCount;

        internal int ActiveCount => Volatile.Read(ref _activeCount);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal int RegisterCount => Volatile.Read(ref _registerCount);

        public IDisposable Register(SqliteConnection connection)
        {

            ArgumentNullException.ThrowIfNull(connection);

            _ = Interlocked.Increment(ref _registerCount);

            _ = Interlocked.Increment(ref _activeCount);

            return new Registration(this);

        }

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

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
