using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

internal sealed partial class MemorySourceProofReader
{
    private static async Task VerifyOwnersAsync(SqliteConnection connection, SqliteTransaction transaction,
        MemoryEvaluationSourceCoordinate source, CovenantCanonicalHashWriter metadata, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT
                COALESCE((SELECT max(Sequence) FROM owner_deletion_events WHERE OwnerKindCode = 1 AND OwnerId = $campaign COLLATE NOCASE), 0),
                COALESCE((SELECT max(Sequence) FROM owner_deletion_events WHERE OwnerKindCode = 2 AND OwnerId = $owner COLLATE NOCASE), 0),
                COALESCE((SELECT max(Sequence) FROM owner_deletion_events WHERE OwnerKindCode = 2 AND OwnerId = $author COLLATE NOCASE), 0),
                $author IS NULL OR EXISTS(SELECT 1 FROM Sessions WHERE Id = $author COLLATE NOCASE)
            """, ("$campaign", source.CampaignId?.ToString("D").ToUpperInvariant()),
            ("$owner", source.CanonicalOwnerSessionId), ("$author", source.SourceSessionId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        Require(await reader.ReadAsync(ct).ConfigureAwait(false));

        Require(reader.GetInt64(0) == 0 && reader.GetInt64(1) == 0 && reader.GetInt64(2) == 0 && reader.GetBoolean(3));

        HashRow(metadata, reader);
    }
}
