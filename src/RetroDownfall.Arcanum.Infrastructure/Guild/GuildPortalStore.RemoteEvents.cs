using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    private static async Task<Result<GuildChangeEventDto>> ProjectChangeWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, PortalEvent row, CancellationToken ct)
    {
        GuildPortalDeliveryEvidence? evidence = null;

        if (row.Source == GuildPortalEventSource.Inbox && row.Kind == GuildEventKind.MessageCommitted && row.EnvelopeId is { } envelope)
        {
            await using SqliteCommand read = Command(connection, transaction, """
                SELECT CASE WHEN length(CAST(i.RecipientQuestId AS BLOB))=36 THEN i.RecipientQuestId END,
                    i.DeliveryStateCode,d.TransportStateCode,
                    CASE WHEN length(CAST(d.PeerReceiptId AS BLOB))=36 THEN d.PeerReceiptId END
                FROM guild_inbox i LEFT JOIN guild_remote_portal_deliveries d
                  ON d.InboxId=i.Id AND d.RecipientQuestId=i.RecipientQuestId AND d.EnvelopeId=i.EnvelopeId
                WHERE i.Id=$inbox AND i.EnvelopeId=$envelope AND i.RecipientQuestId IS NOT NULL AND
                  (d.InboxId IS NOT NULL OR EXISTS(SELECT 1 FROM guild_inbox sibling
                    JOIN guild_remote_portal_deliveries remote ON remote.InboxId=sibling.Id WHERE sibling.EnvelopeId=i.EnvelopeId))
                """);

            Add(read, "$inbox", Id(row.ObjectId));

            Add(read, "$envelope", Id(envelope));

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!ReadRemoteReceiptId(reader, 0, out Guid recipient)
                    || reader.GetValue(1) is not long state || state is < 1 or > 3) return Unavailable;

                GuildPortalTransportEvidence? transport = null;

                Guid? remoteReceipt = null;

                if (!reader.IsDBNull(2))
                {
                    if (reader.GetValue(2) is not long phase || phase is < 0 or > 2) return Unavailable;

                    transport = (GuildPortalTransportEvidence)phase;

                    if (transport == GuildPortalTransportEvidence.PeerAccepted)
                    {
                        if (!ReadRemoteReceiptId(reader, 3, out Guid peerReceipt)) return Unavailable;

                        remoteReceipt = peerReceipt;
                    }
                }

                evidence = new(row.ObjectId, envelope, recipient, (GuildDeliveryState)state, transport, remoteReceipt);
            }
        }

        return new GuildChangeEventDto(row.Sequence, row.Kind, row.ObjectId, row.Revision, [], row.QuestId, row.SpendSource, evidence);
    }
}
