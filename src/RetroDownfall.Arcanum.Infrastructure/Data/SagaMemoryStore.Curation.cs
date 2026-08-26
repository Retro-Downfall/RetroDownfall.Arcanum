using System.Data.Common;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class SagaMemoryStore
{

    public async Task<SagaMemoryCurationRow?> ReadCurationRowAsync(string id, CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                // One LEFT JOIN against saga_memory_embeddings rather than a second round trip: the row,
                // its lifecycle, and whether an embedding still exists are three facts about the same
                // instant, and a caller reading them separately would be describing three instants as
                // though they were one.
                cmd.CommandText =
                    """
                    SELECT m."Id", m."Content", m."CreatedAt", m."SessionId", m."Tags", m."Source",
                           p.SessionId, p.AttachmentId, p.LogicalKey, p.Version,
                           p.ContentHash, p.MaterializedAt, p.SourceType,
                           EXISTS(
                               SELECT 1 FROM "SessionAttachments" a
                               WHERE a."Id" = p.AttachmentId AND a."State" = 'Bound'
                           ),
                           m.ScopeKindCode, m.CampaignId, m."RetiredAtUtc", m."PinnedAtUtc",
                           e."MemoryId" IS NOT NULL
                    FROM "saga_memories" m
                    LEFT JOIN saga_memory_attachment_provenance p ON p.MemoryId = m."Id"
                    LEFT JOIN "saga_memory_embeddings" e ON e."MemoryId" = m."Id"
                    WHERE m."Id" = @id
                    """;

                AddParameter(cmd, "@id", id);

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    return null;

                }

                SagaMemoryDto memory = ReadMemory(reader);

                bool hasEmbedding = reader.GetInt32(18) == 1;

                SagaMemoryLifecycle lifecycle = new(memory.RetiredAtUtc, memory.PinnedAtUtc);

                return new SagaMemoryCurationRow(memory, lifecycle, hasEmbedding);

            },
            cancellationToken).ConfigureAwait(false);

    }

}
