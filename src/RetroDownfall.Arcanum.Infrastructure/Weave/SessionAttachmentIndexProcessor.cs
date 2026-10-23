using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>How one dequeued request ended.</summary>
/// <param name="SpendsAttempt">
/// Whether the automatic retry, when <paramref name="ShouldRetry"/> asks for one, carries the next attempt
/// number. Only a busy deferral keeps its number: it read and parsed nothing, so it was not an attempt.
/// </param>
internal sealed record SessionAttachmentIndexOutcome(
    SessionAttachmentIndexDisposition Disposition,
    SessionAttachmentIndexStatus Status,
    bool ShouldRetry,
    bool SpendsAttempt = true)
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

    /// <summary>The one shape a deferral for a busy parser family takes.</summary>
    /// <remarks>
    /// The unit held its lease to the end and wrote its durable row, so it is concluded and gives back its
    /// pending queue identity. The row stays <see cref="SessionAttachmentIndexStatus.Pending"/>: the
    /// automatic retry lives only in memory, and a pending row is what reconciliation re-selects when that
    /// retry is lost to a restart, a full queue or a gate toggle. The retry backoff and the pending-identity
    /// de-duplication keep the two from looping. Nothing was read or parsed, so no attempt is spent.
    /// </remarks>
    internal static SessionAttachmentIndexOutcome DeferredForBusyFamily { get; } =
        new(
            SessionAttachmentIndexDisposition.Concluded,
            SessionAttachmentIndexStatus.Pending,
            ShouldRetry: true,
            SpendsAttempt: false);
}

