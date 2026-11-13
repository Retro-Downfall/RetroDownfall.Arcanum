using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Checks the selected Quest ancestry against durable owner-erasure evidence.</summary>
internal static class GuildSpendRefreshOwnerProof
{
    internal static async Task<Result> RequireWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid questId, Guid rootQuestId, CancellationToken ct)
    {
        if (questId == Guid.Empty || rootQuestId == Guid.Empty) return Unavailable;

        HashSet<Guid> visited = [];

        Guid current = questId;

        while (true)
        {
            if (!visited.Add(current)) return Unavailable;

            Guid? parent;

            Guid? session;

            Guid? campaign;

            Guid? workspace;

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                command.CommandText = "SELECT Id,RootQuestId,ParentQuestId,SessionId,CampaignId,WorkspaceId FROM guild_quests WHERE Id=$id";

                command.Parameters.AddWithValue("$id", Format(current));

                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                    || !TryId(reader.GetValue(0), out Guid? rowId) || rowId != current
                    || !TryId(reader.GetValue(1), out Guid? root) || root != rootQuestId
                    || !TryNullableId(reader.GetValue(2), out parent)
                    || !TryId(reader.GetValue(3), out session)
                    || !TryNullableId(reader.GetValue(4), out campaign)
                    || !TryNullableId(reader.GetValue(5), out workspace)
                    || (workspace is not null && workspace != campaign)) return Unavailable;
            }

            Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, session, campaign, ct).ConfigureAwait(false);

            if (owner.IsFailure) return Unavailable;

            if (current == rootQuestId) return parent is null ? Result.Success() : Unavailable;

            if (parent is not { } next) return Unavailable;

            current = next;
        }
    }

    private static bool TryNullableId(object value, out Guid? id)
    {
        if (value is DBNull)
        {
            id = null;

            return true;
        }

        return TryId(value, out id);
    }

    private static bool TryId(object value, out Guid? id)
    {
        id = null;

        if (value is not string text || !Guid.TryParseExact(text, "D", out Guid parsed) || parsed == Guid.Empty
            || !string.Equals(text, Format(parsed), StringComparison.Ordinal)) return false;

        id = parsed;

        return true;
    }

    private static string Format(Guid id) => id.ToString("D").ToUpperInvariant();

    private static Error Unavailable => new(ErrorCodes.Guild.OwnerUnavailable, "The Guild owner is unavailable.");
}
