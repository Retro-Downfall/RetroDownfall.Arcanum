namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// The one abstraction a caller reaches a registry format's extraction through, whichever way the format is parsed: in
/// this process by a pure extractor (<see cref="SessionAttachmentDocumentExtractionHost"/>) or in a jailed worker process
/// (<see cref="SessionAttachmentIsolatedDocumentHost"/>). Every host runs a document in the same two steps, and only the
/// second is a host's own. The first, <see cref="ReadSnapshotAsync"/>, is written here once for every host: it reserves the
/// family's one slot and reads the caller's authenticated snapshot under the caller's admission, within the read's deadline
/// and with the byte checks, and returns a <see cref="SessionAttachmentDocumentSnapshot"/> that holds the slot and the bytes.
/// The second is the parse, which each host declares on its own type and no caller reaches through this one, so the caller
/// that owns admission (the indexer) names the parse it runs and decides whether it runs inside an effect group.
/// </summary>
/// <remarks>
/// What differs between hosts in the first step is two values the host passes in: the read's budget, and whether the parse
/// spends what the read leaves of that same budget (in process) or has a clock of its own (a worker's wall clock). The slot
/// carries a quarantine that only a parse which outlives its caller sets: such a parse keeps the slot until it really ends,
/// the family refuses every caller at once while it does, and the quarantine releases the callers already queued for the
/// slot, which would otherwise wait out the whole slot wait behind a parse that may never end.
/// </remarks>
internal abstract class SessionAttachmentDocumentHost
{
    /// <summary>The one code-owned reason a deferred attempt carries, whichever contention caused it.</summary>
    internal const string BusyReason = "Document extraction is temporarily busy.";

    /// <summary>The snapshot read outlived its budget.</summary>
    internal const string ReadDeadlineReason = "Document extraction exceeded its deadline while reading its snapshot.";

    /// <summary>The bytes read are not the length the record states, or exceed the caller's ceiling.</summary>
    internal const string SnapshotMismatchReason = "Document input does not match its bounded snapshot.";

    /// <summary>The longest a caller waits for its family's slot before its attempt is deferred.</summary>
    internal static readonly TimeSpan DefaultSlotWait = TimeSpan.FromSeconds(30);

    private readonly long _timestampFrequency;

    private readonly TimeSpan _readBudget;

    private readonly bool _parseSpendsReadBudget;

    private readonly TimeSpan _slotWait;

    private readonly object _gate = new();

    private readonly SemaphoreSlim _slot = new(1, 1);

    private bool _quarantined;

    /// <summary>
    /// Cancelled when the family is quarantined, releasing every caller queued for the slot before it. Each quarantine
    /// replaces it, so a caller that queues after the family recovers waits on a fresh one.
    /// </summary>
    private CancellationTokenSource _quarantineSignal = new();

    /// <param name="readBudget">The longest the snapshot read may take, counted from when the slot is held.</param>
    /// <param name="parseSpendsReadBudget">
    /// Whether the parse spends what the read leaves of the same budget. A read that ends with none of it left is then
    /// refused at the read, since its parse would have nothing to spend; otherwise only a read its deadline cancels is.
    /// </param>
    /// <param name="slotWait">The longest an interactive caller waits for the slot; <see cref="DefaultSlotWait"/> by default.</param>
    /// <param name="timeProvider">The monotonic document clock and deadline timers; system time by default.</param>
    private protected SessionAttachmentDocumentHost(
        TimeSpan readBudget,
        bool parseSpendsReadBudget,
        TimeSpan? slotWait,
        TimeProvider? timeProvider = null)
    {
        Clock = timeProvider ?? TimeProvider.System;

        // TimeProvider timestamps use one stable frequency for the lifetime of the provider. Capture it before
        // admitted reads and parses; elapsed checks then expose only their timestamp query to source analysis.
        _timestampFrequency = Clock.TimestampFrequency;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_timestampFrequency);

        _readBudget = readBudget;

        _parseSpendsReadBudget = parseSpendsReadBudget;

