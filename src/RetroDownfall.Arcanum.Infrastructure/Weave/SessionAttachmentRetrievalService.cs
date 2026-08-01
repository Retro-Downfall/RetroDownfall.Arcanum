using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

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
                "session_attachment_embeddings_vec",
                "ChunkId",
                "Embedding",
                "session_attachment_chunks",
                "ChunkId",
                includeHistorical ? "SessionId" : "RetrievalScope",
                sessionId.ToString(),
                queryEmbedding,
                maxResults,
                threshold,
                cancellationToken).ConfigureAwait(false);

            if (search.IsFailure || search.Value.Length == 0)
            {

                return [];

            }

            return await index.GetRetrievedChunksAsync(
                sessionId,
                search.Value,
                cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

    }

    public async Task<IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus>> GetStatusesAsync(
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {

        EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.AttachmentRetrievalEnabled)
        {

            return attachmentIds.ToDictionary(
                static id => id,
                static _ => SessionAttachmentIndexStatus.NotEligible);

        }

        IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus> stored = await index
            .GetStatusesAsync(attachmentIds, cancellationToken)
            .ConfigureAwait(false);

        return attachmentIds.ToDictionary(
            static id => id,
            id => stored.GetValueOrDefault(id, SessionAttachmentIndexStatus.Pending));

    }

}
