using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The canonical tier's record of which master key its dataset-keyed envelopes are derived under.
/// </summary>
/// <remarks>
/// The core authority row already converges on a master-key change, advancing the master version,
/// authority epoch, and recovery envelope epoch. That covers purposes four through six, which are keyed
/// by installation identity. Purposes one through three are keyed by Covenant dataset state, and their
/// epoch lives here so that a canonical tier which is absent or damaged simply does not advance
/// (§10.12).
///
/// <para>That separation is the point. A key rotation must not be blocked by an unhealthy optional
/// capability, and an unhealthy capability must not be handed a fresh epoch it never wrote. Startup
/// therefore converges the core row unconditionally and this row only inside the canonical install
/// transaction.</para>
/// </remarks>
internal static class CovenantEnvelopeStateStore
{

    /// <summary>
    /// Reads the canonical envelope-key identity, or <see langword="null"/> when no state row exists.
    /// </summary>
    internal static async Task<CovenantEnvelopeStateRow?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT DatasetGeneration,
                   EnvelopeMasterKeyVersion,
                   EnvelopeMasterKeyFingerprint,
                   EnvelopeKeyEpoch
            FROM covenant_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.GetValue(0) is not byte[] datasetGeneration
            || datasetGeneration.Length != 16
            || reader.GetValue(2) is not byte[] fingerprint
            || fingerprint.Length != 32)
        {

            throw new InvalidOperationException(
                "Covenant canonical envelope state is malformed; the capability fails closed rather than reseeding it.");

        }

        return new CovenantEnvelopeStateRow(
            new Guid(datasetGeneration, bigEndian: true),
            checked((uint)reader.GetInt64(1)),
            fingerprint,
            reader.GetInt64(3));

    }

    /// <summary>
    /// Advances the canonical envelope key identity when, and only when, the master key changed.
    /// </summary>
    /// <returns>The state in force after convergence.</returns>
    /// <remarks>
    /// Advances from the value already stored rather than from anything the caller supplies, exactly as
    /// the core authority row does. Rotating back to a previously used key therefore produces a new,
    /// higher epoch instead of restoring the old one, so a token minted under the first use of that key
    /// cannot authenticate after the round trip.
    /// </remarks>
    internal static async Task<CovenantEnvelopeStateRow> ConvergeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        uint currentMasterKeyVersion,
        byte[] currentMasterKeyFingerprint,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(currentMasterKeyFingerprint);

        CovenantEnvelopeStateRow existing =
            await ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Covenant canonical envelope state is absent; the capability fails closed rather than seeding it here.");

        if (CryptographicOperations.FixedTimeEquals(
            existing.MasterKeyFingerprint,
            currentMasterKeyFingerprint))
        {
            return existing;
        }

        long nextEpoch = existing.EnvelopeKeyEpoch == long.MaxValue
            ? throw new InvalidOperationException("The Covenant envelope key epoch is exhausted.")
            : existing.EnvelopeKeyEpoch + 1;

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE covenant_state
            SET EnvelopeMasterKeyVersion = $masterKeyVersion,
                EnvelopeMasterKeyFingerprint = $masterKeyFingerprint,
                EnvelopeKeyEpoch = $envelopeKeyEpoch,
                UpdatedAtUtc = $updatedAtUtc
            WHERE StateKey = 1;
            """;

        _ = command.Parameters.AddWithValue("$masterKeyVersion", (long)currentMasterKeyVersion);

        _ = command.Parameters.AddWithValue("$masterKeyFingerprint", currentMasterKeyFingerprint);

        _ = command.Parameters.AddWithValue("$envelopeKeyEpoch", nextEpoch);

        _ = command.Parameters.AddWithValue(
            "$updatedAtUtc",
            updatedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return existing with
        {
            MasterKeyVersion = currentMasterKeyVersion,
            MasterKeyFingerprint = currentMasterKeyFingerprint,
            EnvelopeKeyEpoch = nextEpoch,
        };

    }

}

/// <summary>
/// The canonical tier's envelope-key identity, all of it nonsecret.
/// </summary>
internal sealed record CovenantEnvelopeStateRow(
    Guid DatasetGeneration,
    uint MasterKeyVersion,
    byte[] MasterKeyFingerprint,
    long EnvelopeKeyEpoch);
