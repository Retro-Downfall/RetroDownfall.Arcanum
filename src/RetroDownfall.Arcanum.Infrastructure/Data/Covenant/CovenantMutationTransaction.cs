using System.Data;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// One caller-owned immediate write transaction over a centrally initialized Grimoire connection.
/// </summary>
/// <remarks>
/// Ownership is the whole point. The mutation kernel and the quota guard both write through this
/// object and neither may open, commit, roll back, or retry it: Covenant publication commits
/// atomically with the assistant response, and a component that could commit on its own would break
/// that atomicity from the inside.
///
/// <para>Busy retry belongs to the owner too. Retrying part of a transaction is not a retry, it is a
/// second, partial transaction.</para>
/// </remarks>
internal sealed class CovenantMutationTransaction
{

    internal CovenantMutationTransaction(SqliteConnection connection, SqliteTransaction transaction)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        if (connection.State != ConnectionState.Open)
        {

            throw new InvalidOperationException(
                "A Covenant mutation transaction requires an open Grimoire connection.");

        }

        if (!ReferenceEquals(transaction.Connection, connection))
        {

            throw new ArgumentException(
                "The Covenant mutation transaction must belong to the supplied connection.",
                nameof(transaction));

        }

        Connection = connection;

        Transaction = transaction;

    }

    internal SqliteConnection Connection { get; }

    internal SqliteTransaction Transaction { get; }

    /// <summary>
    /// Creates a command already attached to this transaction, so no caller can accidentally run a
    /// Covenant write outside it.
    /// </summary>
    internal SqliteCommand CreateCommand()
    {

        SqliteCommand command = Connection.CreateCommand();

        command.Transaction = Transaction;

        return command;

    }

}
