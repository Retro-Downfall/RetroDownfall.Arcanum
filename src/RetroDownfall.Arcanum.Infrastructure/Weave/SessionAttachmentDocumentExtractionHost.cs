using System.Diagnostics;

using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Bounds parser waiting and retained text for a closed, reviewed in-process format. One slot per
/// parser family serializes its invocations. Waiting for that slot is bounded separately and is never
/// charged to the waiting document: a slot not acquired in time, a slot not free for a caller that
/// asked not to wait, or a family quarantined by an outstanding parser, is
/// <see cref="SessionAttachmentExtractionStatus.Deferred"/>, which no caller treats as terminal. A
/// quarantine refuses later callers at once and releases the callers already queued for the slot.
/// Once the slot is held, the document's own budget covers its snapshot read and its parse, and expiry
/// of that budget is a terminal failure. Snapshot reads remain awaited inside the caller's admission and
/// receive cancellation from that budget. A parser still running after its budget quarantines its
/// family until it ends. This limits abandoned pure computation; it does not claim to preempt a parser
/// or police its private allocations.
/// </summary>
internal sealed class SessionAttachmentDocumentExtractionHost(
    Func<ReadOnlyMemory<byte>, ISessionAttachmentDocumentTextSink, CancellationToken, ValueTask> extract,
    TimeSpan timeout,
    int maximumCharacters = 262_144,
    TimeSpan? slotWait = null)
{
    /// <summary>The one code-owned reason a deferred attempt carries, whichever contention caused it.</summary>
    internal const string BusyReason = "Document extraction is temporarily busy.";

    /// <summary>The longest a caller waits for its family's slot before its attempt is deferred.</summary>
    internal static readonly TimeSpan DefaultSlotWait = TimeSpan.FromSeconds(30);

    internal static SessionAttachmentDocumentExtractionHost Default => SessionAttachmentDocumentExtractors.TextContainerHost;

    private readonly TimeSpan _slotWait = slotWait ?? DefaultSlotWait;

    private readonly object _gate = new();

    private readonly SemaphoreSlim _serial = new(1, 1);

    private bool _quarantined;

    /// <summary>
    /// Cancelled when the family is quarantined, releasing every caller queued for the slot before it.
    /// Each quarantine replaces it, so a caller that queues after the family recovers waits on a fresh one.
    /// </summary>
    private CancellationTokenSource _quarantineSignal = new();

    internal async Task<SessionAttachmentExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> readBytes,
        long expectedByteLength,
        long maximumBytes,
        CancellationToken cancellationToken,
        bool waitForSlot = true)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Contention belongs to other documents. A quarantined family refuses at once rather than
        // queueing behind a parser that may never end, and its quarantine releases a caller already
        // queued. Otherwise the slot wait is bounded on its own clock, and a caller that must not wait
        // takes the slot only when it is free. No refusal has read or parsed this document, so none is
        // its outcome.
        CancellationToken quarantine;

        lock (_gate)
        {
            if (_quarantined)
            {
                return Deferred();
            }

            quarantine = _quarantineSignal.Token;
        }

        using (CancellationTokenSource slotWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quarantine))
        {
            try
            {
                if (!await _serial.WaitAsync(waitForSlot ? _slotWait : TimeSpan.Zero, slotWait.Token).ConfigureAwait(false))
                {
                    return Deferred();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Deferred();
            }
        }

        // The document's own budget starts only now that it holds the slot. The snapshot read and the
        // parse share it, and its expiry is charged to this document alone.
        long started = Stopwatch.GetTimestamp();

        TimeSpan remaining = timeout;

        bool parserOwnsSlot = false;

        try
        {
            // Storage borrows the caller's admission and must finish (including stream disposal)
            // before this method returns. A cancellation deadline requests cooperation; it is never
            // permission to abandon a read that still owns installation resources.
            using CancellationTokenSource readDeadline = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);

            readDeadline.CancelAfter(remaining);

            ReadOnlyMemory<byte> bytes;

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
                return Failed("Document extraction exceeded its deadline while reading its snapshot.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            remaining = timeout - Stopwatch.GetElapsedTime(started);

            if (readDeadline.IsCancellationRequested || remaining <= TimeSpan.Zero)
            {
                return Failed("Document extraction exceeded its deadline while reading its snapshot.");
            }

            if (bytes.Length != expectedByteLength || bytes.Length > maximumBytes)
            {
                return Failed("Document input does not match its bounded snapshot.");
            }

            CancellationTokenSource cancellation = new();

            BoundedDocumentTextSink sink = new(maximumCharacters, cancellation.Token);

            // The callback is a closed concrete method group in the production registry. The
            // scheduled closure references only owned bytes, the bounded sink, parser cancellation,
            // and host mechanics/callback. Neither callback here references the storage delegate or
            // the read deadline, so neither borrowed value is retained by their shared closure.
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

            parserOwnsSlot = true;

            _ = invocation.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;

                    lock (_gate)
                    {
                        _quarantined = false;
                    }

                    cancellation.Dispose();

                    _serial.Release();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                SessionAttachmentExtractionResult result = await invocation
                    .WaitAsync(remaining, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                return Stopwatch.GetElapsedTime(started) >= timeout
                    ? Failed("Document extraction exceeded its deadline.")
                    : result;
            }
            catch (TimeoutException) when (!invocation.IsFaulted)
            {
                sink.Close();

                QuarantineWhileRunning(invocation);

                RequestCancellation(cancellation);

                return Failed("Document extraction exceeded its deadline.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                sink.Close();

                QuarantineWhileRunning(invocation);

                RequestCancellation(cancellation);

                throw;
            }
        }
        finally
        {
            if (!parserOwnsSlot)
            {
                _serial.Release();
            }
        }
    }

    /// <summary>
    /// Quarantines the family while the abandoned invocation still runs, and releases the callers queued for
    /// its slot: they would otherwise wait out the whole slot wait behind a parser that may never end. The
    /// release runs their continuations on the thread pool, not on this caller's thread. The released source
    /// owns no timer and is not disposed, since a caller may still be linking to its token.
    /// </summary>
    private void QuarantineWhileRunning(Task invocation)
    {
        CancellationTokenSource? queued = null;

        lock (_gate)
        {
            _quarantined = !invocation.IsCompleted;

            if (_quarantined)
            {
                queued = _quarantineSignal;

                _quarantineSignal = new CancellationTokenSource();
            }
        }

        if (queued is not null)
        {
            _ = queued.CancelAsync();
        }
    }

    private static void RequestCancellation(CancellationTokenSource cancellation)
    {
        try
        {
            _ = cancellation.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Completion raced the timeout; the invocation has already ended.
        }
    }

    internal static SessionAttachmentExtractionResult Failed(string reason) => new(
        SessionAttachmentExtractionStatus.Failed, string.Empty, WasTruncated: false, reason);

    internal static SessionAttachmentExtractionResult Deferred() => new(
        SessionAttachmentExtractionStatus.Deferred, string.Empty, WasTruncated: false, BusyReason);
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
    private readonly object _gate = new();

    private readonly StringBuilder _text;

    private readonly int _maximumCharacters;

    private readonly CancellationToken _cancellationToken;

    private readonly SessionAttachmentStreamingTextTransformer _transformer;

    private bool _closed;

    private bool _truncated;

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

            _truncated = true;

            if (_text.Length > 0 && char.IsHighSurrogate(_text[^1]))
            {
                _text.Length--;
            }
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
                _truncated);
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

        if (_text.Length < _maximumCharacters)
        {
            _text.Append(current);

            return;
        }

        _truncated = true;

        if (_text.Length > 0 && char.IsHighSurrogate(_text[^1]))
        {
            _text.Length--;
        }
    }
}
