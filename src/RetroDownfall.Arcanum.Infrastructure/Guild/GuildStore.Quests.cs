using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildStore
{
    /// <summary>Pin visible Card metadata without disclosing its separately protected body.</summary>
    internal static async Task<Result<(Guid AgentId, Guid CardId, long Revision)>> SelectQuestCardWithinAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid agentId, GuildCardReadScope scope, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT a.AgentId,a.CardId,a.CurrentRevision FROM guild_agent_cards a
            JOIN guild_agent_card_revisions r ON r.CardId=a.CardId AND r.Revision=a.CurrentRevision
            JOIN guild_content c ON c.ContentId=r.ContentId
            WHERE a.AgentId=$id AND a.Available=1 AND a.ContentId=r.ContentId
                AND {LiveOwners} AND {LiveContent} AND {ScopePredicate}
                AND c.ContentKindCode=0 AND c.SourceRecordId=a.CardId AND c.Revision=a.CurrentRevision
                AND c.SessionId IS a.SessionId AND c.CampaignId IS a.CampaignId
                AND length(c.Body)<=65536 AND typeof(c.Digest)='blob' AND length(c.Digest)=32
            """);

        Add(command, "$id", Format(agentId));

        BindScope(command, scope);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? (Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2))
            : new Error(ErrorCodes.Guild.NotFound, "The Guild destination is unavailable in this owner scope.");
    }
}
