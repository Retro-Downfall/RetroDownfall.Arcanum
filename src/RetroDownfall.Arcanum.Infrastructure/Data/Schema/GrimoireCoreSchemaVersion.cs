using System.Data.Common;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Reads the committed Core version on the caller's connection and snapshot; never caches it.</summary>
internal static class GrimoireCoreSchemaVersion
{
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

        command.CommandText = "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 0 AND TransactionTierCode = 0";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is long version && version is > 0 and <= int.MaxValue
            ? (int)version
            : throw new InvalidDataException("The committed Core schema version is missing or invalid.");
    }
}
