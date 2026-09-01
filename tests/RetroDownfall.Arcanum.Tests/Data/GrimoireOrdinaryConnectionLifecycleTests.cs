using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class GrimoireOrdinaryConnectionLifecycleTests
{

    private const string ConnectionString = "Data Source=:memory:;Pooling=False";

    [Fact]
    public void Closed_connection_begins_exactly_one_open_ticket()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, drain);

        using SqliteConnection connection = new(ConnectionString);

        using IGrimoireOrdinaryConnectionRegistration registration = lifecycle.BeginOpen(connection);

        Assert.Equal(gate.CurrentGeneration, registration.Generation);

        _ = Assert.Throws<InvalidOperationException>(() => lifecycle.BeginOpen(connection));

        registration.MarkFailed();

    }

    [Fact]
    public void Already_open_unproven_connection_cannot_be_borrowed()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, new RecordingConnectionDrain());

        using SqliteConnection connection = new(ConnectionString);

        connection.Open();

        Result<IGrimoireOrdinaryConnectionRegistration> borrowed =
            lifecycle.BorrowCurrentOpen(connection);

        Assert.True(borrowed.IsFailure);

    }

    [Fact]
    public void Proven_current_generation_open_can_be_borrowed()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, drain);

        using SqliteConnection connection = new(ConnectionString);

        using IGrimoireOrdinaryConnectionRegistration owner = ProveOpen(lifecycle, connection);

        Result<IGrimoireOrdinaryConnectionRegistration> borrowed =
            lifecycle.BorrowCurrentOpen(connection);

        Assert.True(borrowed.IsSuccess, borrowed.IsFailure ? borrowed.Error.Message : null);

        using IGrimoireOrdinaryConnectionRegistration holder = borrowed.Value;

        Assert.Same(connection, holder.Connection);

        Assert.Equal(owner.Generation, holder.Generation);

        Assert.Equal(1, drain.RegisterCount);

    }

    [Fact]
    public async Task Stale_generation_provenance_cannot_be_borrowed()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, new RecordingConnectionDrain());

        using SqliteConnection connection = new(ConnectionString);

        using IGrimoireOrdinaryConnectionRegistration owner = ProveOpen(lifecycle, connection);

        await AdvanceGenerationAsync(gate, 1);

        Result<IGrimoireOrdinaryConnectionRegistration> borrowed =
            lifecycle.BorrowCurrentOpen(connection);

        Assert.True(borrowed.IsFailure);

    }

    [Fact]
    public void Disposing_one_of_two_holders_keeps_the_physical_open_enrolled()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, drain);

        using SqliteConnection connection = new(ConnectionString);

        using IGrimoireOrdinaryConnectionRegistration owner = ProveOpen(lifecycle, connection);

        IGrimoireOrdinaryConnectionRegistration borrowed =
            lifecycle.BorrowCurrentOpen(connection).Value;

        borrowed.Dispose();

        Assert.Equal(1, drain.ActiveCount);

        Assert.Equal(0, drain.DisposeCount);

    }

    [Fact]
    public void Last_holder_unregisters_the_physical_open_exactly_once()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, drain);

        using SqliteConnection connection = new(ConnectionString);

        IGrimoireOrdinaryConnectionRegistration owner = ProveOpen(lifecycle, connection);

        IGrimoireOrdinaryConnectionRegistration borrowed =
            lifecycle.BorrowCurrentOpen(connection).Value;

        owner.Dispose();

        Assert.Equal(1, drain.ActiveCount);

        borrowed.Dispose();

        borrowed.Dispose();

        owner.Dispose();

        Assert.Equal(0, drain.ActiveCount);

        Assert.Equal(1, drain.DisposeCount);

    }

    [Fact]
    public async Task Refused_registration_cannot_terminalize_until_physical_connection_is_closed()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, new RecordingConnectionDrain());

        using SqliteConnection connection = new(ConnectionString);

        using IGrimoireOrdinaryConnectionRegistration registration = lifecycle.BeginOpen(connection);

        connection.Open();

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

        Result revalidated = registration.RevalidateAfterNativeOpen();

        Assert.True(revalidated.IsFailure);

        _ = Assert.Throws<InvalidOperationException>(registration.MarkRefusedAfterOpen);

        Assert.False(closingAdmission.IsCompleted);

        connection.Close();

        registration.MarkRefusedAfterOpen();

        Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

    }

    private static IGrimoireOrdinaryConnectionRegistration ProveOpen(
        IGrimoireOrdinaryConnectionLifecycle lifecycle,
        SqliteConnection connection)
    {

        IGrimoireOrdinaryConnectionRegistration registration = lifecycle.BeginOpen(connection);

        connection.Open();

        Result revalidated = registration.RevalidateAfterNativeOpen();

        Assert.True(
            revalidated.IsSuccess,
            revalidated.IsFailure ? revalidated.Error.Message : null);

        Result opened = registration.MarkOpened();

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        return registration;

    }

    private static async Task AdvanceGenerationAsync(
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

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Result reopened = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);

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

        public Result ClearExactPoolAfterClose(SqliteConnection connection) =>
            Result.Success();

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
