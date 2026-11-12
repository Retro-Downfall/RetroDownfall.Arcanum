using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal static partial class GuildStorageIntegrity
{
    private static async Task ValidateOperatorSequencesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM guild_cohorts c WHERE NOT EXISTS(
                    SELECT 1 FROM guild_operator_sequences s WHERE s.KindCode=1 AND s.ObjectId=c.Id))
                OR EXISTS(SELECT 1 FROM guild_workspace_leases l WHERE NOT EXISTS(
                    SELECT 1 FROM guild_operator_sequences s WHERE s.KindCode=2 AND s.ObjectId=l.Id))
                OR EXISTS(SELECT 1 FROM guild_quests q WHERE NOT EXISTS(
                    SELECT 1 FROM guild_operator_sequences s WHERE s.KindCode=3 AND s.ObjectId=q.Id AND s.Sequence=q.IssuedSequence))
                OR EXISTS(SELECT 1 FROM guild_operator_sequences s WHERE
                    (s.KindCode=1 AND NOT EXISTS(SELECT 1 FROM guild_cohorts c WHERE c.Id=s.ObjectId))
                    OR (s.KindCode=2 AND NOT EXISTS(SELECT 1 FROM guild_workspace_leases l WHERE l.Id=s.ObjectId))
                    OR (s.KindCode=3 AND NOT EXISTS(SELECT 1 FROM guild_quests q WHERE q.Id=s.ObjectId AND q.IssuedSequence=s.Sequence)))
                OR EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='guild_operator_sequences' AND (typeof(seq)<>'integer' OR seq<0))
                OR coalesce((SELECT seq FROM sqlite_sequence WHERE name='guild_operator_sequences'),0)
                    < coalesce((SELECT max(Sequence) FROM guild_operator_sequences),0)
            """;

        if (!Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))) throw new GuildStorageIntegrityException();
    }
}
