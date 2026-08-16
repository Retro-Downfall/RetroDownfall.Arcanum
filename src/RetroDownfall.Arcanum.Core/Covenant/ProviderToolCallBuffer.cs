using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// One complete provider tool call, frozen and ready to classify.
/// </summary>
/// <remarks>
/// This is the first moment the name and the arguments exist together as one value. Everything
/// before it is fragments the transport is still assembling, and nothing outside the buffer may see
/// those (§10.14).
/// </remarks>
public sealed class FrozenProviderToolCall
{

    internal FrozenProviderToolCall(
        int callIndex,
        string callId,
        string name,
        ReadOnlySpan<byte> argumentsUtf8)
    {

        CallIndex = callIndex;

        CallId = callId;

        Name = name;

        ArgumentsUtf8 = [.. argumentsUtf8];

    }

    /// <summary>The provider's per-attempt call index this call was assembled under.</summary>
    public int CallIndex { get; }

    /// <summary>The provider's tool-call id, or the empty string when it supplied none.</summary>
    public string CallId { get; }

    /// <summary>The exact final tool name, decoded as strict UTF-8.</summary>
    public string Name { get; }

    /// <summary>The exact raw argument bytes, in arrival order.</summary>
    public ImmutableArray<byte> ArgumentsUtf8 { get; }

}

/// <summary>
/// Buffers every streamed tool-name and argument fragment privately until one call is complete.
/// </summary>
/// <remarks>
/// A streaming provider may deliver a tool name in pieces, deliver arguments before the name it
/// belongs to, and interleave several call indexes. Classification cannot run on a prefix: a call
/// whose name is still <c>"propose_"</c> is not yet distinguishable from an ordinary tool, so
/// releasing its arguments to SSE, progress, transcript, or diagnostics at that point would publish
/// profile content that the finished name would have marked private. This buffer therefore holds
/// everything and exposes exactly one exit — <see cref="Freeze(int, string?)"/> — after which the
/// caller classifies the frozen identity and decides what, if anything, becomes public (§10.14).
///
/// <para>Every bound is code-owned rather than configurable, and every counter is charged with the
/// raw delta <em>before</em> the buffer grows, so a provider that never terminates a call cannot
/// exhaust host memory one accepted fragment at a time. Crossing any bound aborts the whole attempt
/// and clears every private buffer, because a partially discarded attempt is one whose remaining
/// fragments no longer describe anything the provider actually sent.</para>
///
/// <para>Not thread-safe. One buffer belongs to one provider attempt, which reads its own stream
/// sequentially.</para>
/// </remarks>
public sealed class ProviderToolCallBuffer
{

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<int, CallFragments> _open = [];

    private readonly HashSet<int> _frozen = [];

    private long _bufferedBytes;

    /// <summary>Whether a bound was crossed and this attempt can no longer produce a tool call.</summary>
    public bool IsAborted { get; private set; }

    /// <summary>How many call indexes currently hold buffered fragments.</summary>
    public int OpenCallCount => _open.Count;

    /// <summary>How many raw name and argument bytes this attempt currently holds.</summary>
    public long BufferedByteCount => _bufferedBytes;

