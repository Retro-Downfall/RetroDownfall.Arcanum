using System.Diagnostics;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// The one reserve-and-read step every document host shares (<see cref="SessionAttachmentDocumentHost.ReadSnapshotAsync"/>),
/// pinned once for each kind of host: the family slot it reserves, the authenticated read it bounds, the snapshot that holds
/// the slot until it is disposed or until a parse takes the slot over, and every way out of the step giving the slot back.
/// Each host's own parse step (<see cref="SessionAttachmentDocumentExtractionHost.ParseInProcessAsync"/> and
/// <see cref="SessionAttachmentIsolatedDocumentHost.RunWorkerAsync"/>) runs once, on a held snapshot its own host read. The
/// in-process host's quarantine is pinned through the step too: an overrunning parse keeps the slot past its caller until it
/// really ends, the family refuses at once while it does, and the overrun releases a caller already queued for the slot.
/// </summary>
public sealed class SessionAttachmentDocumentHostReadTests
{
    private const string InProcess = "in-process";

    private const string Isolated = "isolated";

    private static readonly byte[] Document = "ARCCOMP1\nDOCUMENT"u8.ToArray();

    public static TheoryData<string> Hosts() => new() { InProcess, Isolated };

    // ---------------------------------------------------------------- the slot the step reserves

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task ReadSnapshotAsync_HoldsTheSlotUntilTheSnapshotIsDisposedAndGivesItBackOnce(string kind)
    {
        HostUnderTest subject = Create(kind);

        SessionAttachmentDocumentSnapshot snapshot = await ReadAsync(subject.Host);

        Assert.Null(snapshot.Refusal);

        int reads = 0;

        // While the snapshot is held the family is busy, so a caller that must not wait is deferred and reads nothing.
        using (SessionAttachmentDocumentSnapshot busy = await subject.Host.ReadSnapshotAsync(
            _ =>
            {
                reads++;

                return Task.FromResult<ReadOnlyMemory<byte>>(Document);
            },
            Document.Length,
            1024,
            CancellationToken.None,
            waitForSlot: false).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            AssertDeferred(busy.Refusal);
        }

        Assert.Equal(0, reads);

        snapshot.Dispose();

        snapshot.Dispose();

        // A disposed snapshot holds no slot and no bytes, so no parse runs on it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => subject.Parse(snapshot, CancellationToken.None));

        using SessionAttachmentDocumentSnapshot next = await ReadAsync(subject.Host);

        Assert.Null(next.Refusal);

        // The second disposal gave nothing back: while the next snapshot holds the one slot, the family is still busy.
        using (SessionAttachmentDocumentSnapshot stillBusy = await ReadAsync(subject.Host))
        {
            AssertDeferred(stillBusy.Refusal);
        }

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, (await subject.Parse(next, CancellationToken.None)).Status);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task ReadSnapshotAsync_FamilyBusy_IsRefusedAsDeferredWithoutReadingAndHoldsNothing(string kind)
    {
        HostUnderTest subject = Create(kind);

        using SessionAttachmentDocumentSnapshot holder = await ReadAsync(subject.Host);

        int reads = 0;

        using (SessionAttachmentDocumentSnapshot refused = await subject.Host.ReadSnapshotAsync(
            _ =>
            {
                reads++;

                return Task.FromResult<ReadOnlyMemory<byte>>(Document);
            },
            Document.Length,
            1024,
            CancellationToken.None,
            waitForSlot: false).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            AssertDeferred(refused.Refusal);

            Assert.Equal(0, reads);

            // A refusal holds no slot and no bytes, so there is nothing for a parse to run on.
            await Assert.ThrowsAsync<InvalidOperationException>(() => subject.Parse(refused, CancellationToken.None));
        }

        // Disposing the refusal gave back nothing it did not hold: the holder still has the family.
        using SessionAttachmentDocumentSnapshot stillBusy = await ReadAsync(subject.Host);

        AssertDeferred(stillBusy.Refusal);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task ReadSnapshotAsync_SlotNotFreedWithinTheSlotWait_IsDeferredWithoutReading(string kind)
    {
        HostUnderTest subject = Create(kind, slotWait: TimeSpan.FromMilliseconds(100));

        using SessionAttachmentDocumentSnapshot holder = await ReadAsync(subject.Host);

        int reads = 0;

        using SessionAttachmentDocumentSnapshot deferred = await subject.Host.ReadSnapshotAsync(
            _ =>
            {
                reads++;

                return Task.FromResult<ReadOnlyMemory<byte>>(Document);
            },
            Document.Length,
            1024,
            CancellationToken.None,
            waitForSlot: true).WaitAsync(TimeSpan.FromSeconds(5));

        AssertDeferred(deferred.Refusal);

        Assert.Equal(0, reads);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task ReadSnapshotAsync_CallerCancelledWhileQueuedForTheSlot_PropagatesAndHoldsNothing(string kind)
    {
        HostUnderTest subject = Create(kind);

        SessionAttachmentDocumentSnapshot holder = await ReadAsync(subject.Host);

        using CancellationTokenSource caller = new();

        Task<SessionAttachmentDocumentSnapshot> queued = subject.Host.ReadSnapshotAsync(
            ReadDocument, Document.Length, 1024, caller.Token, waitForSlot: true);

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));

        holder.Dispose();

        // The cancelled caller took nothing, so the slot the holder gave back is free at once.
        using SessionAttachmentDocumentSnapshot next = await ReadAsync(subject.Host);

        Assert.Null(next.Refusal);
    }

