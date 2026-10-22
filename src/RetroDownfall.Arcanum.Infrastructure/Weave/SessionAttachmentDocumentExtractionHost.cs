using System.Diagnostics;

using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Bounds parser waiting and retained text for a closed, reviewed in-process format. Snapshot reads
/// remain awaited inside the caller's admission and receive cancellation from the total deadline.
/// A parser still running after timeout quarantines its format until it ends. This limits abandoned
/// pure computation; it does not claim to preempt a parser or police its private allocations.
/// </summary>
internal sealed class SessionAttachmentDocumentExtractionHost(
    Func<ReadOnlyMemory<byte>, ISessionAttachmentDocumentTextSink, CancellationToken, ValueTask> extract,
    TimeSpan timeout,
    int maximumCharacters = 262_144)
{
    internal static SessionAttachmentDocumentExtractionHost Default => SessionAttachmentDocumentExtractors.TextContainerHost;

    private readonly object _gate = new();

    private readonly SemaphoreSlim _serial = new(1, 1);

    private bool _quarantined;

    internal async Task<SessionAttachmentExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> readBytes,
        long expectedByteLength,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long started = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (_quarantined)
            {
                return Failed("Document extraction is quarantined while a prior invocation is outstanding.");
            }
        }

        if (!await _serial.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return Failed("Document extraction exceeded its deadline while awaiting the format.");
        }

        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);

        if (remaining <= TimeSpan.Zero)
        {
            _serial.Release();

            return Failed("Document extraction exceeded its deadline.");
        }

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

                lock (_gate)
                {
                    _quarantined = !invocation.IsCompleted;
                }

                RequestCancellation(cancellation);

                return Failed("Document extraction exceeded its deadline.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                sink.Close();

                lock (_gate)
                {
                    _quarantined = !invocation.IsCompleted;
                }

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
