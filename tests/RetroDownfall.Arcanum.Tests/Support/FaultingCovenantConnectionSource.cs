using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class FaultingCovenantConnectionSource(int code) : ICovenantConnectionSource
{
    private Exception Failure() => code switch
    {
        -1 => new IOException("PRIVATE-storage-path/key/SQL"),
        -2 => new UnauthorizedAccessException("PRIVATE-storage-path/key/SQL"),
        _ => new SqliteException("PRIVATE-storage-path/key/SQL", code),
    };

    public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<SqliteConnection>(Failure());

    public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
        GetOpenConnectionAsync(cancellationToken);
}
