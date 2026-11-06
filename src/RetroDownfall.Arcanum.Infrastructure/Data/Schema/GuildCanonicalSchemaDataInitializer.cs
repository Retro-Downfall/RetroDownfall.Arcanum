using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

internal sealed class GuildCanonicalSchemaDataInitializer : IGrimoireSchemaDataInitializer
{
    public GrimoireSchemaTransactionTier TransactionTier => GrimoireSchemaTransactionTier.GuildCanonical;

    public async Task InitializeAsync(SqliteConnection connection, SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context, CancellationToken cancellationToken)
    {
        await using (SqliteCommand epoch = connection.CreateCommand())
        {
            epoch.Transaction = transaction;

            epoch.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='guild_signet_authority_state'";

            if (Equals(1L, await epoch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
            {
                epoch.CommandText = "INSERT INTO guild_signet_authority_state(StateKey,Epoch) VALUES(1,$epoch) ON CONFLICT(StateKey) DO NOTHING";

                epoch.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("D").ToUpperInvariant());

                _ = await epoch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await GuildStorageIntegrity.ValidateAsync(connection, transaction, cancellationToken,
            context.DeclaredSchemaVersion == 0 ? GrimoireSchemaVersionChains.GuildCanonicalSchemaVersion : context.DeclaredSchemaVersion).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "INSERT INTO capability_cleanup_state(CapabilityFamilyCode,AppliedCampaignSequence,AppliedSessionSequence,FullSweepRequired,UpdatedAtUtc) VALUES(2,0,0,1,$now) ON CONFLICT(CapabilityFamilyCode) DO NOTHING";

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(context.InstalledAtUtc));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
