using System.Globalization;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// The host of a format parsed only out of process. It shares the reserve-and-read step of every host
/// (<see cref="SessionAttachmentDocumentHost.ReadSnapshotAsync"/>): one document of the family at a time, a caller that
/// must not queue (the indexer) deferred at once when the family is busy, an interactive caller waiting a bounded time, and
/// the caller's authenticated snapshot read under the caller's admission within its own budget, with the same byte checks.
/// Its own step, <see cref="RunWorkerAsync"/>, awaits the worker on that snapshot inline: no detached task and no
/// quarantine, because the worker owns its child process and returns only after the child is gone, whatever ended it, so
/// the slot stays with the snapshot and the caller's disposal of it gives the slot back. The worker's verdict is projected
/// through the same bounded sink as every other format, so truncation and page marks behave alike.
/// <see cref="ExtractAsync"/> composes the two steps for an interactive caller; the indexer takes them apart so that the
/// effect group a worker needs is begun only once the snapshot is in hand.
/// </summary>
/// <remarks>
/// The format's <see cref="DocumentWorkerProfile"/> words everything format-specific in the projection: the heading and the
/// reasons a verdict without text ends with. Each page with text is projected as the profile's heading (for a PDF,
/// <c>[PDF page n]</c>) on a line of its own, then its text, then a line break, with the page mark set where its heading
/// starts. The bracketed form is not how a document prints its own folios, so a footer such as <c>Page 1</c> drawn first on
/// PDF page 3 reads as page text under the <c>[PDF page 3]</c> heading, never as the page's provenance. Nor can a page draw
/// the heading itself: the heading is in-band, so every occurrence of its opening
/// (<see cref="DocumentWorkerProfile.HeadingOpening"/>) inside page text is written with a backslash before the bracket
/// (<see cref="DocumentWorkerProfile.EscapedHeadingOpening"/>) and only the host's own headings read as headings. A page
/// without text emits nothing and its number is never reused, so the numbers that remain are the document's own. Cancelling <c>revocation</c>
/// (maintenance taking the Grimoire back from the indexer) kills the worker and defers the document, which nothing treats
/// as terminal; a revocation already signalled when the worker would start defers it without starting anything; cancelling
/// the caller's token kills it and propagates.
/// </remarks>
internal sealed class SessionAttachmentIsolatedDocumentHost(
    IDocumentExtractionWorker worker,
    DocumentWorkerFormat format,
    int maximumCharacters = 262_144,
    TimeSpan? readTimeout = null,
    TimeSpan? slotWait = null) : SessionAttachmentDocumentHost(readTimeout ?? DefaultReadTimeout, parseSpendsReadBudget: false, slotWait)
{
    /// <summary>The worker call or its verdict failed in a way no other reason describes, whatever the format.</summary>
    internal const string WorkerFaultReason = "Document extraction failed.";

    /// <summary>The longest the snapshot read may take once the slot is held; the worker has its own wall clock.</summary>
    internal static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    private readonly IDocumentExtractionWorker _worker = worker;

    private readonly DocumentWorkerFormat _format = format;

    /// <summary>The format's profile, whose heading and verdict reasons every projection uses; a format without one is refused here.</summary>
    private readonly DocumentWorkerProfile _profile = DocumentWorkerProfile.For(format);

    private readonly int _maximumCharacters = maximumCharacters;

    /// <summary>
    /// <see cref="SessionAttachmentDocumentHost.ReadSnapshotAsync"/> and then <see cref="RunWorkerAsync"/> with no revocation,
    /// holding the slot from the read through the worker. The interactive paths (attach, refresh, reference, pin) come here,
    /// under their request's admission.
    /// </summary>
    internal override async Task<SessionAttachmentExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> readBytes,
        long expectedByteLength,
        long maximumBytes,
        CancellationToken cancellationToken,
        bool waitForSlot = true)
    {
        using SessionAttachmentDocumentSnapshot snapshot = await ReadSnapshotAsync(
            readBytes, expectedByteLength, maximumBytes, cancellationToken, waitForSlot).ConfigureAwait(false);

        return snapshot.Refusal
            ?? await RunWorkerAsync(snapshot, cancellationToken, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// This host's own step: runs the worker, once, on a snapshot this host read and still holds, and projects its verdict.
    /// A member of this class alone, so a caller that holds this host by its own type reaches the worker and nothing else.
    /// The snapshot keeps the slot; the caller's disposal of it gives the slot back.
    /// </summary>
    /// <param name="revocation">
    /// Cancelled when the caller's admission is taken back for maintenance. Already cancelled, it defers the document without
    /// starting anything; cancelled while the worker runs, it kills the worker and defers the document. The indexer passes its
    /// work lease's revocation and begins the lease's effect group before this step, so maintenance that became pending
    /// during the snapshot read has already refused that group and this step is never reached.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The snapshot is a refusal, was read by another host, was disposed, or has already been run.
    /// </exception>
    internal async Task<SessionAttachmentExtractionResult> RunWorkerAsync(
        SessionAttachmentDocumentSnapshot snapshot,
        CancellationToken cancellationToken,
        CancellationToken revocation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ReadOnlyMemory<byte> bytes = snapshot.TakeBytesForParse(this);

        cancellationToken.ThrowIfCancellationRequested();

        // The last moment before a process exists. A worker started now would run while maintenance waits, only for its
        // verdict to be discarded when the document stands down.
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

    private SessionAttachmentExtractionResult Project(DocumentWorkerResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome is not { } outcome)
        {
            return Failed(result.FailureReason ?? WorkerFaultReason);
        }

        switch (outcome.Kind)
        {
            case DocumentExtractionKind.Extracted:

                break;

            case DocumentExtractionKind.NoText:

                // Nothing to read is not a failure: an image-only document is the defined input for recognition later.
                return new SessionAttachmentExtractionResult(SessionAttachmentExtractionStatus.NotEligible, string.Empty, WasTruncated: false);

            case DocumentExtractionKind.PasswordRequired:

                return Failed(_profile.PasswordRequiredReason);

            case DocumentExtractionKind.Malformed:

                return Failed(_profile.MalformedReason);

            case DocumentExtractionKind.Bounds:

                return Failed(_profile.BoundsReason);

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

                if (!sink.TryAppend(string.Create(CultureInfo.InvariantCulture, $"{_profile.HeadingOpening} {pageNumber}]\n"))
                    || !sink.TryAppend(text.Replace(_profile.HeadingOpening, _profile.EscapedHeadingOpening, StringComparison.Ordinal))
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
