using System.Data.Common;

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

}

/// <summary>The scoped database context's own connection, which is what the store uses.</summary>
internal sealed class CovenantClosedPeriodLedgerConnection(ArcanumDbContext db)
    : ICovenantClosedPeriodLedgerConnection
{

    private readonly ArcanumDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public DbConnection Connection => _db.Database.GetDbConnection();

}
