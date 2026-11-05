using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal static class GuildStorageAvailability
{
    internal static async Task<Result> CheckAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        try
        {
            foreach (GrimoireSchemaManifest manifest in new[] { GrimoireSchemaManifests.Core, GrimoireSchemaManifests.GuildCanonical })
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = "SELECT COUNT(*) FROM grimoire_feature_schemas WHERE FamilyCode=$family AND TransactionTierCode=$tier AND SchemaVersion=$version AND SourceDefinitionFingerprint=$source AND HealthCode=0";

                command.Parameters.AddWithValue("$family", (int)manifest.Family);

                command.Parameters.AddWithValue("$tier", (int)manifest.TransactionTier);

                command.Parameters.AddWithValue("$version", manifest.Version);

                command.Parameters.AddWithValue("$source", manifest.SourceDefinitionFingerprint);

                if (!Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)))
                {
                    return Unavailable();
                }

                GrimoireSchemaManifestInspector inspector = new(GrimoireSchemaTierOwnershipRegistry.CreateDefault());

                if (!(await inspector.InspectAsync(connection, transaction, manifest, ct).ConfigureAwait(false)).IsValid)
                {
                    return Unavailable();
                }
            }

            return Result.Success();
        }
        catch (SqliteException)
        {
            return Unavailable();
        }
    }

    internal static async Task<Result> CheckCovenantAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        GrimoireSchemaManifest manifest = GrimoireSchemaManifests.CovenantCanonical;

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT COUNT(*) FROM grimoire_feature_schemas WHERE FamilyCode=1 AND TransactionTierCode=1 AND SchemaVersion=$version AND SourceDefinitionFingerprint=$source AND HealthCode=0";

        command.Parameters.AddWithValue("$version", manifest.Version);

        command.Parameters.AddWithValue("$source", manifest.SourceDefinitionFingerprint);

        GrimoireSchemaManifestInspector inspector = new(GrimoireSchemaTierOwnershipRegistry.CreateDefault());

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))
            && (await inspector.InspectAsync(connection, transaction, manifest, ct).ConfigureAwait(false)).IsValid
                ? Result.Success() : new Error(ErrorCodes.Guild.AuthorityUnavailable, "Protected Guild content requires healthy Covenant storage.");
    }

    private static Error Unavailable() => new(ErrorCodes.Guild.Unavailable, "Guild storage is unavailable.");
}