    /// <summary>
    /// Appends one raw fragment of a tool name.
    /// </summary>
    /// <remarks>
    /// The fragment is decoded incrementally with a strict decoder, so a multibyte sequence may
    /// legitimately straddle two fragments while a genuinely malformed one is refused as early as it
    /// can be proven wrong rather than at freeze time.
    /// </remarks>
    public Result AppendNameFragment(int callIndex, string? callId, ReadOnlySpan<byte> utf8Fragment)
    {

        if (IsAborted)
        {
            return Result.Failure(AbortedError());
        }

        if (_frozen.Contains(callIndex))
        {
            return Result.Failure(InvalidError(
                "A frozen provider tool-call index cannot be reused within one attempt."));
        }

        if (!TryReserve(callIndex, utf8Fragment.Length, out CallFragments? call, out Error reservationError))
        {
            return Result.Failure(reservationError);
        }

        if (call!.CallId.Length == 0)
        {
            call.CallId = callId ?? string.Empty;
        }
        else if (!string.IsNullOrEmpty(callId)
            && !string.Equals(call.CallId, callId, StringComparison.Ordinal))
        {
            return Result.Failure(InvalidError(
                "A provider tool call changed its call id partway through its name."));
        }

        if (call.NameBytes + utf8Fragment.Length > CovenantLimits.MaxProviderToolNameBytes)
        {
            return Result.Failure(Abort(BufferExceededError(
                "A provider tool name exceeded its buffered-byte bound.")));
        }

        call.NameBytes += utf8Fragment.Length;

        Charge(utf8Fragment.Length);

        try
        {
            call.AppendNameFragment(utf8Fragment);
        }
        catch (DecoderFallbackException)
        {
            // The decoder's own state is undefined after a fallback, so the index is spent rather
            // than merely failed: nothing that arrives for it later can be trusted to belong to the
            // same call.
            Retire(callIndex, call);

            return Result.Failure(InvalidError(
                "A provider tool-name fragment was not strict UTF-8."));
        }

        return Result.Success();

    }

    /// <summary>Appends one raw fragment of a tool call's arguments.</summary>
    public Result AppendArgumentFragment(int callIndex, ReadOnlySpan<byte> utf8Fragment)
    {

        if (IsAborted)
        {
            return Result.Failure(AbortedError());
        }

        if (_frozen.Contains(callIndex))
        {
            return Result.Failure(InvalidError(
                "A frozen provider tool-call index cannot be reused within one attempt."));
        }

        if (!TryReserve(callIndex, utf8Fragment.Length, out CallFragments? call, out Error reservationError))
        {
            return Result.Failure(reservationError);
        }

        if (call!.Arguments.WrittenCount + utf8Fragment.Length > CovenantLimits.MaxProviderToolArgumentBytes)
        {
            return Result.Failure(Abort(BufferExceededError(
                "A provider tool call exceeded its buffered argument bound.")));
        }

        Charge(utf8Fragment.Length);

        call.Arguments.Write(utf8Fragment);

        return Result.Success();

    }

    /// <summary>
    /// Completes one call index and hands back its exact identity.
    /// </summary>
    /// <param name="providerReportedName">
    /// The final name the provider claims for this call, when it reports one separately from the
    /// fragments. A value that disagrees with the assembled name — including a prefix of it — fails
    /// closed rather than choosing one of the two.
    /// </param>
    public Result<FrozenProviderToolCall> Freeze(int callIndex, string? providerReportedName = null)
    {

        if (IsAborted)
        {
            return Result<FrozenProviderToolCall>.Failure(AbortedError());
        }

        if (_frozen.Contains(callIndex))
        {
            return Result<FrozenProviderToolCall>.Failure(InvalidError(
                "A frozen provider tool-call index cannot be frozen twice."));
        }

        if (!_open.TryGetValue(callIndex, out CallFragments? call))
        {
            return Result<FrozenProviderToolCall>.Failure(InvalidError(
                "No provider tool-call fragments were buffered for this call index."));
        }

        string name;

        try
        {
            name = call.CompleteName();
        }
        catch (DecoderFallbackException)
        {
            Retire(callIndex, call);

            return Result<FrozenProviderToolCall>.Failure(InvalidError(
                "A provider tool name terminated on an incomplete or malformed UTF-8 sequence."));
        }

        if (name.Length == 0)
        {
            Retire(callIndex, call);

            return Result<FrozenProviderToolCall>.Failure(InvalidError(
                "A provider tool call terminated without a name."));
        }

        if (providerReportedName is not null
            && !string.Equals(providerReportedName, name, StringComparison.Ordinal))
        {
            Retire(callIndex, call);

            return Result<FrozenProviderToolCall>.Failure(InvalidError(
                "A provider tool call reported a final name that disagrees with its assembled fragments."));
        }

        FrozenProviderToolCall frozen = new(
            callIndex,
            call.CallId,
            name,
            call.Arguments.WrittenSpan);

        Retire(callIndex, call);

        return Result<FrozenProviderToolCall>.Success(frozen);

    }

