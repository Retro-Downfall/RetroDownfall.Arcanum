using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed record SessionAttachmentIndexOutcome(
    SessionAttachmentIndexStatus Status,
    bool ShouldRetry);

internal sealed class SessionAttachmentIndexProcessor(
    IOptionsMonitor<ArcanumSettings> options,
    IWeaveService weave,
    ISessionAttachmentStore attachments,
    SessionAttachmentIndexRepository index,
    ILogger<SessionAttachmentIndexProcessor> logger)
{

    private const int AutomaticEmbeddingBatchSize = 64;

    private const string IndexPipelineVersion = "v1";

    public async Task<SessionAttachmentIndexOutcome> ProcessAsync(
        SessionAttachmentIndexRequest request,
        CancellationToken cancellationToken)
    {

        EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.AttachmentRetrievalEnabled)
        {

            return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.NotEligible, ShouldRetry: false);

        }

        SessionAttachmentRecord? attachment = await attachments
            .GetByIdAsync(request.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        if (attachment is null
            || attachment.State != SessionAttachmentState.Bound
            || attachment.SessionId != request.SessionId)
        {

            return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.NotEligible, ShouldRetry: false);

        }

        AttachmentEmbeddingSettings settings = embeddings.Attachments ?? new AttachmentEmbeddingSettings();

        await index.SetPendingAsync(
            attachment,
            request.Attempt,
            cancellationToken).ConfigureAwait(false);

        if (attachment.Kind != SessionAttachmentKind.Text)
        {

            await MarkWithoutIndexAsync(
                attachment,
                SessionAttachmentIndexStatus.NotEligible,
                request.Attempt,
                failureReason: null,
                extractedAt: null,
                cancellationToken).ConfigureAwait(false);

            return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.NotEligible, ShouldRetry: false);

        }

        DateTimeOffset extractedAt = DateTimeOffset.UtcNow;

        if (!weave.IsAvailable)
        {

            await MarkWithoutIndexAsync(
                attachment,
                SessionAttachmentIndexStatus.Failed,
                request.Attempt,
                "The embedding provider is unavailable.",
                extractedAt,
                cancellationToken).ConfigureAwait(false);

            return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.Failed, ShouldRetry: true);

        }

        Stream stream;

        try
        {

            stream = await attachments.OpenReadAsync(attachment, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(
                ex,
                "Attachment {AttachmentId} could not be read through encrypted blob storage for indexing.",
                attachment.Id);

            await MarkWithoutIndexAsync(
                attachment,
                SessionAttachmentIndexStatus.Failed,
                request.Attempt,
                "Encrypted attachment bytes are unavailable.",
                extractedAt: null,
                cancellationToken).ConfigureAwait(false);

            return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.Failed, ShouldRetry: true);

        }

        int chunkSize = ArcanumSettingClamps.EmbeddingsAttachmentChunkSizeCharacters(
            settings.ChunkSizeCharacters);

        int overlap = ArcanumSettingClamps.EmbeddingsAttachmentChunkOverlapForChunkSize(
            settings.ChunkOverlapCharacters,
            chunkSize);

        int expectedDimensions = ArcanumSettingClamps.EmbeddingsDimensions(embeddings.Dimensions);

        DateTimeOffset indexedAt = DateTimeOffset.UtcNow;

        string pipelineFingerprint = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{IndexPipelineVersion}:{chunkSize}:{overlap}");

        SessionAttachmentIndexCheckpoint checkpoint = await index.BeginReplaceAsync(
            attachment,
            expectedDimensions,
            pipelineFingerprint,
            extractedAt,
            cancellationToken).ConfigureAwait(false);

        extractedAt = checkpoint.ExtractedAt;

        List<SessionAttachmentTextChunk> chunkBatch = new(AutomaticEmbeddingBatchSize);

        bool wroteAnyBatch = checkpoint.NextChunkIndex > 0;

        int observedChunkCount = 0;

        async Task<SessionAttachmentIndexOutcome?> FlushBatchAsync()
        {

            string[] inputs = chunkBatch
                .Select(static chunk => chunk.Text)
                .ToArray();

            Result<Embedding<float>[]> batch = await weave
                .EmbedBatchAsync(inputs, cancellationToken)
                .ConfigureAwait(false);

            if (batch.IsFailure)
            {

                await MarkWithoutIndexAsync(
                    attachment,
                    SessionAttachmentIndexStatus.Failed,
                    request.Attempt,
                    "The embedding provider failed.",
                    extractedAt,
                    cancellationToken).ConfigureAwait(false);

                return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.Failed, ShouldRetry: true);

            }

            if (batch.Value.Length != chunkBatch.Count
                || batch.Value.Any(item => item.Vector.Length != expectedDimensions))
            {

                await MarkWithoutIndexAsync(
                    attachment,
                    SessionAttachmentIndexStatus.Failed,
                    request.Attempt,
                    "The embedding response dimensions did not match configuration.",
                    extractedAt,
                    cancellationToken).ConfigureAwait(false);

                return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.Failed, ShouldRetry: false);

            }

            await index.AppendReplaceBatchAsync(
                attachment,
                checkpoint.GenerationId,
                chunkBatch,
                batch.Value,
                expectedDimensions,
                extractedAt,
                indexedAt,
                cancellationToken).ConfigureAwait(false);

            wroteAnyBatch = true;

            chunkBatch.Clear();

            return null;

        }

        await using (stream)
        {

            await using IAsyncEnumerator<SessionAttachmentTextChunk> chunks =
                SessionAttachmentTextExtractor
                    .ReadChunksAsync(
                        stream,
                        attachment.MimeType,
                        attachment.OriginalFileName,
                        chunkSize,
                        overlap,
                        cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

            while (true)
            {

                bool hasNext;

                try
                {

                    hasNext = await chunks.MoveNextAsync().ConfigureAwait(false);

                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {

                    throw;

                }
                catch (SessionAttachmentExtractionException ex)
                {

                    SessionAttachmentIndexStatus status =
                        ex.Status == SessionAttachmentExtractionStatus.NotEligible
                            ? SessionAttachmentIndexStatus.NotEligible
                            : SessionAttachmentIndexStatus.Failed;

                    await MarkWithoutIndexAsync(
                        attachment,
                        status,
                        request.Attempt,
                        ex.FailureReason,
                        extractedAt,
                        cancellationToken).ConfigureAwait(false);

                    return new SessionAttachmentIndexOutcome(status, ShouldRetry: false);

                }
                catch (Exception ex)
                {

                    logger.LogWarning(
                        ex,
                        "Attachment {AttachmentId} could not be streamed through encrypted blob storage for indexing.",
                        attachment.Id);

                    await MarkWithoutIndexAsync(
                        attachment,
                        SessionAttachmentIndexStatus.Failed,
                        request.Attempt,
                        "Encrypted attachment bytes are unavailable.",
                        extractedAt,
                        cancellationToken).ConfigureAwait(false);

                    return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.Failed, ShouldRetry: true);

                }

                if (!hasNext)
                {

                    break;

                }

                observedChunkCount = checked(chunks.Current.ChunkIndex + 1);

                if (chunks.Current.ChunkIndex < checkpoint.NextChunkIndex)
                {

                    continue;

                }

                chunkBatch.Add(chunks.Current);

                if (chunkBatch.Count == AutomaticEmbeddingBatchSize
                    && await FlushBatchAsync().ConfigureAwait(false) is { } batchFailure)
                {

                    return batchFailure;

                }

            }

        }

        if (chunkBatch.Count > 0
            && await FlushBatchAsync().ConfigureAwait(false) is { } finalBatchFailure)
        {

            return finalBatchFailure;

        }

        if (!wroteAnyBatch)
        {

            await MarkWithoutIndexAsync(
                attachment,
                SessionAttachmentIndexStatus.NotEligible,
                request.Attempt,
                failureReason: null,
                extractedAt,
                cancellationToken).ConfigureAwait(false);

            return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.NotEligible, ShouldRetry: false);

        }

        await index.CompleteReplaceAsync(
            attachment,
            checkpoint.GenerationId,
            observedChunkCount,
            extractedAt,
            indexedAt,
            request.Attempt,
            cancellationToken).ConfigureAwait(false);

        return new SessionAttachmentIndexOutcome(SessionAttachmentIndexStatus.Indexed, ShouldRetry: false);

    }

    public async Task MarkFailedAsync(
        SessionAttachmentIndexRequest request,
        string failureReason,
        CancellationToken cancellationToken)
    {

        SessionAttachmentRecord? attachment = await attachments
            .GetByIdAsync(request.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        if (attachment is null
            || attachment.State != SessionAttachmentState.Bound
            || attachment.SessionId != request.SessionId)
        {

            return;

        }

        await MarkWithoutIndexAsync(
            attachment,
            SessionAttachmentIndexStatus.Failed,
            request.Attempt,
            failureReason,
            extractedAt: null,
            cancellationToken).ConfigureAwait(false);

    }

    private Task MarkWithoutIndexAsync(
        SessionAttachmentRecord attachment,
        SessionAttachmentIndexStatus status,
        int attempt,
        string? failureReason,
        DateTimeOffset? extractedAt,
        CancellationToken cancellationToken) =>
        index.MarkWithoutIndexAsync(
            attachment.Id,
            attachment.ContentSha256,
            status,
            attempt,
            failureReason,
            extractedAt,
            cancellationToken);

}
