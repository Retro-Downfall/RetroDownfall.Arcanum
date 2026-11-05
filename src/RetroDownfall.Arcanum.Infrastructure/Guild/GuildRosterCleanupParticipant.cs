using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed class GuildRosterCleanupParticipant : IGuildOwnerCleanupParticipant
{
    public async ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"UPDATE guild_agent_cards SET Available=0,ReasonCode='Guild.OwnerUnavailable' WHERE {(owner.Kind == 1 ? "CampaignId" : "SessionId")}=$owner COLLATE NOCASE;";

        command.Parameters.AddWithValue("$owner", owner.Id.ToString("D").ToUpperInvariant());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    public ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Result.Success());
    }

    public async ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        string column = owner.Kind == 1 ? "CampaignId" : "SessionId";

        command.CommandText = (owner.Kind == 1 ? """
            DELETE FROM guild_declared_source_claims WHERE RootKey IN(
                SELECT RootKey FROM guild_declared_roots WHERE WorkspaceId=$owner COLLATE NOCASE);
            DELETE FROM guild_declared_roots WHERE WorkspaceId=$owner COLLATE NOCASE;
            """ : string.Empty) + $"""
            DELETE FROM guild_agent_card_skills WHERE CardId IN(SELECT CardId FROM guild_agent_cards WHERE {column}=$owner COLLATE NOCASE);
            DELETE FROM guild_agent_card_revisions WHERE CardId IN(SELECT CardId FROM guild_agent_cards WHERE {column}=$owner COLLATE NOCASE);
            DELETE FROM guild_agent_cards WHERE {column}=$owner COLLATE NOCASE;
            """;

        command.Parameters.AddWithValue("$owner", owner.Id.ToString("D").ToUpperInvariant());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
