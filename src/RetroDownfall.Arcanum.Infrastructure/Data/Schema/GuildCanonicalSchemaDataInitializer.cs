using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

internal sealed class GuildCanonicalSchemaDataInitializer : IGrimoireSchemaDataInitializer
{
    public GrimoireSchemaTransactionTier TransactionTier => GrimoireSchemaTransactionTier.GuildCanonical;

    public async Task InitializeAsync(SqliteConnection connection, SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context, CancellationToken cancellationToken)
    {
        await GuildStorageIntegrity.ValidateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "INSERT INTO capability_cleanup_state(CapabilityFamilyCode,AppliedCampaignSequence,AppliedSessionSequence,FullSweepRequired,UpdatedAtUtc) VALUES(2,0,0,1,$now) ON CONFLICT(CapabilityFamilyCode) DO NOTHING";

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(context.InstalledAtUtc));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
