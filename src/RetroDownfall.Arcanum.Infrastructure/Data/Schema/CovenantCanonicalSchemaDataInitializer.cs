using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Seeds the single <c>covenant_state</c> row inside the Covenant canonical install transaction.
/// </summary>
/// <remarks>
/// A fresh dataset starts at canonical sequence zero with a null applied FTS tuple and
/// <see cref="CovenantFtsRebuildState.FullRebuildRequired"/>, because an accelerator that has never
/// been built is behind by definition and must not be trusted to answer a query.
///
/// <para>The applied core deletion sequences are read from the always-present core journal rather
/// than seeded to zero. Zero would claim this capability had already applied nothing when the
/// journal may already hold events, which would let Covenant skip cleanup it actually owes.</para>
///
/// <para>Repeat installation verifies the existing singleton and changes nothing: the dataset
/// generation is the identity every in-flight turn snapshot binds, so silently reissuing it on a
/// reopen would invalidate live work.</para>
/// </remarks>
internal sealed class CovenantCanonicalSchemaDataInitializer : IGrimoireSchemaDataInitializer
{

    public GrimoireSchemaTransactionTier TransactionTier =>
        GrimoireSchemaTransactionTier.CovenantCanonical;

    public async Task InitializeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(context);

        if (await StateRowExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {

            await VerifyExistingStateAsync(connection, transaction, context, cancellationToken)
                .ConfigureAwait(false);

            // Converging here rather than in the core transaction is what keeps a master-key rotation
            // from being blocked by an unhealthy canonical tier: the core row always advances, and the
            // dataset-keyed envelope epoch advances only where its dataset actually lives (§10.12).
            _ = await CovenantEnvelopeStateStore.ConvergeAsync(
                connection,
                transaction,
                context.MasterKeyVersion,
                context.MasterKeyFingerprint,
                context.InstalledAtUtc,
                cancellationToken).ConfigureAwait(false);

            return;

        }

        long campaignSequence = await ReadOwnerSequenceAsync(
            connection,
            transaction,
            ownerKindCode: 1,
            cancellationToken).ConfigureAwait(false);

        long sessionSequence = await ReadOwnerSequenceAsync(
            connection,
            transaction,
            ownerKindCode: 2,
            cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO covenant_state (
                StateKey,
                DatasetGeneration,
                CanonicalSearchSequence,
                AppliedDatasetGeneration,
                AppliedSearchSequence,
                AppliedCampaignDeletionSequence,
                AppliedSessionDeletionSequence,
                AcceleratorEpoch,
                KeyReclamationEpoch,
                EnvelopeMasterKeyVersion,
                EnvelopeMasterKeyFingerprint,
                EnvelopeKeyEpoch,
                NextSearchRowId,
                RebuildStateCode,
                RebuildTargetSequence,
                RebuildCursor,
                UpdatedAtUtc)
            VALUES (
                1,
                $datasetGeneration,
                0,
                NULL,
                NULL,
                $campaignSequence,
                $sessionSequence,
                1,
                1,
                $masterKeyVersion,
                $masterKeyFingerprint,
                1,
                1,
                $rebuildStateCode,
                NULL,
                NULL,
                $updatedAtUtc);
            """;

        _ = command.Parameters.AddWithValue("$datasetGeneration", RandomNumberGenerator.GetBytes(16));

        _ = command.Parameters.AddWithValue("$campaignSequence", campaignSequence);

        _ = command.Parameters.AddWithValue("$sessionSequence", sessionSequence);

        // Stored in checked signed storage: the version is an unsigned counter in the domain, and
        // SQLite has no unsigned integer type.
        _ = command.Parameters.AddWithValue("$masterKeyVersion", (long)context.MasterKeyVersion);

        _ = command.Parameters.AddWithValue("$masterKeyFingerprint", context.MasterKeyFingerprint);

        _ = command.Parameters.AddWithValue(
            "$rebuildStateCode",
            (long)CovenantFtsRebuildState.FullRebuildRequired);

        _ = command.Parameters.AddWithValue("$updatedAtUtc", FormatTimestamp(context.InstalledAtUtc));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<bool> StateRowExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT COUNT(*) FROM covenant_state;";

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) > 0;

    }

    private static async Task VerifyExistingStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT COUNT(*)
            FROM covenant_state
            WHERE StateKey = 1
              AND length(DatasetGeneration) = 16
              AND EnvelopeMasterKeyVersion > 0;
            """;

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) != 1)
        {

            throw new InvalidOperationException(
                "Covenant canonical state is present but malformed; the capability fails closed rather than reseeding it.");

        }

    }

    private static async Task<long> ReadOwnerSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ownerKindCode,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT COALESCE(MAX(Sequence), 0)
            FROM owner_deletion_events
            WHERE OwnerKindCode = $ownerKindCode;
            """;

        _ = command.Parameters.AddWithValue("$ownerKindCode", ownerKindCode);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);

    }

}
