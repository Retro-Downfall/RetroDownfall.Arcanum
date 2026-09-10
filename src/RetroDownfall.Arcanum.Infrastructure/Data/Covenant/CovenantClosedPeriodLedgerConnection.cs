using System.Data.Common;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The one durable-ledger connection a closed period keeps working, named rather than reached for.
/// </summary>
/// <remarks>
/// A Covenant erasure closes ordinary admission and then still has to terminalize its own operation
/// row: the compare-exchange the journal binds itself to happens while the database is closed, and it
/// has to, because a row terminalized after the reopen would leave a window where the journal has been
/// discarded and nothing says which answer the row was supposed to carry.
///
/// <para>The admission gate has a primitive for exactly this — a scoped permit over one exact
/// connection object — and the whole of it depends on that object being the same one the operation
/// store uses. This interface is how the coordinator names it without taking a dependency on the
/// database context: a scoped store, a scoped reconciler and a scoped coordinator already share one
/// context, and what is needed here is a way to say so rather than a second way to get one.</para>
/// </remarks>
internal interface ICovenantClosedPeriodLedgerConnection
{

    /// <summary>The exact connection object the operation store issues its statements on.</summary>
    DbConnection Connection { get; }

    /// <summary>
    /// Opens that connection under this installation's connection policy, or leaves it open.
    /// </summary>
    /// <remarks>
    /// Opening is a method here rather than the caller's own call on
    /// <see cref="Connection"/> because a bare <c>OpenAsync</c> on the context's connection object
    /// runs none of the policy: Entity Framework's connection interceptors fire for connections
    /// Entity Framework opens, and this one is opened by an erasure that took a scoped permit over it
    /// while ordinary admission is shut. What that silently loses is not a nicety —
    /// <c>secure_delete</c> and <c>foreign_keys</c> are applied and read back in exactly one place,
    /// and without them an erasure's deletions leave the erased bytes readable in the freed pages and
    /// leave every cascade-only child row behind. So the component that owns the connection owns the
    /// act of opening it, and there is no second way to do it.
    /// </remarks>
    Task OpenAsync(CancellationToken cancellationToken);

}

/// <summary>The scoped database context's own connection, which is what the store uses.</summary>
internal sealed class CovenantClosedPeriodLedgerConnection(
    ArcanumDbContext db,
    ICovenantSqliteConnectionInitializer initializer)
    : ICovenantClosedPeriodLedgerConnection
{

    private readonly ArcanumDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    private readonly ICovenantSqliteConnectionInitializer _initializer =
        initializer ?? throw new ArgumentNullException(nameof(initializer));

    public DbConnection Connection => _db.Database.GetDbConnection();

    /// <summary>
    /// Opens and initializes, in the same mode every ordinary connection to this database uses.
    /// </summary>
    /// <remarks>
    /// <see cref="CovenantSqliteConnectionMode.ReadWrite"/> rather than the exclusive maintenance mode
    /// the closed period's other handles use, because this is the ordinary durable ledger: it carries
    /// the erasure's own deletions and its terminal compare-exchange, and it has to behave exactly as
    /// it would outside a closed period. Taking the exclusive lock here would also put this handle in
    /// contention with the maintenance connections the same closed period opens.
    ///
    /// <para>Initialization runs on every open rather than once, because the window is opened and
    /// closed around each durable step and a fresh handle carries none of the previous one's pragmas.
    /// An already-open connection is left alone: it was opened through here.</para>
    /// </remarks>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = Connection;

        if (connection.State is System.Data.ConnectionState.Open)
        {

            return;

        }

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (connection is SqliteConnection sqlite)
        {

            await _initializer.InitializeAsync(
                sqlite,
                CovenantSqliteConnectionMode.ReadWrite,
                cancellationToken).ConfigureAwait(false);

        }

    }

}
