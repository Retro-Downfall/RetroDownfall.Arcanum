using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed record SessionAttachmentIndexOutcome(
    SessionAttachmentIndexDisposition Disposition,
    SessionAttachmentIndexStatus Status,
    bool ShouldRetry)
{

    /// <summary>The one shape a maintenance deferral takes, so it cannot be spelled two ways.</summary>
    /// <remarks>
    /// <see cref="SessionAttachmentIndexStatus.Pending"/> is the literal truth rather than a filler:
    /// the durable row says pending, the queue identity is still pending, and the caller reads only
    /// <see cref="ShouldRetry"/> in production. <see cref="ShouldRetry"/> is <see langword="false"/>
    /// for an equally literal reason — the automatic-retry path is the attempt-increment path, and a
    /// refusal that was never an attempt must not take it.
    /// </remarks>
    internal static SessionAttachmentIndexOutcome DeferredForMaintenance { get; } =
        new(
            SessionAttachmentIndexDisposition.DeferredForMaintenance,
            SessionAttachmentIndexStatus.Pending,
            ShouldRetry: false);

}

internal sealed class SessionAttachmentIndexProcessor(
    IOptionsMonitor<ArcanumSettings> options,
    IWeaveService weave,
    ISessionAttachmentStore attachments,
    SessionAttachmentIndexRepository index,
    ILogger<SessionAttachmentIndexProcessor> logger)
{

    private const int AutomaticEmbeddingBatchSize = 64;

    private const string IndexPipelineVersion = "v1";

    /// <summary>Indexes one dequeued request under the work lease its caller already holds.</summary>
    /// <remarks>
    /// The lease arrives as a parameter rather than as an injected dependency, and that is a safety
    /// choice. This type is registered by convention as a scoped service, so a constructor dependency
    /// would compile and then fail at resolution on a path the suite reaches only through the
    /// service's own dequeue loop. A parameter cannot be mis-registered, and it puts the rule that
    /// matters — one lease per dequeued request, taken before the scope this instance lives in — at
    /// the call site where it is enforced rather than here where it is only used.
    /// </remarks>
    public async Task<SessionAttachmentIndexOutcome> ProcessAsync(
        SessionAttachmentIndexRequest request,
        IGrimoireWorkLease workLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(workLease);

        EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.AttachmentRetrievalEnabled)
        {

            return Concluded(SessionAttachmentIndexStatus.NotEligible, shouldRetry: false);

        }

        SessionAttachmentRecord? attachment = await attachments
            .GetByIdAsync(request.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        if (attachment is null
            || attachment.State != SessionAttachmentState.Bound
            || attachment.SessionId != request.SessionId)
        {

            return Concluded(SessionAttachmentIndexStatus.NotEligible, shouldRetry: false);

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

            return Concluded(SessionAttachmentIndexStatus.NotEligible, shouldRetry: false);

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

            return Concluded(SessionAttachmentIndexStatus.Failed, shouldRetry: true);

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

            return Concluded(SessionAttachmentIndexStatus.Failed, shouldRetry: true);

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

        // One sequential effect group per batch, opened before the provider call and closed after
        // whichever of this batch's three durable exits it takes. The span is the batch's whole
        // independently resumable unit: begun, the closure waits through the call and its append or
        // its classification; refused, nothing is billed and every earlier batch stands. Groups are
        // taken one after another from the same lease, which the gate permits because its per-lease
        // guard is a slot the previous group's disposal empties, not a once-per-lease latch.
        async Task<SessionAttachmentIndexOutcome?> FlushBatchAsync()
        {

            if (!workLease.TryBeginExternalEffectGroup(
                    out IGrimoireExternalEffectGroup? effectGroup))
            {

                return SessionAttachmentIndexOutcome.DeferredForMaintenance;

            }

            await using IGrimoireExternalEffectGroup effect = effectGroup!;

            string[] inputs = chunkBatch
                .Select(static chunk => chunk.Text)
                .ToArray();

            // The host token, never the lease's revocation. Once the frontier is won maintenance
            // waits through this group and its durable disposition rather than cancelling into it —
            // and here that rule has teeth beyond the frontier, because a cancellation reaching the
            // caller is caught by an arm that marks the attachment Failed and moves its attempt.
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

                return Concluded(SessionAttachmentIndexStatus.Failed, shouldRetry: true);

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

                return Concluded(SessionAttachmentIndexStatus.Failed, shouldRetry: false);

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

                    return Concluded(status, shouldRetry: false);

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

                    return Concluded(SessionAttachmentIndexStatus.Failed, shouldRetry: true);

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

            return Concluded(SessionAttachmentIndexStatus.NotEligible, shouldRetry: false);

        }

        // Publication takes no effect group of its own, and the omission is deliberate. A group is an
        // atomic frontier in front of an external effect, and this has none; maintenance already
        // waits through it because stage one waits on the work lease's terminal and the gate cannot
        // reach Closed while that lease is held. A group here would therefore add no waiting and one
        // refusal point, whose only consequence would be a fully embedded generation left unpublished
        // until a later run re-extracted it for nothing.
        await index.CompleteReplaceAsync(
            attachment,
            checkpoint.GenerationId,
            observedChunkCount,
            extractedAt,
            indexedAt,
            request.Attempt,
            cancellationToken).ConfigureAwait(false);

        return Concluded(SessionAttachmentIndexStatus.Indexed, shouldRetry: false);

    }

    /// <summary>An outcome for a unit that held its lease to the end, whatever it decided.</summary>
    private static SessionAttachmentIndexOutcome Concluded(
        SessionAttachmentIndexStatus status,
        bool shouldRetry) =>
        new(SessionAttachmentIndexDisposition.Concluded, status, shouldRetry);

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
