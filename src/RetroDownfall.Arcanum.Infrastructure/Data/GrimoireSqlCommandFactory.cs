using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Creates direct Grimoire commands on the connection and transaction owned by a scoped EF context.
/// </summary>
/// <remarks>
/// This is the reviewed bridge used by context-backed repositories migrated from EF query execution.
/// It never creates, replaces, or disposes a connection, and every command joins the context's current
/// transaction. Specialized stores that already own an admitted connection or transition transaction
/// keep that explicit owner instead of routing through this factory.
/// </remarks>
internal static class GrimoireSqlCommandFactory
{
    public static async Task<SqliteCommand> CreateAsync(
        ArcanumDbContext db,
        string commandText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        if (db.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException(
                "Direct Grimoire commands require the scoped SQLite connection.");
        }

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;

        if (db.Database.CurrentTransaction?.GetDbTransaction() is { } transaction)
        {
            if (transaction is not SqliteTransaction sqliteTransaction)
            {
                await command.DisposeAsync().ConfigureAwait(false);

                throw new InvalidOperationException(
                    "The scoped Grimoire transaction is not a SQLite transaction.");
            }

            command.Transaction = sqliteTransaction;
        }

        return command;
    }
}