internal sealed class SessionAttachmentIndexProcessor(
    IOptionsMonitor<ArcanumSettings> options,
    IWeaveService weave,
    ISessionAttachmentStore attachments,
    ISessionAttachmentIndexWriter index,
    ILogger<SessionAttachmentIndexProcessor> logger,
    SessionAttachmentDocumentHost? comprehension = null)
{
    private const int AutomaticEmbeddingBatchSize = 64;

    private const string IndexPipelineVersion = "v1";

    private const string DocumentPipelineVersion = "comprehension-v1";

    /// <summary>
    /// The pipeline of a document whose chunk ranges are pages. It differs from <see cref="DocumentPipelineVersion"/> so an
    /// in-flight replacement begun before pages were recorded is never resumed into one that records them.
    /// </summary>
    private const string PagedDocumentPipelineVersion = "comprehension-v2";

    /// <summary>The one code-owned reason a pages-unit document without usable page marks carries.</summary>
    internal const string PageProvenanceUnavailableReason = "Document page provenance is unavailable.";

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

        ArcanumSettings runtimeSettings = options.CurrentValue;

        EmbeddingSettings embeddings = runtimeSettings.ResolveEmbeddings();

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

        bool isDocument = SessionAttachmentComprehensionPolicy.IsEligibleDocument(attachment, runtimeSettings);

        if (attachment.Kind != SessionAttachmentKind.Text && !isDocument)
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

        if (!weave.IsAvailable && !isDocument)
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

        // A document of a pages-unit format states its chunk ranges as pages, which the extraction's page marks give.
        bool pagedDocument = isDocument
            && SessionAttachmentComprehensionPolicy.TryGetFormat(attachment.MimeType, out SessionAttachmentComprehensionFormat? documentFormat)
            && documentFormat.RangeUnit == SessionAttachmentRangeUnit.Pages;

        SessionAttachmentPageMap? pageMap = null;

        try
        {
            if (isDocument)
            {
                long maxDocumentBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

                SessionAttachmentDocumentHost? documentHost = comprehension
                    ?? SessionAttachmentDocumentExtractors.For(attachment.MimeType);

                SessionAttachmentExtractionResult extraction;

                // A worker process is an external effect, and a lease maintenance has already revoked starts none: a document
                // parsed out of process is then not even read, and stands down unspent.
                if (documentHost is SessionAttachmentIsolatedDocumentHost
                    && workLease.MaintenanceRevocation.IsCancellationRequested)
                {
                    return SessionAttachmentIndexOutcome.DeferredForMaintenance;
                }

                // Every host's snapshot is reserved and read by the one shared step, inside the work lease and outside any
                // effect group, and never queues for its family's slot: a slot held by other documents' parses would hold
                // maintenance's stage-one drain behind them. A busy family is a deferral instead, retried after the backoff
                // with the lease given back. The snapshot holds the slot from the read through the parse and gives it back on
                // every way out of this block, so a document that stands down never holds the next one up.
                using (SessionAttachmentDocumentSnapshot snapshot = await SessionAttachmentTextExtractor.ReadDocumentSnapshotAsync(
                    attachments,
                    attachment,
                    runtimeSettings,
                    maxDocumentBytes,
                    cancellationToken,
                    documentHost,
                    waitForSlot: false).ConfigureAwait(false))
                {
                    if (snapshot.Refusal is { } refusal)
                    {
                        extraction = refusal;
                    }
                    else if (documentHost is SessionAttachmentIsolatedDocumentHost isolatedHost)
                    {
                        // Admission is this method's alone, and each parse step is reached by its host's own type, so the
                        // worker runs only inside an effect group of this lease, begun only now that the snapshot is in
                        // hand. Maintenance that arrived during the read found no open group: the gate signalled the lease's
                        // revocation at once and refuses the group, and the document stands down unspent with no worker
                        // started. The worker is linked to the lease's revocation, so a lease that revokes an open group
                        // kills it and the document is deferred. The gate itself waits through an open group and signals
                        // revocation only once it closes, so maintenance that arrived while the worker ran is seen after the
                        // group, and the document stands down unspent instead of writing an index generation maintenance is
                        // waiting to take the Grimoire from.
                        if (workLease.MaintenanceRevocation.IsCancellationRequested
                            || !workLease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? extractionGroup))
                        {
                            return SessionAttachmentIndexOutcome.DeferredForMaintenance;
                        }

                        await using (IGrimoireExternalEffectGroup extractionEffect = extractionGroup!)
                        {
                            extraction = await isolatedHost.RunWorkerAsync(
                                snapshot,
                                cancellationToken,
                                workLease.MaintenanceRevocation).ConfigureAwait(false);
                        }

                        if (workLease.MaintenanceRevocation.IsCancellationRequested)
                        {
                            return SessionAttachmentIndexOutcome.DeferredForMaintenance;
                        }
                    }
                    else
                    {
                        // An in-process parse is pure computation over bytes already read, so it takes no group. It takes the
                        // slot over from the snapshot, and a parse that outlives this call keeps the slot, and its family
                        // quarantined, until it really ends.
                        extraction = await ((SessionAttachmentDocumentExtractionHost)documentHost!)
                            .ParseInProcessAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (extraction.Status == SessionAttachmentExtractionStatus.Deferred)
                {
                    // Other documents held this format's parser: nothing was read or parsed, so this is
                    // neither the document's outcome nor an extraction. The row stays pending with the
                    // busy reason and no extraction time, so it is never durably terminal.
                    await MarkWithoutIndexAsync(attachment, SessionAttachmentIndexStatus.Pending, request.Attempt,
                        extraction.FailureReason, extractedAt: null, cancellationToken).ConfigureAwait(false);

                    return SessionAttachmentIndexOutcome.DeferredForBusyFamily;
                }

                if (extraction.Status != SessionAttachmentExtractionStatus.Extracted)
                {
                    SessionAttachmentIndexStatus status = extraction.Status == SessionAttachmentExtractionStatus.NotEligible
                        ? SessionAttachmentIndexStatus.NotEligible
                        : SessionAttachmentIndexStatus.Failed;

                    await MarkWithoutIndexAsync(attachment, status, request.Attempt, extraction.FailureReason,
                        extractedAt, cancellationToken).ConfigureAwait(false);

                    return Concluded(status, shouldRetry: false);
                }

                // A pages-unit chunk cannot be told from a line-numbered one once stored, so text that no page claims is a
                // defective extraction, not a document to index under a page label it cannot honour.
                if (pagedDocument && !SessionAttachmentPageMap.TryCreate(extraction.Pages, out pageMap))
                {
                    await MarkWithoutIndexAsync(attachment, SessionAttachmentIndexStatus.Failed, request.Attempt,
                        PageProvenanceUnavailableReason, extractedAt, cancellationToken).ConfigureAwait(false);

                    return Concluded(SessionAttachmentIndexStatus.Failed, shouldRetry: false);
                }

                // Deterministic document failures are terminal even while the provider is down.
                // Only an extracted document may enter the existing provider retry path.
                if (!weave.IsAvailable)
                {
                    await MarkWithoutIndexAsync(attachment, SessionAttachmentIndexStatus.Failed, request.Attempt,
                        "The embedding provider is unavailable.", extractedAt, cancellationToken).ConfigureAwait(false);

                    return Concluded(SessionAttachmentIndexStatus.Failed, shouldRetry: true);
                }

                stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(extraction.Text), writable: false);
            }
            else
            {
                stream = await attachments.OpenReadAsync(attachment, cancellationToken).ConfigureAwait(false);
            }
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
            $"{(pagedDocument ? PagedDocumentPipelineVersion : isDocument ? DocumentPipelineVersion : IndexPipelineVersion)}:{chunkSize}:{overlap}");

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
        // whichever durable exit this batch takes. The span is the batch's whole
        // independently resumable unit: begun, the closure waits through the call and its append or
        // its classification; refused, nothing is billed and every earlier batch stands. Groups are
        // taken one after another from the same lease, which the gate permits because its per-lease
        // guard is a slot the previous group's disposal empties, not a once-per-lease latch.
        async Task<SessionAttachmentIndexOutcome> ConcludeInterruptedBatchAsync(
            OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "Session attachment {AttachmentId} indexing was interrupted and will be retried.",
                attachment.Id);

            await MarkWithoutIndexAsync(
                attachment,
                SessionAttachmentIndexStatus.Failed,
                request.Attempt,
                "Attachment indexing was interrupted and will be retried.",
                extractedAt,
                cancellationToken).ConfigureAwait(false);

            return Concluded(SessionAttachmentIndexStatus.Failed, shouldRetry: true);
        }

        async Task<SessionAttachmentIndexOutcome> ConcludeUnexpectedBatchFailureAsync(
            Exception exception)
        {
            logger.LogWarning(
                exception,
                "Session attachment {AttachmentId} indexing batch failed unexpectedly.",
                attachment.Id);

            await MarkWithoutIndexAsync(
                attachment,
                SessionAttachmentIndexStatus.Failed,
                request.Attempt,
                "Attachment indexing failed unexpectedly.",
                extractedAt,
                cancellationToken).ConfigureAwait(false);

            return Concluded(SessionAttachmentIndexStatus.Failed, shouldRetry: true);
        }

        // The lease is an argument rather than a capture, so the group's receiver is the exact value each caller passes.
        async Task<SessionAttachmentIndexOutcome?> FlushBatchAsync(IGrimoireWorkLease batchLease)
        {
            if (!batchLease.TryBeginExternalEffectGroup(
                    out IGrimoireExternalEffectGroup? effectGroup))
            {
                return SessionAttachmentIndexOutcome.DeferredForMaintenance;
            }

            await using IGrimoireExternalEffectGroup effect = effectGroup!;

            string[] inputs = chunkBatch
                .Select(static chunk => chunk.Text)
                .ToArray();

            // The host token, never the lease's revocation. Once the frontier is won maintenance
            // waits through this group and its durable disposition rather than cancelling into it.
            // Provider cancellation while the host is live is classified below inside that same
            // group; host cancellation remains the one cancellation that propagates unchanged.
            Result<Embedding<float>[]> batch;

            try
            {
                batch = await weave
                    .EmbedBatchAsync(inputs, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // WeaveService deliberately propagates provider cancellation. It is a genuine,
                // retryable interruption when the host token is still live, so classify it before
                // giving back the effect group that admitted the provider call. Let a signalled host
                // token escape instead: shutdown is neither a product failure nor another attempt.
                return await ConcludeInterruptedBatchAsync(ex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await ConcludeUnexpectedBatchFailureAsync(ex).ConfigureAwait(false);
            }

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

            // The vector count needs no check here (IWeaveService answers one vector per input or
            // fails); the width does, because the configured dimensions are this index's contract.
            if (batch.Value.Any(item => item.Vector.Length != expectedDimensions))
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

            try
            {
                await index.AppendReplaceBatchAsync(
                    attachment,
                    checkpoint.GenerationId,
                    chunkBatch,
                    batch.Value,
                    expectedDimensions,
                    extractedAt,
                    indexedAt,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                return await ConcludeInterruptedBatchAsync(ex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await ConcludeUnexpectedBatchFailureAsync(ex).ConfigureAwait(false);
            }

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
                        isDocument ? "text/plain" : attachment.MimeType,
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

                chunkBatch.Add(pageMap is null ? chunks.Current : pageMap.WithPages(chunks.Current));

                if (chunkBatch.Count == AutomaticEmbeddingBatchSize
                    && await FlushBatchAsync(workLease).ConfigureAwait(false) is { } batchFailure)
                {
                    return batchFailure;
                }
            }
        }

        if (chunkBatch.Count > 0
            && await FlushBatchAsync(workLease).ConfigureAwait(false) is { } finalBatchFailure)
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

/// <summary>
/// Maps a character offset of an extracted document's text to the page that holds it. Page marks come from the
/// bounded sink in increasing offset order, so the page of an offset is that of the last mark at or before it.
/// </summary>
internal sealed class SessionAttachmentPageMap
{
    private readonly IReadOnlyList<SessionAttachmentPageMark> _marks;

    private SessionAttachmentPageMap(IReadOnlyList<SessionAttachmentPageMark> marks)
    {
        _marks = marks;
    }

    /// <summary>
    /// Builds the map when the first mark opens the text. Without marks, or with text ahead of the first one, some
    /// character would belong to no page, so there is no map.
    /// </summary>
    public static bool TryCreate(
        IReadOnlyList<SessionAttachmentPageMark>? marks,
        [NotNullWhen(true)] out SessionAttachmentPageMap? map)
    {
        if (marks is { Count: > 0 } && marks[0].Offset == 0)
        {
            map = new SessionAttachmentPageMap(marks);

            return true;
        }

        map = null;

        return false;
    }

    /// <summary>The chunk with its start and end lines replaced by the pages of its first and last character.</summary>
    public SessionAttachmentTextChunk WithPages(SessionAttachmentTextChunk chunk) => chunk with
    {
        StartLine = PageAt(chunk.CharacterStart),
        EndLine = PageAt(chunk.CharacterEnd - 1),
    };

    private int PageAt(int offset)
    {
        int low = 0;

        int high = _marks.Count - 1;

        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);

            if (_marks[middle].Offset <= offset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return _marks[low].Page;
    }
}
