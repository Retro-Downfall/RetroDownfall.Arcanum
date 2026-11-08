using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Core.Guild;
using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed class SessionAttachmentRetrievalService(
    IOptionsMonitor<ArcanumSettings> options,
    IDivinationService divination,
    SessionAttachmentIndexRepository index,
    ILogger<SessionAttachmentRetrievalService> logger) : ISessionAttachmentRetrievalService
{

    public async Task<SessionAttachmentRetrievedChunk[]> SearchAsync(
        Guid sessionId,
        Embedding<float> queryEmbedding,
        bool includeHistorical,
        CancellationToken cancellationToken)
    {
        return await GuildMemoryAccess.RunAsync<SessionAttachmentRetrievedChunk[]>(GuildMemoryStore.Attachment, GuildMemoryOperation.Read, async guildToken =>
        {
            EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

            if (!embeddings.Enabled || !embeddings.AttachmentRetrievalEnabled)
            {

                return [];

            }

            try
            {

                int maxResults = ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedChunks(
                    embeddings.Attachments.MaxRetrievedChunks);

                float threshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(
                    embeddings.SimilarityThreshold);

                Result<DivinationResult[]> search = await divination.SearchScopedAsync(
                    "session_attachment_embeddings",
                    "ChunkId",
                    "Embedding",
                    "session_attachment_chunks",
                    "ChunkId",
                    includeHistorical ? "SessionId" : "RetrievalScope",
                    sessionId.ToString(),
                    queryEmbedding,
                    maxResults,
                    threshold,
                    guildToken).ConfigureAwait(false);

                if (search.IsFailure || search.Value.Length == 0)
                {

                    return [];

                }

                SessionAttachmentRetrievedChunk[] chunks = await index.GetRetrievedChunksAsync(
                    sessionId,
                    search.Value,
                    guildToken).ConfigureAwait(false);

                // Recheck after the search: a live configuration change must not release already stored
                // document text after Comprehension was switched off.
                return SessionAttachmentComprehensionPolicy.IsEnabled(options.CurrentValue)
                    ? chunks
                    : chunks.Where(static chunk => !SessionAttachmentComprehensionPolicy.TryGetFormat(chunk.MimeType, out _)).ToArray();

            }
            catch (OperationCanceledException) when (guildToken.IsCancellationRequested)
            {

                throw;

            }
            catch (GuildMemoryAccessException)
            {
                throw;
            }
            catch (Exception ex)
            {

                logger.LogDebug(
                    ex,
                    "Session attachment retrieval failed for {SessionId}; continuing without attachment context.",
                    sessionId);

                return [];

            }

        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus>> GetStatusesAsync(
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {
        return await GuildMemoryAccess.RunAsync<IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus>>(GuildMemoryStore.Attachment, GuildMemoryOperation.Read, async guildToken =>
        {
            EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

            if (!embeddings.Enabled || !embeddings.AttachmentRetrievalEnabled)
            {

                return attachmentIds.ToDictionary(
                    static id => id,
                    static _ => SessionAttachmentIndexStatus.NotEligible);

            }

            IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus> stored = await index
                .GetStatusesAsync(attachmentIds, guildToken)
                .ConfigureAwait(false);

            return attachmentIds.ToDictionary(
                static id => id,
                id => stored.GetValueOrDefault(id, SessionAttachmentIndexStatus.Pending));

        }, cancellationToken).ConfigureAwait(false);
    }

}
