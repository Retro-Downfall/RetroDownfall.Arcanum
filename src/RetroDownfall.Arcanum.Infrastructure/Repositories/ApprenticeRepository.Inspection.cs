using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed partial class ApprenticeRepository
{
    private const string LinkedApprenticeColumns = GrimoireEntitySql.ApprenticeColumns
        + ", (SELECT QuestId FROM guild_local_executions WHERE ApprenticeId=\"Apprentices\".\"Id\") AS GuildQuestId";

    public async Task<Result<Apprentice?>> GetLinkedByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            await using IDbContextTransaction? owned = _db.Database.CurrentTransaction is null
                ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;

            Result<bool> available = await GuildLinkageAvailableWithinAsync(cancellationToken).ConfigureAwait(false);

            if (available.IsFailure) return available.Error;

            string columns = available.Value ? LinkedApprenticeColumns : GrimoireEntitySql.ApprenticeColumns;

            Apprentice? apprentice = await ReadSingleAsync(
                $"SELECT {columns} FROM \"Apprentices\" WHERE \"Id\" = $id LIMIT 1;",
                command => GrimoireEntitySql.AddParameter(command, "$id", GrimoireEntitySql.Format(id)),
                cancellationToken).ConfigureAwait(false);

            if (apprentice is not null)
                apprentice.ParentApprenticeId = DeserializeCheckpoint(apprentice.CheckpointData)?.ParentApprenticeId;

            return Result<Apprentice?>.Success(apprentice);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            return GuildOwnershipUnavailable;
        }
    }

    public async Task<Result<ListPageResult<Apprentice>>> ListLinkedAsync(
        Guid? campaignId, string? status, Guid? parentId, Guid? rootId, int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using IDbContextTransaction? owned = _db.Database.CurrentTransaction is null
                ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;

            Result<bool> available = await GuildLinkageAvailableWithinAsync(cancellationToken).ConfigureAwait(false);

            if (available.IsFailure) return available.Error;

            if (!available.Value && (parentId is not null || rootId is not null))
                return new ListPageResult<Apprentice>([], false);

            return await ListWithinAsync(campaignId, status, limit, beforeUpdatedAt,
                parentId, rootId, available.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return GuildOwnershipUnavailable;
        }
    }

    private static Apprentice ReadInspectedApprentice(SqliteDataReader reader)
    {
        Apprentice apprentice = GrimoireEntitySql.ReadApprentice(reader);

        if (reader.FieldCount == 14 && !reader.IsDBNull(13))
            apprentice.GuildQuestId = Guid.Parse(reader.GetString(13));

        return apprentice;
    }
}
