using System.Collections.Concurrent;
using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
public sealed class SessionEntryPersistenceTests(GrimoireFixture fixture)
{

    [SkippableFact]
    public async Task Fresh_probe_read_holds_one_read_only_ordinary_lease_through_materialization()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = fixture.CopyDatabase();

        await using ArcanumDbContext db = fixture.CreateContext(path);

        RecordingFreshOrdinaryConnectionFactory connections = For(db);

        SessionEntryPersistence persistence = new(db, connections);

        ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(
            new ToolInvocationIdentity(
                "task-6-probe",
                ProviderToolCallId: "call-1",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolName: "read_probe"));

        MandatoryToolInteractionProbe probe = new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            receipt,
            ToolCallId: "call-1",
            ToolName: "read_probe",
            Arguments: "{}",
            ModelUsed: "test-model",
            CreatedAt: new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

        using ScopedConsumerPause pause = new(
            "SessionEntryPersistence.ReadProbeOnFreshConnectionAsync");

        Task<MandatoryToolInteractionProbeResult> reading = persistence
            .ProbeMandatoryToolInteractionAsync(probe, CancellationToken.None);

        Task entered = pause.WaitUntilEnteredAsync();

        try
        {

            Task first = await Task.WhenAny(entered, reading);

            Assert.Same(entered, first);

            await entered;

            Assert.Equal(1, connections.LiveLeaseCount);

            Assert.Equal(
                [GrimoireOrdinaryFreshConnectionKind.ReadOnly],
                connections.Kinds);

            Assert.Equal(ConnectionState.Open, connections.LastConnection!.State);

        }
        finally
        {

            pause.Release();

            _ = await reading.WaitAsync(TimeSpan.FromSeconds(10));

        }

        MandatoryToolInteractionProbeResult result = await reading;

        Assert.Equal(MandatoryToolInteractionProbeOutcome.NotFound, result.Outcome);

        Assert.Equal(0, connections.LiveLeaseCount);

        Assert.Equal(ConnectionState.Closed, connections.LastConnection!.State);

    }

    [SkippableFact]
    public async Task Fresh_receipt_read_holds_one_read_only_ordinary_lease_through_materialization()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = fixture.CopyDatabase();

        await using ArcanumDbContext db = fixture.CreateContext(path);

        RecordingFreshOrdinaryConnectionFactory connections = For(db);

        SessionEntryPersistence persistence = new(db, connections);

        ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(
            new ToolInvocationIdentity(
                "task-6-receipt",
                ProviderToolCallId: "call-2",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolName: "read_receipt"));

        MandatoryToolInteraction interaction = new(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            receipt,
            ToolCallId: "call-2",
            ToolName: "read_receipt",
            Arguments: "{}",
            Result: "ok",
            ModelUsed: "test-model",
            CreatedAt: new DateTimeOffset(2026, 9, 1, 12, 1, 0, TimeSpan.Zero));

        using ScopedConsumerPause pause = new(
            "SessionEntryPersistence.ReadReceiptOnFreshConnectionAsync");

        Task<MandatoryToolInteractionPreflightResult> reading = persistence
            .PreflightMandatoryToolInteractionAsync(
                interaction,
                settings: null,
                CancellationToken.None);

        Task entered = pause.WaitUntilEnteredAsync();

        try
        {

            Task first = await Task.WhenAny(entered, reading);

            Assert.Same(entered, first);

            await entered;

            Assert.Equal(1, connections.LiveLeaseCount);

            Assert.Equal(
                [GrimoireOrdinaryFreshConnectionKind.ReadOnly],
                connections.Kinds);

            Assert.Equal(ConnectionState.Open, connections.LastConnection!.State);

        }
        finally
        {

            pause.Release();

            _ = await reading.WaitAsync(TimeSpan.FromSeconds(10));

        }

        MandatoryToolInteractionPreflightResult result = await reading;

        Assert.Equal(MandatoryToolInteractionPreflightOutcome.Rejected, result.Outcome);

        Assert.Equal(0, connections.LiveLeaseCount);

        Assert.Equal(ConnectionState.Closed, connections.LastConnection!.State);

    }

    private static RecordingFreshOrdinaryConnectionFactory For(ArcanumDbContext db) =>
        new(db.Database.GetConnectionString()!);

}

internal static class TestOrdinaryConnectionFactory
{

    internal static RecordingFreshOrdinaryConnectionFactory For(ArcanumDbContext db) =>
        new(db.Database.GetConnectionString()!);

}

internal sealed class RecordingFreshOrdinaryConnectionFactory : IGrimoireOrdinaryConnectionFactory
{

    private readonly string _connectionString;

    private readonly string? _overridePassphrase;

    private readonly CovenantConnectionDrain _drain = new();

    private readonly ConcurrentQueue<GrimoireOrdinaryFreshConnectionKind> _kinds = [];

    private readonly ConcurrentQueue<SqliteConnection> _opened = [];

    private int _liveLeaseCount;

    private int _blockNextRelease;

    private int _blockNextOpen;

    private int _refuseNextOpen;

    private TaskCompletionSource? _openBlocked;

    private TaskCompletionSource? _allowOpen;

    private TaskCompletionSource? _releaseEntered;

    private TaskCompletionSource? _allowRelease;

    internal RecordingFreshOrdinaryConnectionFactory(
        string connectionString,
        string? overridePassphrase = null)
    {

        _connectionString = connectionString;

        _overridePassphrase = overridePassphrase;

    }

