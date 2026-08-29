using System.Data.Common;

using System.Globalization;

using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Reads or lazily creates the installation's one <see cref="SagaSuppressionDigest"/> key.
/// </summary>
/// <remarks>
/// Takes the caller's connection and transaction rather than opening its own, exactly as
/// <c>AnnalsClaimWriter</c> does and for the same reason: the key has to commit or roll back with the
/// retirement that needed it. A key generated and then rolled back must not survive to bind a digest
/// the corresponding retirement never actually recorded.
/// </remarks>
internal static class SagaSuppressionKeyStore
{

    private const string TimestampFormat = "o";

    /// <summary>The installation's suppression key, or <see langword="null"/> when nothing has been retired.</summary>
    internal static async Task<byte[]?> ReadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT KeyMaterial FROM saga_suppression_key WHERE KeyId = 1";

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? null : (byte[])result;

    }

    /// <summary>
    /// The installation's suppression key, generating it inside the caller's transaction when this is
    /// the first retirement.
    /// </summary>
    /// <remarks>
    /// The insert is <c>INSERT OR IGNORE</c> followed by a read rather than a check-then-insert: two
    /// retirements racing on separate connections would both see no key and both try to write one, and
    /// the loser of that race has to end up holding the winner's key rather than an abort.
    /// </remarks>
    internal static async Task<byte[]> ReadOrCreateAsync(
        DbConnection connection,
        DbTransaction? transaction,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        byte[] candidate = RandomNumberGenerator.GetBytes(32);

        await using (DbCommand command = connection.CreateCommand())
        {

            command.Transaction = transaction;

            command.CommandText =
                """
                INSERT OR IGNORE INTO saga_suppression_key (KeyId, KeyMaterial, CreatedAtUtc)
                VALUES (1, @keyMaterial, @createdAt)
                """;

            AddParameter(command, "@keyMaterial", candidate);

            AddParameter(command, "@createdAt", Format(createdAt));

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        return await ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "saga_suppression_key holds no row immediately after INSERT OR IGNORE targeted it.");

    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value ?? DBNull.Value;

        _ = command.Parameters.Add(parameter);

    }

    private static string Format(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

}
