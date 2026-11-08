using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed partial class ApprenticeRepository
{
    public async Task<Result<bool>> IsGuildBoundAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) return new Error(ErrorCodes.Guild.InvalidRequest, "An exact Apprentice identity is required.");

        try
        {
            if (_db.Database.CurrentTransaction is not null)
                return await IsGuildBoundWithinAsync(id, cancellationToken).ConfigureAwait(false);

            await using IDbContextTransaction owned = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            return await IsGuildBoundWithinAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            return GuildOwnershipUnavailable;
        }
    }

    private async Task<Result<bool>> IsGuildBoundWithinAsync(Guid id, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(_db, """
            SELECT (SELECT count(*) FROM grimoire_feature_schemas WHERE FamilyCode=2),
                (SELECT count(*) FROM sqlite_schema WHERE lower(name) GLOB 'guild_*' OR lower(name) GLOB 'idx_guild_*' OR lower(name) GLOB 'ux_guild_*')
            """, cancellationToken).ConfigureAwait(false);

        long registrations;

        long artifacts;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return GuildOwnershipUnavailable;

            registrations = reader.GetInt64(0);

            artifacts = reader.GetInt64(1);
        }

        if (registrations == 0)
        {
            if (artifacts != 0) return GuildOwnershipUnavailable;

            GrimoireSchemaManifest core = GrimoireSchemaManifests.Core;

            command.CommandText = """
                SELECT count(*) FROM grimoire_feature_schemas WHERE FamilyCode=$family AND TransactionTierCode=$tier
                    AND SchemaVersion=$version AND SourceDefinitionFingerprint=$source AND HealthCode=0
                """;

            command.Parameters.AddWithValue("$family", (int)core.Family);

            command.Parameters.AddWithValue("$tier", (int)core.TransactionTier);

            command.Parameters.AddWithValue("$version", core.Version);

            command.Parameters.AddWithValue("$source", core.SourceDefinitionFingerprint);

            if (!Equals(1L, await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))) return GuildOwnershipUnavailable;

            GrimoireSchemaManifestInspector inspector = new(GrimoireSchemaTierOwnershipRegistry.CreateDefault());

            if (!(await inspector.InspectAsync(command.Connection!, command.Transaction, core, cancellationToken).ConfigureAwait(false)).IsValid)
                return GuildOwnershipUnavailable;

            return false;
        }

        if (registrations != 1) return GuildOwnershipUnavailable;

        Result available = await GuildStorageAvailability.CheckAsync(command.Connection!, command.Transaction, cancellationToken).ConfigureAwait(false);

        if (available.IsFailure) return available.Error;

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM guild_local_executions WHERE ApprenticeId=$id)";

        command.Parameters.AddWithValue("$id", id.ToString("D").ToUpperInvariant());

        return Equals(1L, await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static Error GuildOwnershipUnavailable => new(ErrorCodes.Guild.Unavailable,
        "Durable Guild execution ownership could not be established.");
}
