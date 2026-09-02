using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The one ordinary-connection double for suites that own a fixture Grimoire, implementing both
/// admission members over that fixture database.
/// </summary>
/// <remarks>
/// It exists because neither earlier fake implements the whole interface — the scoped recorder
/// throws from <c>OpenFreshAsync</c> and the fresh recorder throws from <c>AcquireScopedAsync</c> —
/// so a suite whose subject uses both members had no double to reach for, and a suite that
/// substituted one of them proved nothing about the other.
///
/// <para>Registering the production <c>GrimoireOrdinaryConnectionFactory</c> instead is not the
/// alternative: its fresh-open path builds its connection string from
/// <c>ArcanumPaths.GrimoireDatabaseFile</c> rather than from the context under test, so a fixture
/// that registered it would read and write the developer's live Grimoire. This double derives both
/// members from the fixture database's own connection string — explicitly when constructed from a
/// context, otherwise from the first scoped connection it is handed — so there is exactly one
/// source of truth for where the bytes are.</para>
/// </remarks>
internal sealed class FixtureOrdinaryConnectionFactory : IGrimoireOrdinaryConnectionFactory
{

    private readonly object _gate = new();

    private readonly List<CovenantSqliteConnectionMode> _modes = [];

    private readonly List<GrimoireOrdinaryFreshConnectionKind> _kinds = [];

    private string? _connectionString;

    private int _liveReadOnlyLeaseCount;

    private int _liveReadWriteLeaseCount;

    private int _liveFreshLeaseCount;

    /// <summary>
    /// A double that learns the fixture database from the first scoped connection it is handed.
    /// </summary>
    internal FixtureOrdinaryConnectionFactory()
    {
    }

    /// <summary>
    /// A double bound up front to one fixture database's connection string.
    /// </summary>
    internal FixtureOrdinaryConnectionFactory(string connectionString)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;

    }

    internal static FixtureOrdinaryConnectionFactory For(ArcanumDbContext db)
    {

        ArgumentNullException.ThrowIfNull(db);

        return new FixtureOrdinaryConnectionFactory(
            db.Database.GetConnectionString()
            ?? throw new InvalidOperationException(
                "The fixture context has no connection string to derive ordinary admission from."));

    }

    /// <summary>
    /// Scoped acquisition modes, in order. Fresh opens are recorded on <see cref="Kinds" /> instead,
    /// so a suite asserting on the scoped sequence is not perturbed by a readback.
    /// </summary>
    internal IReadOnlyList<CovenantSqliteConnectionMode> Modes
    {

        get
        {

            lock (_gate)
            {

                return [.. _modes];

            }

        }

    }

    internal IReadOnlyList<GrimoireOrdinaryFreshConnectionKind> Kinds
    {

        get
        {

            lock (_gate)
            {

                return [.. _kinds];

            }

        }

    }

    internal int LiveLeaseCount =>
        Volatile.Read(ref _liveReadOnlyLeaseCount) + Volatile.Read(ref _liveReadWriteLeaseCount);

    internal int LiveFreshLeaseCount => Volatile.Read(ref _liveFreshLeaseCount);

    internal int LiveLeaseCountFor(CovenantSqliteConnectionMode mode) => mode switch
    {
        CovenantSqliteConnectionMode.ReadOnly => Volatile.Read(ref _liveReadOnlyLeaseCount),
        CovenantSqliteConnectionMode.ReadWrite => Volatile.Read(ref _liveReadWriteLeaseCount),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>
    /// Admits the caller's own connection: opens it when closed and leases it without taking
    /// ownership, which is what the serving factory does for a borrowed scoped handle.
    /// </summary>
    public async Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        if (mode is not CovenantSqliteConnectionMode.ReadOnly
            and not CovenantSqliteConnectionMode.ReadWrite)
        {

            throw new ArgumentOutOfRangeException(nameof(mode));

        }

        lock (_gate)
        {

            _connectionString ??= connection.ConnectionString;

            _modes.Add(mode);

        }

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken);

        }

        Increment(mode);

        return Result<IGrimoireOrdinaryConnectionLease>.Success(
            new ScopedLease(this, connection, mode));

    }

    /// <summary>
    /// Opens an independent unpooled connection over the fixture database and initializes it exactly
    /// as the serving factory does, so a fresh readback sees the rows the fixture context wrote.
    /// </summary>
    public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
        GrimoireOrdinaryFreshConnectionKind kind,
        CancellationToken cancellationToken)
    {

        SqliteConnectionStringBuilder builder = new(ConnectionString())
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
                throw new ArgumentOutOfRangeException(nameof(kind));
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

        lock (_gate)
        {

            _kinds.Add(kind);

        }

        _ = Interlocked.Increment(ref _liveFreshLeaseCount);

        return Result<IGrimoireOrdinaryConnectionLease>.Success(
            new FreshLease(this, connection));

    }

    /// <summary>
    /// The fixture database this double opens fresh connections over.
    /// </summary>
    /// <remarks>
    /// Throws rather than refusing with a <see cref="Result" />: an unbound double is a composition
    /// mistake in the suite, and a refusal would let the subject take its unavailable branch and the
    /// test pass without ever reaching the readback it names.
    /// </remarks>
    private string ConnectionString()
    {

        lock (_gate)
        {

            return _connectionString
                ?? throw new InvalidOperationException(
                    "The fixture ordinary connection factory was never given a database: construct it "
                    + "from the fixture context, or acquire a scoped connection before opening fresh.");

        }

    }

    private void Increment(CovenantSqliteConnectionMode mode)
    {

        if (mode is CovenantSqliteConnectionMode.ReadOnly)
        {

            _ = Interlocked.Increment(ref _liveReadOnlyLeaseCount);

        }
        else
        {

            _ = Interlocked.Increment(ref _liveReadWriteLeaseCount);

        }

    }

    private void ReleaseFresh() =>
        Interlocked.Decrement(ref _liveFreshLeaseCount);

    private void Decrement(CovenantSqliteConnectionMode mode)
    {

        if (mode is CovenantSqliteConnectionMode.ReadOnly)
        {

            _ = Interlocked.Decrement(ref _liveReadOnlyLeaseCount);

        }
        else
        {

            _ = Interlocked.Decrement(ref _liveReadWriteLeaseCount);

        }

    }

    private sealed class ScopedLease(
        FixtureOrdinaryConnectionFactory owner,
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode) : IGrimoireOrdinaryConnectionLease
    {

        private int _released;

        public SqliteConnection Connection => connection;

        public void Dispose() => Release();

        public ValueTask DisposeAsync()
        {

            Release();

            return ValueTask.CompletedTask;

        }

        /// <summary>
        /// Releases the count only. The connection belongs to the caller's context, so closing it
        /// here would end the fixture's session mid-test.
        /// </summary>
        private void Release()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                owner.Decrement(mode);

            }

        }

    }

    private sealed class FreshLease(
        FixtureOrdinaryConnectionFactory owner,
        SqliteConnection connection) : IGrimoireOrdinaryConnectionLease
    {

        private int _released;

        public SqliteConnection Connection => connection;

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _released, 1) != 0)
            {

                return;

            }

            connection.Dispose();

            owner.ReleaseFresh();

        }

        public async ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) != 0)
            {

                return;

            }

            await connection.DisposeAsync();

            owner.ReleaseFresh();

        }

    }

}
