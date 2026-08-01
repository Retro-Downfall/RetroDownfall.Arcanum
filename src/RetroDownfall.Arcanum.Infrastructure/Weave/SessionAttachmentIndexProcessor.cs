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

        int maxBytes = ArcanumSettingClamps.EmbeddingsAttachmentMaxBytes(settings.MaxAttachmentBytes);

        if (attachment.Kind != SessionAttachmentKind.Text || attachment.ByteLength > maxBytes)
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

        ReadOnlyMemory<byte> bytes;

        try
        {

            bytes = await attachments.ReadBytesAsync(attachment, cancellationToken).ConfigureAwait(false);

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

        int maxCharacters = ArcanumSettingClamps.EmbeddingsAttachmentMaxExtractedCharacters(
            settings.MaxExtractedCharacters);

        SessionAttachmentExtractionResult extraction = SessionAttachmentTextExtractor.Extract(
            bytes.Span,
            attachment.MimeType,
            attachment.OriginalFileName,
            maxCharacters);

        if (extraction.Status != SessionAttachmentExtractionStatus.Extracted)
        {

            SessionAttachmentIndexStatus status = extraction.Status == SessionAttachmentExtractionStatus.NotEligible
                ? SessionAttachmentIndexStatus.NotEligible
                : SessionAttachmentIndexStatus.Failed;

            await MarkWithoutIndexAsync(
                attachment,
                status,
                request.Attempt,
                extraction.FailureReason,
                extractedAt,
                cancellationToken).ConfigureAwait(false);

            return new SessionAttachmentIndexOutcome(status, ShouldRetry: false);

        }

        int chunkSize = ArcanumSettingClamps.EmbeddingsAttachmentChunkSizeCharacters(
            settings.ChunkSizeCharacters);

        int overlap = ArcanumSettingClamps.EmbeddingsAttachmentChunkOverlapForChunkSize(
            settings.ChunkOverlapCharacters,
            chunkSize);

        int maxChunks = ArcanumSettingClamps.EmbeddingsAttachmentMaxChunksPerAttachment(
            settings.MaxChunksPerAttachment);

        SessionAttachmentTextChunk[] chunks = SessionAttachmentChunker.Chunk(
            extraction.Text,
            chunkSize,
            overlap,
            maxChunks);

        if (chunks.Length == 0)
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

        Result<Embedding<float>[]> generated = await weave
            .EmbedBatchAsync([.. chunks.Select(static chunk => chunk.Text)], cancellationToken)
            .ConfigureAwait(false);

        if (generated.IsFailure)
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

        int expectedDimensions = ArcanumSettingClamps.EmbeddingsDimensions(embeddings.Dimensions);

        if (generated.Value.Length != chunks.Length
            || generated.Value.Any(item => item.Vector.Length != expectedDimensions))
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

        await index.ReplaceAsync(
            attachment,
            chunks,
            generated.Value,
            expectedDimensions,
            extractedAt,
            DateTimeOffset.UtcNow,
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
