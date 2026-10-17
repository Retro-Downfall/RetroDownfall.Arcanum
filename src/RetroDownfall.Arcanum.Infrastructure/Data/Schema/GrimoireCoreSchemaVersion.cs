using System.Data.Common;

using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Reads the committed Core version on the caller's connection and snapshot; never caches it.</summary>
internal static class GrimoireCoreSchemaVersion
{
    private const string SelectCoreVersion = "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 0 AND TransactionTierCode = 0";

    internal static async Task<T> InSnapshotAsync<T>(
        DbConnection connection,
        Func<Task<T>> read,
        CancellationToken cancellationToken)
    {
        await using DbCommand boundary = connection.CreateCommand();

        boundary.CommandText = "BEGIN DEFERRED";

        _ = await boundary.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            T result = await read().ConfigureAwait(false);

            boundary.CommandText = "COMMIT";

            _ = await boundary.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }
        catch
        {
            boundary.CommandText = "ROLLBACK";

            _ = await boundary.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    internal static async Task<int> ReadAsync(
        DbConnection connection,
        CancellationToken cancellationToken,
        DbTransaction? transaction = null)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = SelectCoreVersion;

        return Committed(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The same read for a caller that already holds the concrete provider connection. Reading through the
    /// provider types keeps the command and its disposal on the exact provider contract, where the abstract
    /// overload cannot name the command it creates.
    /// </summary>
    internal static async Task<int> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = SelectCoreVersion;

        return Committed(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static int Committed(object? value) =>
        value is long version && version is > 0 and <= int.MaxValue
            ? (int)version
            : throw new InvalidDataException("The committed Core schema version is missing or invalid.");
}