    internal IReadOnlyList<GrimoireOrdinaryFreshConnectionKind> Kinds => [.. _kinds];

    internal IReadOnlyList<SqliteConnection> Opened => [.. _opened];

    internal SqliteConnection? LastConnection => _opened.LastOrDefault();

    internal int LiveLeaseCount => Volatile.Read(ref _liveLeaseCount);

    internal Task ReleaseEntered =>
        (_releaseEntered ?? throw new InvalidOperationException("No lease release is blocked.")).Task;

    internal ConnectionState? StateAtRelease { get; private set; }

    internal Task OpenBlocked =>
        (_openBlocked ?? throw new InvalidOperationException("No ordinary open is blocked.")).Task;

    internal void BlockNextOpen()
    {

        if (Interlocked.Exchange(ref _blockNextOpen, 1) != 0)
        {

            throw new InvalidOperationException("An ordinary open is already blocked.");

        }

        _openBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _allowOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    }

    internal void AllowOpen() =>
        (_allowOpen ?? throw new InvalidOperationException("No ordinary open is blocked."))
            .TrySetResult();

    internal void RefuseNextOpen() => Volatile.Write(ref _refuseNextOpen, 1);

    internal void BlockNextRelease()
    {

        if (Interlocked.Exchange(ref _blockNextRelease, 1) != 0)
        {

            throw new InvalidOperationException("A lease release is already blocked.");

        }

        _releaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _allowRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    }

    internal void AllowRelease() =>
        (_allowRelease ?? throw new InvalidOperationException("No lease release is blocked."))
            .TrySetResult();

    internal Task<Result> DrainAsync(CancellationToken cancellationToken) =>
        _drain.DrainAsync(cancellationToken);

    public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
        GrimoireOrdinaryFreshConnectionKind kind,
        CancellationToken cancellationToken)
    {

        if (Interlocked.Exchange(ref _refuseNextOpen, 0) != 0)
        {

            return Result<IGrimoireOrdinaryConnectionLease>.Failure(
                new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "The test ordinary connection factory refused the open."));

        }

        SqliteConnectionStringBuilder builder = new(_connectionString)
        {

            Pooling = false,

        };

        CovenantSqliteConnectionMode mode;

        switch (kind)
        {
            case GrimoireOrdinaryFreshConnectionKind.ReadOnly:
                builder.Mode = SqliteOpenMode.ReadOnly;

                builder.Cache = SqliteCacheMode.Private;

                mode = CovenantSqliteConnectionMode.ReadOnly;

                break;

            case GrimoireOrdinaryFreshConnectionKind.ReadWrite:
            case GrimoireOrdinaryFreshConnectionKind.IsolatedHeartbeat:
                builder.Mode = SqliteOpenMode.ReadWriteCreate;

                mode = CovenantSqliteConnectionMode.ReadWrite;

                break;

            default:
                return Result<IGrimoireOrdinaryConnectionLease>.Failure(
                    new Error(
                        ErrorCodes.Covenant.Unavailable,
                        "The test ordinary connection kind is invalid."));
        }

        if (_overridePassphrase is not null)
        {

            builder.Password = _overridePassphrase;

        }

        SqliteConnection connection = new(builder.ToString());

        try
        {

            await connection.OpenAsync(cancellationToken);

            await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
                connection,
                mode,
                cancellationToken);

        }
        catch
        {

            await connection.DisposeAsync();

            throw;

        }

        IDisposable registration = _drain.Register(connection);

        _kinds.Enqueue(kind);

        _opened.Enqueue(connection);

        _ = Interlocked.Increment(ref _liveLeaseCount);

        Lease lease = new(this, connection, registration);

        if (Interlocked.Exchange(ref _blockNextOpen, 0) != 0)
        {

            _openBlocked!.TrySetResult();

            try
            {

                await _allowOpen!.Task.WaitAsync(cancellationToken);

            }
            catch
            {

                await lease.DisposeAsync();

                throw;

            }

        }

        return Result<IGrimoireOrdinaryConnectionLease>.Success(
            lease);

    }

    private void ReleaseSynchronously(SqliteConnection connection, IDisposable registration)
    {

        if (connection.State != ConnectionState.Closed)
        {

            connection.Close();

        }

        StateAtRelease = connection.State;

        registration.Dispose();

        connection.Dispose();

        _ = Interlocked.Decrement(ref _liveLeaseCount);

    }

    private async ValueTask ReleaseAsynchronouslyAsync(
        SqliteConnection connection,
        IDisposable registration)
    {

        if (connection.State != ConnectionState.Closed)
        {

            await connection.CloseAsync();

        }

        StateAtRelease = connection.State;

        if (Interlocked.Exchange(ref _blockNextRelease, 0) != 0)
        {

            _releaseEntered!.TrySetResult();

            await _allowRelease!.Task;

        }

        registration.Dispose();

        await connection.DisposeAsync();

        _ = Interlocked.Decrement(ref _liveLeaseCount);

    }

    private sealed class Lease(
        RecordingFreshOrdinaryConnectionFactory owner,
        SqliteConnection connection,
        IDisposable registration) : IGrimoireOrdinaryConnectionLease
    {

        private int _disposed;

        public SqliteConnection Connection { get; } = connection;

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {

                owner.ReleaseSynchronously(Connection, registration);

            }

        }

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0
                ? owner.ReleaseAsynchronouslyAsync(Connection, registration)
                : ValueTask.CompletedTask;

    }

}
