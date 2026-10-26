using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Bounds parser waiting and retained text for a closed, reviewed in-process format, over the reserve-and-read step every
/// host shares (<see cref="SessionAttachmentDocumentHost.ReadSnapshotAsync"/>). One slot per parser family serializes its
/// invocations. Waiting for that slot is bounded separately and is never charged to the waiting document: a slot not
/// acquired in time, a slot not free for a caller that asked not to wait, or a family quarantined by an outstanding parser,
/// is <see cref="SessionAttachmentExtractionStatus.Deferred"/>, which no caller treats as terminal. A quarantine refuses
/// later callers at once and releases the callers already queued for the slot. Once the slot is held, the document's own
/// budget covers its snapshot read and its parse, and expiry of that budget is a terminal failure. Snapshot reads remain
/// awaited inside the caller's admission and receive cancellation from that budget. The parse
/// (<see cref="ParseInProcessAsync"/>) is the only code of this host's own: it takes the slot over from the snapshot, and a
/// parser still running after its budget keeps the slot and quarantines its family until it ends. This limits abandoned
/// pure computation; it does not claim to preempt a parser or police its private allocations.
/// </summary>
internal sealed class SessionAttachmentDocumentExtractionHost(
    Func<ReadOnlyMemory<byte>, ISessionAttachmentDocumentTextSink, CancellationToken, ValueTask> extract,
    TimeSpan timeout,
    int maximumCharacters = 262_144,
    TimeSpan? slotWait = null,
    TimeProvider? timeProvider = null) : SessionAttachmentDocumentHost(timeout, parseSpendsReadBudget: true, slotWait, timeProvider)
{
    /// <summary>The document's parse outlived what its budget had left once its snapshot was read.</summary>
    internal const string DeadlineReason = "Document extraction exceeded its deadline.";

    internal static SessionAttachmentDocumentExtractionHost Default => SessionAttachmentDocumentExtractors.TextContainerHost;

    /// <summary>The document's own budget, which the snapshot read and the parse share; the base holds it for the read.</summary>
    private readonly TimeSpan _timeout = timeout;

    internal override async Task<SessionAttachmentExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> readBytes,
        long expectedByteLength,
        long maximumBytes,
        CancellationToken cancellationToken,
        bool waitForSlot = true)
    {
        using SessionAttachmentDocumentSnapshot snapshot = await ReadSnapshotAsync(
            readBytes, expectedByteLength, maximumBytes, cancellationToken, waitForSlot).ConfigureAwait(false);

        return snapshot.Refusal ?? await ParseInProcessAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This host's own step: parses, once, a snapshot this host read and still holds, within what the read left of the
    /// document's budget. A member of this class alone, so a caller that holds this host by its own type reaches the
    /// in-process parse and nothing else. The parse takes the slot over from the snapshot as it starts and gives it back
    /// when it ends, so a parse that outlives its caller (its budget expired, or the caller was cancelled) keeps the slot,
    /// and its family quarantined, until it really ends; a step that ends before the parse starts leaves the slot with the
    /// snapshot, whose disposal gives it back.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The snapshot is a refusal, was read by another host, was disposed, or has already been parsed.
    /// </exception>
    internal async Task<SessionAttachmentExtractionResult> ParseInProcessAsync(
        SessionAttachmentDocumentSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ReadOnlyMemory<byte> bytes = snapshot.TakeBytesForParse(this);

        cancellationToken.ThrowIfCancellationRequested();

        long started = snapshot.SlotTakenAt;

        TimeSpan remaining = _timeout - ElapsedSince(started);

        if (remaining <= TimeSpan.Zero)
        {
            return Failed(DeadlineReason);
        }

        CancellationTokenSource cancellation = new();

        BoundedDocumentTextSink sink = new(maximumCharacters, cancellation.Token);

        // The callback is a closed concrete method group in the production registry. The scheduled closure references only
        // owned bytes, the bounded sink, parser cancellation, and host mechanics/callback. Neither callback here references
        // the storage delegate or the read deadline, so neither borrowed value is retained by their shared closure.
        Task<SessionAttachmentExtractionResult> invocation = Task.Run(async () =>
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();

                await extract(bytes, sink, cancellation.Token).ConfigureAwait(false);

                cancellation.Token.ThrowIfCancellationRequested();

                return sink.Complete();
            }
            catch (DocumentPackageRejectedException rejected)
            {
                // A closed, code-owned rejection: fixed status and reason, no entry or part names.
                // It is an ordinary outcome, so the format is not quarantined by it.
                return rejected.ToResult();
            }
            catch (Exception)
            {
                return Failed("Document extraction failed.");
            }
        }, CancellationToken.None);

        // From here the parse owns the slot: its own end gives the slot back, whenever that is. The continuation references
        // only the parser cancellation and the snapshot whose slot it gives back, never the storage delegate.
        snapshot.HandSlotToParse();

        _ = invocation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;

                cancellation.Dispose();

                snapshot.ReleaseHandedOverSlot();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            SessionAttachmentExtractionResult result = await invocation
                .WaitAsync(remaining, Clock, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return ElapsedSince(started) >= _timeout
                ? Failed(DeadlineReason)
                : result;
        }
        catch (TimeoutException) when (!invocation.IsFaulted)
        {
            sink.Close();

            QuarantineWhileRunning(invocation);

            RequestCancellation(cancellation);

            return Failed(DeadlineReason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sink.Close();

            QuarantineWhileRunning(invocation);

            RequestCancellation(cancellation);

            throw;
        }
    }

    /// <summary>
    /// Asks the parser to stop. The parse that ends disposes its own source, which can precede this request when completion
    /// raced the caller's timeout or cancellation, and cancelling a disposed source does not throw: it returns a faulted
    /// task. The fault is observed here, since a task nobody observes surfaces much later as an unobserved task exception.
    /// </summary>
    internal static void RequestCancellation(CancellationTokenSource cancellation)
    {
        _ = cancellation.CancelAsync().ContinueWith(
            static cancelled =>
            {
                _ = cancelled.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed class ComprehensionTextContainerExtractor : ISessionAttachmentDocumentExtractor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ReadOnlySpan<byte> header = "ARCCOMP1\n"u8;

        if (!bytes.Span.StartsWith(header))
        {
            throw new InvalidDataException("Invalid Comprehension text container signature.");
        }

        ReadOnlySpan<byte> payload = bytes.Span[header.Length..];

        if (payload.IndexOf((byte)0) >= 0)
        {
            throw new InvalidDataException("Document text contains NUL characters.");
        }

        // Validate the entire bounded input, including text beyond the retained output ceiling.
        _ = StrictUtf8.GetCharCount(payload);

        Span<char> characters = stackalloc char[2048];

        while (!payload.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int usedBytes = Math.Min(characters.Length, payload.Length);

            // Whole-payload validation above proves that a continuation byte belongs to one scalar.
            // Back off at most three bytes so each slice is complete UTF-8. A UTF-8 byte slice needs
            // no more UTF-16 code units than bytes, keeping the fixed stack buffer sufficient.
            if (usedBytes < payload.Length)
            {
                while ((payload[usedBytes] & 0xC0) == 0x80)
                {
                    usedBytes--;
                }
            }

            int usedCharacters = StrictUtf8.GetChars(payload[..usedBytes], characters);

            if (!output.TryAppend(characters[..usedCharacters]))
            {
                return ValueTask.CompletedTask;
            }

            payload = payload[usedBytes..];
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class BoundedDocumentTextSink : ISessionAttachmentDocumentTextSink
{
    /// <summary>
    /// The most page marks one document retains. A page that would need a mark beyond it is not retained and the
    /// result is truncated, so no retained character is ever attributed to a page that is not its own.
    /// </summary>
    internal const int MaximumPageMarks = 2_000;

    private readonly object _gate = new();

    private readonly List<SessionAttachmentPageMark> _pages = [];

    private readonly StringBuilder _text;

    private readonly int _maximumCharacters;

    private readonly CancellationToken _cancellationToken;

    private readonly SessionAttachmentStreamingTextTransformer _transformer;

    private bool _closed;

    private bool _truncated;

    // The page the next retained character starts, or zero when none has begun since the last mark. A mark is
    // taken when that character is retained, so a page with no retained text leaves none.
    private int _pendingPage;

    // Whether the last character taken was a carriage return the transformer still holds back.
    private bool _carriageReturnHeld;

    public BoundedDocumentTextSink(int maximumCharacters, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);

        _maximumCharacters = maximumCharacters;

        _cancellationToken = cancellationToken;

        _text = new StringBuilder(Math.Min(maximumCharacters, 4096));

        _transformer = new SessionAttachmentStreamingTextTransformer(false, AppendCharacter);
    }

    public bool TryAppend(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            if (_closed || _truncated)
            {
                return false;
            }

            foreach (char current in text)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (current == '\0')
                {
                    throw new InvalidDataException("Document text contains NUL characters.");
                }

                _transformer.Append(current);

                _carriageReturnHeld = current == '\r';

                if (_truncated)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public void MarkTruncated()
    {
        lock (_gate)
        {
            if (_truncated)
            {
                return;
            }

            Truncate();
        }
    }

    public void BeginPage(int pageNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);

        lock (_gate)
        {
            if (_closed || _truncated)
            {
                return;
            }

            // The transformer holds a trailing carriage return back until it sees the next character, and would then
            // emit its line break as the first character of this page. Releasing it now keeps that line break on the
            // page that ended with it.
            if (_carriageReturnHeld)
            {
                _carriageReturnHeld = false;

                _transformer.Append('\n');

                if (_truncated)
                {
                    return;
                }
            }

            _pendingPage = pageNumber;
        }
    }

    public SessionAttachmentExtractionResult Complete()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return SessionAttachmentDocumentExtractionHost.Failed("Document extraction completed after its deadline.");
            }

            _transformer.Complete();

            _closed = true;

            return new SessionAttachmentExtractionResult(
                _text.Length == 0
                    ? SessionAttachmentExtractionStatus.NotEligible
                    : SessionAttachmentExtractionStatus.Extracted,
                _text.ToString(),
                _truncated,
                Pages: _pages.Count == 0 ? null : _pages.ToArray());
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    private void AppendCharacter(char current)
    {
        if (_truncated)
        {
            return;
        }

        if (_text.Length >= _maximumCharacters || (_pendingPage != 0 && _pages.Count == MaximumPageMarks))
        {
            Truncate();

            return;
        }

        if (_pendingPage != 0)
        {
            _pages.Add(new SessionAttachmentPageMark(_text.Length, _pendingPage));

            _pendingPage = 0;
        }

        _text.Append(current);
    }

    private void Truncate()
    {
        _truncated = true;

        if (_text.Length > 0 && char.IsHighSurrogate(_text[^1]))
        {
            _text.Length--;

            // A page whose only retained character was that surrogate has no text left to mark.
            if (_pages.Count > 0 && _pages[^1].Offset == _text.Length)
            {
                _pages.RemoveAt(_pages.Count - 1);
            }
        }
    }
}
