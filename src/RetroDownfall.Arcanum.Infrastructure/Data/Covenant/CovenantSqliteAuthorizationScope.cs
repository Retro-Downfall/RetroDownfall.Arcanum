using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// A live grant of one privileged operation on one connection, valid until disposed.
/// </summary>
/// <remarks>
/// Not constructible outside the initializer, not serializable, and bound to the exact connection it
/// was issued for: a scope obtained on one connection cannot be used to authorize a write on
/// another. Nesting is counted per kind, so an inner scope's disposal cannot revoke an outer one
/// that is still open.
/// </remarks>
internal sealed class CovenantSqliteAuthorizationScope : IDisposable
{

    private readonly CovenantSqliteConnectionState _state;

    private readonly SqliteConnection _connection;

    private bool _disposed;

    internal CovenantSqliteAuthorizationScope(
        CovenantSqliteConnectionState state,
        SqliteConnection connection,
        CovenantSqliteAuthorizationKind kind)
    {

        _state = state;

        _connection = connection;

        Kind = kind;

    }

    internal CovenantSqliteAuthorizationKind Kind { get; }

    /// <summary>
    /// The connection this grant applies to. Used to reject an attempt to carry a scope across
    /// connections.
    /// </summary>
    internal bool AppliesTo(SqliteConnection connection) => ReferenceEquals(_connection, connection);

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _state.Release(Kind);

    }

}