    // ---------------------------------------------------------------- every way out of the read

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task ReadSnapshotAsync_EveryWayOutOfTheRead_GivesTheSlotBack(string kind)
    {
        HostUnderTest subject = Create(kind, readBudget: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<IOException>(() => subject.Host.ReadSnapshotAsync(
            _ => throw new IOException("storage failed"), Document.Length, 1024, CancellationToken.None, waitForSlot: false));

        await AssertSlotFreeAsync(subject);

        await Assert.ThrowsAsync<IOException>(() => subject.Host.ReadSnapshotAsync(
            _ => Task.FromException<ReadOnlyMemory<byte>>(new IOException("storage failed later")),
            Document.Length,
            1024,
            CancellationToken.None,
            waitForSlot: false));

        await AssertSlotFreeAsync(subject);

        using (SessionAttachmentDocumentSnapshot late = await subject.Host.ReadSnapshotAsync(
            async cancellationToken =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);

                return Document;
            },
            Document.Length,
            1024,
            CancellationToken.None,
            waitForSlot: false).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            AssertFailed(SessionAttachmentDocumentHost.ReadDeadlineReason, late.Refusal);
        }

        await AssertSlotFreeAsync(subject);

        using (SessionAttachmentDocumentSnapshot mismatched = await subject.Host.ReadSnapshotAsync(
            ReadDocument, Document.Length + 1, 1024, CancellationToken.None, waitForSlot: false))
        {
            AssertFailed(SessionAttachmentDocumentHost.SnapshotMismatchReason, mismatched.Refusal);
        }

        await AssertSlotFreeAsync(subject);

        using (SessionAttachmentDocumentSnapshot oversized = await subject.Host.ReadSnapshotAsync(
            ReadDocument, Document.Length, Document.Length - 1, CancellationToken.None, waitForSlot: false))
        {
            AssertFailed(SessionAttachmentDocumentHost.SnapshotMismatchReason, oversized.Refusal);
        }

        await AssertSlotFreeAsync(subject);