        _slotWait = slotWait ?? DefaultSlotWait;
    }

    /// <summary>The monotonic clock and deadline timers for one document budget; system time in production.</summary>
    private protected TimeProvider Clock { get; }

    /// <summary>Converts this provider's timestamp with the stable frequency captured at construction.</summary>
    private protected TimeSpan ElapsedSince(long started) =>
        TimeSpan.FromSeconds((Clock.GetTimestamp() - started) / (double)_timestampFrequency);

    /// <summary>
    /// The interactive composition (attach, refresh, reference, pin): <see cref="ReadSnapshotAsync"/>, then the host's own
    /// parse on the snapshot, with the snapshot disposed on every way out. A caller that must not queue passes
    /// <paramref name="waitForSlot"/> <see langword="false"/> and is deferred when the family is busy.
    /// </summary>
    internal abstract Task<SessionAttachmentExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> readBytes,
        long expectedByteLength,
        long maximumBytes,
        CancellationToken cancellationToken,
        bool waitForSlot = true);

    /// <summary>
    /// The shared first step of every host: reserves the family slot and reads the authenticated snapshot under the
    /// caller's admission, within the read's budget. Not virtual and calling nothing virtual, so it is the same code for
    /// every host and every caller.
    /// </summary>
    /// <returns>
    /// A snapshot that holds the slot and the bytes until it is disposed (or until a parse takes the slot over), or one that
    /// holds nothing and carries the refusal that ended the step: deferred when the family is busy or quarantined (nothing
    /// read), failed when the read overran its budget or its bytes did not match the record. Either way the caller disposes
    /// it; a step that throws holds nothing.
    /// </returns>
    internal async Task<SessionAttachmentDocumentSnapshot> ReadSnapshotAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> readBytes,
        long expectedByteLength,
        long maximumBytes,
        CancellationToken cancellationToken,
        bool waitForSlot)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Contention belongs to other documents. A quarantined family refuses at once rather than queueing behind a parse
        // that may never end, and its quarantine releases a caller already queued. Otherwise the slot wait is bounded on its
        // own clock, and a caller that must not wait takes the slot only when it is free. No refusal here has read or parsed
        // this document, so none is its outcome.
        CancellationToken quarantine;

        lock (_gate)
        {
            if (_quarantined)
            {
                return SessionAttachmentDocumentSnapshot.Refused(Deferred());
            }

            quarantine = _quarantineSignal.Token;
        }

        using (CancellationTokenSource slotWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quarantine))
        {
            try
            {
                if (!await _slot.WaitAsync(waitForSlot ? _slotWait : TimeSpan.Zero, slotWait.Token).ConfigureAwait(false))
                {
                    return SessionAttachmentDocumentSnapshot.Refused(Deferred());
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return SessionAttachmentDocumentSnapshot.Refused(Deferred());
            }
        }

        // The document's own budget starts only now that it holds the slot, and its expiry is charged to this document alone.
        long slotTakenAt = Clock.GetTimestamp();

        // Every way out of this step that does not hand the slot to a snapshot gives it back here.
        bool slotHandedOver = false;

        try
        {
            ReadOnlyMemory<byte> bytes;

            bool overran;

            // Storage borrows the caller's admission and must finish, including stream disposal, before this returns. The
            // deadline asks it to stop; it is never permission to abandon a read that still owns installation resources.
            using (CancellationTokenSource timer = new(_readBudget, Clock))
            using (CancellationTokenSource readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token))
            {
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
                    return SessionAttachmentDocumentSnapshot.Refused(Failed(ReadDeadlineReason));
                }

                overran = readDeadline.IsCancellationRequested;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // A parse that spends what the read left of one budget has nothing to spend once the read has spent it all.
            if (_parseSpendsReadBudget && (overran || ElapsedSince(slotTakenAt) >= _readBudget))
            {
                return SessionAttachmentDocumentSnapshot.Refused(Failed(ReadDeadlineReason));
            }

            if (bytes.Length != expectedByteLength || bytes.Length > maximumBytes)
            {
                return SessionAttachmentDocumentSnapshot.Refused(Failed(SnapshotMismatchReason));
            }

            SessionAttachmentDocumentSnapshot held = new(this, bytes, slotTakenAt);

            slotHandedOver = true;

            return held;
        }
        finally
        {
            if (!slotHandedOver)
            {
                ReleaseSlot();
            }
        }
    }

    internal static SessionAttachmentExtractionResult Failed(string reason) => new(
        SessionAttachmentExtractionStatus.Failed, string.Empty, WasTruncated: false, reason);

    internal static SessionAttachmentExtractionResult Deferred() => new(
        SessionAttachmentExtractionStatus.Deferred, string.Empty, WasTruncated: false, BusyReason);

    /// <summary>
    /// Gives the family slot back and ends a quarantine. Only the slot's holder calls it, through its snapshot, and a
    /// quarantine lasts only while the parse that set it holds the slot, so the holder's release is the quarantine's end.
    /// </summary>
    internal void ReleaseSlot()
    {
        lock (_gate)
        {
            _quarantined = false;
        }

        _slot.Release();
    }

    /// <summary>
    /// Quarantines the family while <paramref name="parse"/>, which holds the slot and which its caller has abandoned, still
    /// runs, and releases the callers queued for the slot. The release runs their continuations on the thread pool, not on
    /// this caller's thread. The released source owns no timer and is not disposed, since a caller may still be linking to
    /// its token.
    /// </summary>
    private protected void QuarantineWhileRunning(Task parse)
    {
        CancellationTokenSource? queued = null;

        lock (_gate)
        {
            _quarantined = !parse.IsCompleted;

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
}

/// <summary>
/// One document's hold on its host's family slot together with the authenticated snapshot read under it, or, when the
/// shared read step ended before a parse could run, the refusal that ended it and no hold at all. The caller keeps it in a
/// <see langword="using"/> from the read through the parse, so the slot is given back on every way out of that scope, a
/// refused effect group, an exception and cancellation included. The one exception is a parse that takes the slot over
/// (<see cref="HandSlotToParse"/>) because it may outlive its caller: that parse gives the slot back when it ends
/// (<see cref="ReleaseHandedOverSlot"/>), and disposal then gives back nothing. The slot is given back once, however the two
/// interleave and however often either is called.
/// </summary>
internal sealed class SessionAttachmentDocumentSnapshot : IDisposable
{
    /// <summary>The snapshot holds the slot and its bytes, and no parse has taken them.</summary>
    private const int Held = 0;

    /// <summary>The host's parse has the bytes; the snapshot still gives the slot back.</summary>
    private const int Taken = 1;

    /// <summary>The parse owns the slot and gives it back when it ends.</summary>
    private const int HandedOver = 2;

    /// <summary>The slot is back, or was never held.</summary>
    private const int Released = 3;

    private readonly SessionAttachmentDocumentHost? _host;

    private readonly ReadOnlyMemory<byte> _bytes;

    private int _state;

    internal SessionAttachmentDocumentSnapshot(SessionAttachmentDocumentHost host, ReadOnlyMemory<byte> bytes, long slotTakenAt)
    {
        _host = host;

        _bytes = bytes;

        SlotTakenAt = slotTakenAt;
    }

    private SessionAttachmentDocumentSnapshot(SessionAttachmentExtractionResult refusal)
    {
        Refusal = refusal;

        _state = Released;
    }

    /// <summary>The result that ended the read step, or <see langword="null"/> when the snapshot holds the slot and its bytes.</summary>
    internal SessionAttachmentExtractionResult? Refusal { get; }

    /// <summary>The host clock's monotonic timestamp at which the slot was taken, where the document's own budget began.</summary>
    internal long SlotTakenAt { get; }

    /// <summary>A snapshot that holds nothing and carries the result that ended the read step.</summary>
    internal static SessionAttachmentDocumentSnapshot Refused(SessionAttachmentExtractionResult refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return new SessionAttachmentDocumentSnapshot(refusal);
    }

    /// <summary>Gives the slot back unless a parse has taken it over or it is already back.</summary>
    public void Dispose()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);

            if (state is HandedOver or Released)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _state, Released, state) == state)
            {
                _host!.ReleaseSlot();

                return;
            }
        }
    }

    /// <summary>
    /// The bytes, for the one parse of the host that read them while the slot is still held. Refused, without taking the
    /// snapshot, for another host, a refusal, a disposed snapshot or a second parse.
    /// </summary>
    internal ReadOnlyMemory<byte> TakeBytesForParse(SessionAttachmentDocumentHost host)
    {
        if (!ReferenceEquals(host, _host) || Interlocked.CompareExchange(ref _state, Taken, Held) != Held)
        {
            throw new InvalidOperationException("A parse runs once, on a snapshot its own host read and still holds.");
        }

        return _bytes;
    }

    /// <summary>
    /// Hands the slot to the parse that took the bytes, for a parse that may outlive its caller: from here only that parse's
    /// own end, through <see cref="ReleaseHandedOverSlot"/>, gives the slot back.
    /// </summary>
    internal void HandSlotToParse()
    {
        if (Interlocked.CompareExchange(ref _state, HandedOver, Taken) != Taken)
        {
            throw new InvalidOperationException("Only the parse that took the snapshot's bytes takes its slot over.");
        }
    }

    /// <summary>Gives back the slot a parse took over, once; it is called when that parse ends, whatever ended it.</summary>
    internal void ReleaseHandedOverSlot()
    {
        if (Interlocked.CompareExchange(ref _state, Released, HandedOver) == HandedOver)
        {
            _host!.ReleaseSlot();
        }
    }
}