    /// <summary>
    /// Releases every private buffer this attempt still holds.
    /// </summary>
    /// <remarks>
    /// Called on teardown as well as on failure. It deliberately does not clear
    /// <see cref="IsAborted"/> or the frozen-index set: an aborted attempt stays aborted, and a call
    /// index that already produced a tool call stays spent for the life of the attempt.
    /// </remarks>
    public void Clear()
    {

        foreach (CallFragments call in _open.Values)
        {
            call.Dispose();
        }

        _open.Clear();

        _bufferedBytes = 0;

    }

    private bool TryReserve(
        int callIndex,
        int fragmentLength,
        out CallFragments? call,
        out Error error)
    {

        error = default!;

        if (fragmentLength < 0)
        {
            call = null;

            error = InvalidError("A provider tool-call fragment length cannot be negative.");

            return false;
        }

        if (checked(_bufferedBytes + fragmentLength) > CovenantLimits.MaxProviderToolBufferedBytesPerAttempt)
        {
            call = null;

            error = Abort(BufferExceededError(
                "This provider attempt exceeded its aggregate buffered tool-call bound."));

            return false;
        }

        if (_open.TryGetValue(callIndex, out CallFragments? existing))
        {
            call = existing;

            return true;
        }

        if (_open.Count + _frozen.Count >= CovenantLimits.MaxProviderToolCallIndexes)
        {
            call = null;

            error = Abort(BufferExceededError(
                "This provider attempt exceeded its simultaneous tool-call index bound."));

            return false;
        }

        call = new CallFragments(StrictUtf8.GetDecoder());

        _open[callIndex] = call;

        return true;

    }

    private void Charge(int fragmentLength) =>
        _bufferedBytes = checked(_bufferedBytes + fragmentLength);

    private void Retire(int callIndex, CallFragments call)
    {

        _bufferedBytes = checked(_bufferedBytes - call.NameBytes - call.Arguments.WrittenCount);

        _ = _open.Remove(callIndex);

        _ = _frozen.Add(callIndex);

        call.Dispose();

    }

    private Error Abort(Error error)
    {

        IsAborted = true;

        Clear();

        return error;

    }

    private static Error AbortedError() =>
        BufferExceededError("This provider attempt was aborted by a tool-call transport bound.");

    private static Error BufferExceededError(string message) =>
        new(ErrorCodes.Hub.ProviderToolBufferExceeded, message);

    private static Error InvalidError(string message) =>
        new(ErrorCodes.Hub.ProviderToolCallInvalid, message);

    private sealed class CallFragments(Decoder nameDecoder) : IDisposable
    {

        private readonly StringBuilder _name = new();

        private char[] _scratch = [];

        public ArrayBufferWriter<byte> Arguments { get; } = new(256);

        public string CallId { get; set; } = string.Empty;

        public int NameBytes { get; set; }

        public void AppendNameFragment(ReadOnlySpan<byte> utf8Fragment) =>
            Decode(utf8Fragment, flush: false);

        public string CompleteName()
        {

            Decode(ReadOnlySpan<byte>.Empty, flush: true);

            return _name.ToString();

        }

        public void Dispose()
        {

            _name.Clear();

            Arguments.Clear();

            _scratch = [];

        }

        private void Decode(ReadOnlySpan<byte> utf8Fragment, bool flush)
        {

            int required = nameDecoder.GetCharCount(utf8Fragment, flush);

            if (_scratch.Length < required)
            {
                _scratch = new char[Math.Max(required, 16)];
            }

            int written = nameDecoder.GetChars(utf8Fragment, _scratch, flush);

            _ = _name.Append(_scratch.AsSpan(0, written));

        }

    }

}