        using CancellationTokenSource caller = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subject.Host.ReadSnapshotAsync(
            async cancellationToken =>
            {
                await caller.CancelAsync();

                cancellationToken.ThrowIfCancellationRequested();

                return Document;
            },
            Document.Length,
            1024,
            caller.Token,
            waitForSlot: false));

        await AssertSlotFreeAsync(subject);

        using CancellationTokenSource cancelled = new();

        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subject.Host.ReadSnapshotAsync(
            ReadDocument, Document.Length, 1024, cancelled.Token, waitForSlot: false));

        await AssertSlotFreeAsync(subject);
    }

    // ---------------------------------------------------------------- the budget of the read, as each host had it

    [Fact]
    public async Task ReadSnapshotAsync_InProcessReadThatEndsPastTheDocumentBudget_IsRefusedAtTheRead()
    {
        // In process one budget covers the read and the parse, so a read that ignores its deadline and returns late leaves
        // the parse nothing to spend: the step refuses it at the read, before any parse starts.
        HostUnderTest subject = Create(InProcess, readBudget: TimeSpan.FromMilliseconds(100));

        using SessionAttachmentDocumentSnapshot late = await subject.Host.ReadSnapshotAsync(
            async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400));

                return Document;
            },
            Document.Length,
            1024,
            CancellationToken.None,
            waitForSlot: false).WaitAsync(TimeSpan.FromSeconds(5));

        AssertFailed(SessionAttachmentDocumentHost.ReadDeadlineReason, late.Refusal);

        await AssertSlotFreeAsync(subject);
    }

    [Fact]
    public async Task ReadSnapshotAsync_IsolatedReadThatEndsPastItsBudget_IsStillASnapshotForTheWorker()
    {
        // The isolated host's read budget is its own and its worker has its own wall clock, so a read that ignores its
        // deadline but does return the snapshot is kept, as the isolated host always kept it; only a read that its
        // deadline cancels is refused.
        HostUnderTest subject = Create(Isolated, readBudget: TimeSpan.FromMilliseconds(100));

        using SessionAttachmentDocumentSnapshot late = await subject.Host.ReadSnapshotAsync(
            async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400));

                return Document;
            },
            Document.Length,
            1024,
            CancellationToken.None,
            waitForSlot: false).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(late.Refusal);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, (await subject.Parse(late, CancellationToken.None)).Status);
    }

    // ---------------------------------------------------------------- the parse steps

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task ParseStep_RunsOnceOnAHeldSnapshotOfItsOwnHost(string kind)
    {
        HostUnderTest subject = Create(kind);

        HostUnderTest other = Create(kind);

        using SessionAttachmentDocumentSnapshot snapshot = await ReadAsync(subject.Host);

        // Another host's parse refuses the snapshot without taking it, so its own host still runs it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Parse(snapshot, CancellationToken.None));

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, (await subject.Parse(snapshot, CancellationToken.None)).Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => subject.Parse(snapshot, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task ParseStep_CallerCancelledBeforeItStarts_ThrowsAndTheSnapshotGivesTheSlotBack(string kind)
    {
        int parses = 0;

        HostUnderTest subject = Create(kind, onParse: () => Interlocked.Increment(ref parses));

        using CancellationTokenSource cancelled = new();

        await cancelled.CancelAsync();

        using (SessionAttachmentDocumentSnapshot snapshot = await ReadAsync(subject.Host))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subject.Parse(snapshot, cancelled.Token));
        }

        Assert.Equal(0, parses);

        // No parse took the slot over, so the snapshot's disposal gave it back and nothing is quarantined.
        await AssertSlotFreeAsync(subject);
    }

    [Fact]
    public async Task ParseInProcessAsync_Overrun_KeepsTheSlotPastItsCallerAndQuarantinesTheFamilyUntilTheParseEnds()
    {
        using ManualResetEventSlim release = new(false);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int invocations = 0;

        SessionAttachmentDocumentExtractionHost host = new(
            (_, output, _) =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    entered.TrySetResult();

                    // An uncooperative parser: it ignores cancellation and outlives its own budget.
                    release.Wait();

                    ended.TrySetResult();

                    return ValueTask.CompletedTask;
                }

                output.TryAppend("healthy");

                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(100));

        try
        {
            SessionAttachmentExtractionResult overran;

            using (SessionAttachmentDocumentSnapshot snapshot = await ReadAsync(host))
            {
                overran = await host.ParseInProcessAsync(snapshot, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            AssertFailed("Document extraction exceeded its deadline.", overran);

            // The caller disposed its snapshot, but the parse it handed the slot to still runs and still holds it, so the
            // family refuses at once, whether the next caller would wait or not, and reads nothing.
            int reads = 0;

            Stopwatch refusal = Stopwatch.StartNew();

            using (SessionAttachmentDocumentSnapshot quarantined = await host.ReadSnapshotAsync(
                _ =>
                {
                    reads++;

                    return Task.FromResult<ReadOnlyMemory<byte>>(Document);
                },
                Document.Length,
                1024,
                CancellationToken.None,
                waitForSlot: true).WaitAsync(TimeSpan.FromSeconds(5)))
            {
                refusal.Stop();

                AssertDeferred(quarantined.Refusal);
            }

            Assert.True(refusal.Elapsed < TimeSpan.FromSeconds(2), $"Quarantine refusal took {refusal.Elapsed}.");

            Assert.Equal(0, reads);

            Assert.Equal(1, Volatile.Read(ref invocations));
        }
        finally
        {
            release.Set();
        }

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Once the parse really ends it gives the slot back, and the quarantine ends with it.
        using SessionAttachmentDocumentSnapshot recovered = await EventuallyHeldAsync(host);

        Assert.Equal("healthy", (await host.ParseInProcessAsync(recovered, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task ParseInProcessAsync_OverrunReleasesACallerAlreadyQueuedForTheSlot()
    {
        byte[] poison = "ARCCOMP1\npoison"u8.ToArray();

        using ManualResetEventSlim release = new(false);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SessionAttachmentDocumentExtractionHost host = new(
            (bytes, output, cancellationToken) =>
            {
                if (bytes.Span.SequenceEqual(poison))
                {
                    entered.TrySetResult();

                    release.Wait(TimeSpan.FromSeconds(30));

                    return ValueTask.CompletedTask;
                }

                return new ComprehensionTextContainerExtractor().ExtractAsync(bytes, output, cancellationToken);
            },
            TimeSpan.FromMilliseconds(200),
            slotWait: TimeSpan.FromSeconds(20));

        try
        {
            Task<SessionAttachmentExtractionResult> overrunning = Task.Run(async () =>
            {
                using SessionAttachmentDocumentSnapshot snapshot = await host.ReadSnapshotAsync(
                    _ => Task.FromResult<ReadOnlyMemory<byte>>(poison), poison.Length, 1024, CancellationToken.None, waitForSlot: false);

                return await host.ParseInProcessAsync(snapshot, CancellationToken.None);
            });

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            int reads = 0;

            // Queued for the slot while the parse still runs within its budget.
            Task<SessionAttachmentDocumentSnapshot> queued = host.ReadSnapshotAsync(
                _ =>
                {
                    reads++;

                    return Task.FromResult<ReadOnlyMemory<byte>>(Document);
                },
                Document.Length,
                1024,
                CancellationToken.None,
                waitForSlot: true);

            AssertFailed("Document extraction exceeded its deadline.", await overrunning.WaitAsync(TimeSpan.FromSeconds(5)));

            Stopwatch sinceQuarantine = Stopwatch.StartNew();

            using SessionAttachmentDocumentSnapshot released = await queued.WaitAsync(TimeSpan.FromSeconds(15));

            sinceQuarantine.Stop();

            AssertDeferred(released.Refusal);

            Assert.True(
                sinceQuarantine.Elapsed < TimeSpan.FromSeconds(2),
                $"A caller queued before the quarantine waited {sinceQuarantine.Elapsed} after it.");

            Assert.Equal(0, reads);
        }
        finally
        {
            release.Set();
        }

        using SessionAttachmentDocumentSnapshot recovered = await EventuallyHeldAsync(host);

        Assert.Equal("DOCUMENT", (await host.ParseInProcessAsync(recovered, CancellationToken.None)).Text);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<ReadOnlyMemory<byte>> ReadDocument(CancellationToken cancellationToken) =>
        Task.FromResult<ReadOnlyMemory<byte>>(Document);

    private static Task<SessionAttachmentDocumentSnapshot> ReadAsync(SessionAttachmentDocumentHost host) =>
        host.ReadSnapshotAsync(ReadDocument, Document.Length, 1024, CancellationToken.None, waitForSlot: false);

    /// <summary>A snapshot of the family once its slot is free: a parse handed the slot gives it back when it ends.</summary>
    private static async Task<SessionAttachmentDocumentSnapshot> EventuallyHeldAsync(SessionAttachmentDocumentHost host)
    {
        Stopwatch clock = Stopwatch.StartNew();

        while (true)
        {
            SessionAttachmentDocumentSnapshot snapshot = await ReadAsync(host).WaitAsync(TimeSpan.FromSeconds(5));

            if (snapshot.Refusal is null)
            {
                return snapshot;
            }

            snapshot.Dispose();

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The family slot never came back.");

            await Task.Delay(10);
        }
    }

    private static async Task AssertSlotFreeAsync(HostUnderTest subject)
    {
        using SessionAttachmentDocumentSnapshot probe = await ReadAsync(subject.Host).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(probe.Refusal);
    }

    private static void AssertDeferred(SessionAttachmentExtractionResult? refusal)
    {
        Assert.NotNull(refusal);

        Assert.Equal(SessionAttachmentExtractionStatus.Deferred, refusal.Status);

        Assert.Equal(SessionAttachmentDocumentHost.BusyReason, refusal.FailureReason);

        Assert.Empty(refusal.Text);
    }

    private static void AssertFailed(string reason, SessionAttachmentExtractionResult? result)
    {
        Assert.NotNull(result);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal(reason, result.FailureReason);

        Assert.Empty(result.Text);
    }

    /// <summary>
    /// A host of one kind with its parse step reached through its own type, as the indexer reaches it. Both parse the same
    /// text: the in-process host through the text container's extractor, the isolated host through a worker that answers the
    /// container's text as one page.
    /// </summary>
    private static HostUnderTest Create(string kind, TimeSpan? readBudget = null, TimeSpan? slotWait = null, Action? onParse = null)
    {
        if (kind == InProcess)
        {
            SessionAttachmentDocumentExtractionHost inProcess = new(
                (bytes, output, cancellationToken) =>
                {
                    onParse?.Invoke();

                    return new ComprehensionTextContainerExtractor().ExtractAsync(bytes, output, cancellationToken);
                },
                readBudget ?? TimeSpan.FromSeconds(30),
                slotWait: slotWait);

            return new HostUnderTest(inProcess, inProcess.ParseInProcessAsync);
        }

        SessionAttachmentIsolatedDocumentHost isolated = new(
            new PageWorker(onParse),
            DocumentWorkerFormat.Pdf,
            readTimeout: readBudget,
            slotWait: slotWait);

        return new HostUnderTest(isolated, (snapshot, cancellationToken) => isolated.RunWorkerAsync(snapshot, cancellationToken, CancellationToken.None));
    }

    private sealed record HostUnderTest(
        SessionAttachmentDocumentHost Host,
        Func<SessionAttachmentDocumentSnapshot, CancellationToken, Task<SessionAttachmentExtractionResult>> Parse);

    private sealed class PageWorker(Action? onRun) : IDocumentExtractionWorker
    {
        public Task<DocumentWorkerResult> RunAsync(DocumentWorkerFormat format, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            onRun?.Invoke();

            return Task.FromResult(DocumentWorkerResult.Completed(new PdfExtractionOutcome(PdfExtractionKind.Extracted, ["DOCUMENT"], false)));
        }
    }
}
