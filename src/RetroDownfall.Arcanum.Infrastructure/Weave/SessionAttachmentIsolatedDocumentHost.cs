using System.Globalization;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// The host of a format parsed only out of process. It keeps the in-process host's family-slot semantics: one document
/// of the family at a time, a caller that must not queue (the indexer) deferred at once when the family is busy, and an
/// interactive caller waiting a bounded time. It reads the caller's authenticated snapshot under the caller's admission
/// with the same byte checks, then awaits the worker inline: no detached task and no quarantine, because the worker owns
/// its child process and returns only after the child is gone, whatever ended it. The worker's verdict is projected
/// through the same bounded sink as every other format, so truncation and page marks behave alike.
/// </summary>
/// <remarks>
/// Each page with text is projected as <c>Page n</c> on a line of its own, then its text, then a line break, with the page
/// mark set where its heading starts. A page without text emits nothing and its number is never reused, so the numbers
/// that remain are the document's own. Cancelling <c>revocation</c> (maintenance taking the Grimoire back from the
/// indexer) kills the worker and defers the document, which nothing treats as terminal; cancelling the caller's token
/// kills it and propagates.
/// </remarks>
internal sealed class SessionAttachmentIsolatedDocumentHost(
    IDocumentExtractionWorker worker,
    DocumentWorkerFormat format,
    int maximumCharacters = 262_144,
    TimeSpan? readTimeout = null,
    TimeSpan? slotWait = null) : SessionAttachmentDocumentHost
{
    /// <summary>The document needs a password other than the empty one, which is never guessed, stripped or asked for.</summary>
    internal const string PasswordRequiredReason = "Password-protected PDFs are not supported.";

    /// <summary>The bytes are not a PDF the strict parser can read.</summary>
    internal const string MalformedReason = "The PDF document is malformed.";

    /// <summary>The document declares or shows more pages or glyphs than the extractor's bounds allow.</summary>
    internal const string BoundsReason = "The PDF document exceeds its extraction bounds.";

    /// <summary>The worker call or its verdict failed in a way no other reason describes.</summary>
    internal const string WorkerFaultReason = "Document extraction failed.";

    internal const string ReadDeadlineReason = "Document extraction exceeded its deadline while reading its snapshot.";

    internal const string SnapshotMismatchReason = "Document input does not match its bounded snapshot.";

    /// <summary>The longest the snapshot read may take once the slot is held; the worker has its own wall clock.</summary>
    internal static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    private readonly IDocumentExtractionWorker _worker = worker;

    private readonly DocumentWorkerFormat _format = format;

    private readonly int _maximumCharacters = maximumCharacters;

    private readonly TimeSpan _readTimeout = readTimeout ?? DefaultReadTimeout;

    private readonly TimeSpan _slotWait = slotWait ?? DefaultSlotWait;

    private readonly SemaphoreSlim _serial = new(1, 1);

    internal override Task<SessionAttachmentExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> readBytes,
        long expectedByteLength,
        long maximumBytes,
        CancellationToken cancellationToken,
        bool waitForSlot = true) =>
        ExtractIsolatedAsync(readBytes, expectedByteLength, maximumBytes, cancellationToken, waitForSlot, CancellationToken.None);

    /// <summary>
    /// <see cref="ExtractAsync"/> with a <paramref name="revocation"/> that, once cancelled, kills the worker and defers the
    /// document instead of failing it. A member of this class alone, so a caller that holds this host by its own type
    /// reaches the isolated parse and nothing else.
    /// </summary>
    internal async Task<SessionAttachmentExtractionResult> ExtractIsolatedAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> readBytes,
        long expectedByteLength,
        long maximumBytes,
        CancellationToken cancellationToken,
        bool waitForSlot,
        CancellationToken revocation)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The slot belongs to the document holding it, so a refusal here is contention, never this document's outcome.
        if (revocation.IsCancellationRequested
            || !await _serial.WaitAsync(waitForSlot ? _slotWait : TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return Deferred();
        }

        try
        {
            ReadOnlyMemory<byte> bytes;

            // Storage borrows the caller's admission and must finish, including stream disposal, before this returns.
            // The deadline asks it to stop; it is never permission to abandon the read.
            using (CancellationTokenSource readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readDeadline.CancelAfter(_readTimeout);

                try
                {
                    bytes = await readBytes(readDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (readDeadline.IsCancellationRequested)
                {
                    return Failed(ReadDeadlineReason);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (bytes.Length != expectedByteLength || bytes.Length > maximumBytes)
            {
                return Failed(SnapshotMismatchReason);
            }

            if (revocation.IsCancellationRequested)
            {
                return Deferred();
            }

            DocumentWorkerResult result;

            using (CancellationTokenSource workerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revocation))
            {
                try
                {
                    result = await _worker.RunAsync(_format, bytes, workerLifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (revocation.IsCancellationRequested)
                {
                    return Deferred();
                }
                catch (Exception)
                {
                    return Failed(WorkerFaultReason);
                }
            }

            return Project(result, cancellationToken);
        }
        finally
        {
            _serial.Release();
        }
    }

    private SessionAttachmentExtractionResult Project(DocumentWorkerResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome is not { } outcome)
        {
            return Failed(result.FailureReason ?? WorkerFaultReason);
        }

        switch (outcome.Kind)
        {
            case PdfExtractionKind.Extracted:

                break;

            case PdfExtractionKind.NoText:

                // Nothing to read is not a failure: an image-only document is the defined input for recognition later.
                return new SessionAttachmentExtractionResult(SessionAttachmentExtractionStatus.NotEligible, string.Empty, WasTruncated: false);

            case PdfExtractionKind.PasswordRequired:

                return Failed(PasswordRequiredReason);

            case PdfExtractionKind.Malformed:

                return Failed(MalformedReason);

            case PdfExtractionKind.Bounds:

                return Failed(BoundsReason);

            default:

                return Failed(WorkerFaultReason);
        }

        BoundedDocumentTextSink sink = new(_maximumCharacters, cancellationToken);

        try
        {
            for (int index = 0; index < outcome.Pages.Count; index++)
            {
                string text = outcome.Pages[index];

                if (!HasVisibleCharacter(text))
                {
                    continue;
                }

                int pageNumber = index + 1;

                sink.BeginPage(pageNumber);

                if (!sink.TryAppend(string.Create(CultureInfo.InvariantCulture, $"Page {pageNumber}\n"))
                    || !sink.TryAppend(text)
                    || !sink.TryAppend("\n"))
                {
                    break;
                }
            }
        }
        catch (InvalidDataException)
        {
            // The sink refuses text no frame can carry; only a worker other than the frame's could hand it over.
            return Failed(WorkerFaultReason);
        }

        if (outcome.Truncated)
        {
            sink.MarkTruncated();
        }

        return sink.Complete();
    }

    private static bool HasVisibleCharacter(string text)
    {
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }
}
