using System.Diagnostics;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// What the in-process parse step (<see cref="SessionAttachmentDocumentExtractionHost.ParseInProcessAsync"/>) keeps across its
/// boundary with the shared read step. A parse its caller abandons by cancellation keeps the family slot and quarantines the
/// family until it really ends, exactly as one its budget abandons does: later callers are refused at once, and callers
/// already queued for the slot are released, instead of waiting out the whole slot wait behind a parse that may never end.
/// And the read and the parse spend one document budget, which the parse takes from the snapshot's slot time rather than
/// starting a budget of its own.
/// </summary>
public sealed class SessionAttachmentDocumentExtractionHostParseStepTests
{
    /// <summary>
    /// The longest a refusal that the quarantine makes immediate may take. Without the quarantine the caller waits out the
    /// whole slot wait, which is twelve times as long.
    /// </summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(60);

    private static readonly byte[] Document = "ARCCOMP1\nDOCUMENT"u8.ToArray();

    // ---------------------------------------------------------------- an abandoned parse quarantines its family

    [Fact]
    public async Task ParseInProcessAsync_CallerCancelledWhileTheParseRuns_RefusesLaterCallersAtOnceUntilTheParseEnds()
    {
        StuckParser parser = new();

        SessionAttachmentDocumentExtractionHost host = new(parser.ExtractAsync, TimeSpan.FromSeconds(30), slotWait: SlotWait);

        using CancellationTokenSource caller = new();

        try
        {
            using (SessionAttachmentDocumentSnapshot snapshot = await ReadAsync(host))
            {
                Task<SessionAttachmentExtractionResult> parsing = host.ParseInProcessAsync(snapshot, caller.Token);

                await parser.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await caller.CancelAsync();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parsing.WaitAsync(TimeSpan.FromSeconds(5)));
            }

            // The caller is gone and its snapshot disposed, yet the parse it handed the slot to still runs and still holds it,
            // so the family refuses at once, even a caller that would wait for the slot, and reads nothing.
            int reads = 0;

            using (SessionAttachmentDocumentSnapshot quarantined = await PromptlyAsync(
                host.ReadSnapshotAsync(
                    _ =>
                    {
                        reads++;

                        return Task.FromResult<ReadOnlyMemory<byte>>(Document);
                    },
                    Document.Length,
                    1024,
                    CancellationToken.None,
                    waitForSlot: true),
                "A caller that would wait for the slot"))
            {
                AssertDeferred(quarantined.Refusal);
            }

            Assert.Equal(0, reads);
        }
        finally
        {
            parser.Release();
        }

        await parser.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Once the parse really ends it gives the slot back, and the quarantine ends with it.
        using SessionAttachmentDocumentSnapshot recovered = await EventuallyHeldAsync(host);

        Assert.Equal("healthy", (await host.ParseInProcessAsync(recovered, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task ParseInProcessAsync_CallerCancelledWhileTheParseRuns_ReleasesACallerAlreadyQueuedForTheSlot()
    {
        StuckParser parser = new();

        SessionAttachmentDocumentExtractionHost host = new(parser.ExtractAsync, TimeSpan.FromSeconds(30), slotWait: SlotWait);

        using CancellationTokenSource caller = new();

        try
        {
            using (SessionAttachmentDocumentSnapshot snapshot = await ReadAsync(host))
            {
                Task<SessionAttachmentExtractionResult> parsing = host.ParseInProcessAsync(snapshot, caller.Token);

                await parser.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                int reads = 0;

                // Queued for the slot while the parse still runs and nothing has abandoned it.
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

                Assert.False(queued.IsCompleted);

                await caller.CancelAsync();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parsing.WaitAsync(TimeSpan.FromSeconds(5)));

                // The abandonment released the queued caller, which the parse would otherwise have kept waiting.
                using SessionAttachmentDocumentSnapshot released = await PromptlyAsync(queued, "A caller queued before the abandonment");

                AssertDeferred(released.Refusal);

                Assert.Equal(0, reads);
            }
        }
        finally
        {
            parser.Release();
        }

        await parser.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using SessionAttachmentDocumentSnapshot recovered = await EventuallyHeldAsync(host);

        Assert.Equal("healthy", (await host.ParseInProcessAsync(recovered, CancellationToken.None)).Text);
    }

    // ---------------------------------------------------------------- one budget for the read and the parse

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseInProcessAsync_ReadAndParseThatEachFitTheBudgetButNotTogether_FailAtTheBudgetNotAtTwiceIt(bool composed)
    {
        // The read spends sixty percent of one budget. The parser cannot receive a fresh budget.
        ManualTimerTimeProvider clock = new();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SessionAttachmentDocumentExtractionHost host = new(
            async (_, _, _) =>
            {
                entered.TrySetResult();

                await release.Task;

                ended.TrySetResult();
            },
            TimeSpan.FromSeconds(1), timeProvider: clock);

        Task<ReadOnlyMemory<byte>> ReadSlowlyAsync(CancellationToken _)
        {
            clock.Advance(TimeSpan.FromMilliseconds(600));

            return Task.FromResult<ReadOnlyMemory<byte>>(Document);
        }

        SessionAttachmentDocumentSnapshot? snapshot = null;

        try
        {
            Task<SessionAttachmentExtractionResult> parsing;

            if (composed)
            {
                parsing = host.ExtractAsync(ReadSlowlyAsync, Document.Length, 1024, CancellationToken.None);
            }
            else
            {
                snapshot = await host.ReadSnapshotAsync(
                    ReadSlowlyAsync, Document.Length, 1024, CancellationToken.None, waitForSlot: false);

                Assert.Null(snapshot.Refusal);

                parsing = host.ParseInProcessAsync(snapshot, CancellationToken.None);
            }

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            clock.Advance(TimeSpan.FromMilliseconds(399));

            Assert.False(parsing.IsCompleted);

            clock.Advance(TimeSpan.FromMilliseconds(1));

            SessionAttachmentExtractionResult result = await parsing.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

            Assert.Equal(SessionAttachmentDocumentExtractionHost.DeadlineReason, result.FailureReason);

            Assert.Empty(result.Text);
        }
        finally
        {
            snapshot?.Dispose();

            release.TrySetResult();
        }

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ParseInProcessAsync_BudgetExpiresBeforeScheduling_RefusesWithoutEnteringOrQuarantiningTheParser()
    {
        ManualTimerTimeProvider clock = new();

        int invocations = 0;

        SessionAttachmentDocumentExtractionHost host = new(
            (_, output, _) =>
            {
                Interlocked.Increment(ref invocations);

                output.TryAppend("healthy");

                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(100), timeProvider: clock);

        using (SessionAttachmentDocumentSnapshot snapshot = await ReadAsync(host))
        {
            // Force the ordering the old quarantine fixture could accidentally encounter under a scheduler delay.
            clock.Advance(TimeSpan.FromMilliseconds(100));

            SessionAttachmentExtractionResult result = await host.ParseInProcessAsync(snapshot, CancellationToken.None);

            Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

            Assert.Equal(SessionAttachmentDocumentExtractionHost.DeadlineReason, result.FailureReason);

            Assert.Equal(0, Volatile.Read(ref invocations));
        }

        using SessionAttachmentDocumentSnapshot recovered = await ReadAsync(host);

        Assert.Null(recovered.Refusal);

        Assert.Equal("healthy", (await host.ParseInProcessAsync(recovered, CancellationToken.None)).Text);

        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task ExtractAsync_ReadDeadlineCancelsStorageButAwaitsItsReleaseBeforeReturningTheSlot()
    {
        ManualTimerTimeProvider clock = new();

        TaskCompletionSource<ReadOnlyMemory<byte>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationToken readCancellation = default;

        SessionAttachmentDocumentExtractionHost host = new(
            new ComprehensionTextContainerExtractor().ExtractAsync,
            TimeSpan.FromMilliseconds(100), timeProvider: clock);

        Task<SessionAttachmentExtractionResult> reading = host.ExtractAsync(
            token =>
            {
                readCancellation = token;

                return release.Task;
            },
            Document.Length, 1024, CancellationToken.None);

        try
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));

            Assert.True(readCancellation.IsCancellationRequested);

            Assert.False(reading.IsCompleted);

            using SessionAttachmentDocumentSnapshot busy = await ReadAsync(host);

            Assert.Equal(SessionAttachmentExtractionStatus.Deferred, busy.Refusal?.Status);
        }
        finally
        {
            release.TrySetResult(Document);
        }

        SessionAttachmentExtractionResult result = await reading.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionAttachmentDocumentHost.ReadDeadlineReason, result.FailureReason);

        Assert.Equal(0, clock.ActiveTimers);

        using SessionAttachmentDocumentSnapshot recovered = await ReadAsync(host);

        Assert.Null(recovered.Refusal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ClockWithoutAPositiveTimestampFrequency_IsRefused(long frequency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionAttachmentDocumentExtractionHost(
            new ComprehensionTextContainerExtractor().ExtractAsync,
            TimeSpan.FromSeconds(1), timeProvider: new InvalidFrequencyTimeProvider(frequency)));
    }

    private sealed class InvalidFrequencyTimeProvider(long frequency) : TimeProvider
    {
        public override long TimestampFrequency => frequency;
    }

    // ---------------------------------------------------------------- helpers

    private static Task<SessionAttachmentDocumentSnapshot> ReadAsync(SessionAttachmentDocumentHost host) =>
        host.ReadSnapshotAsync(
            _ => Task.FromResult<ReadOnlyMemory<byte>>(Document), Document.Length, 1024, CancellationToken.None, waitForSlot: false);

    /// <summary>The task's result, which must arrive within <see cref="Prompt"/>; otherwise the caller is still queued.</summary>
    private static async Task<T> PromptlyAsync<T>(Task<T> task, string caller)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(Prompt));

        Assert.True(
            ReferenceEquals(finished, task),
            $"{caller} was not refused within {Prompt}: it is still queued behind a parse its caller abandoned.");

        return await task;
    }

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

    private static void AssertDeferred(SessionAttachmentExtractionResult? refusal)
    {
        Assert.NotNull(refusal);

        Assert.Equal(SessionAttachmentExtractionStatus.Deferred, refusal.Status);

        Assert.Equal(SessionAttachmentDocumentHost.BusyReason, refusal.FailureReason);

        Assert.Empty(refusal.Text);
    }

    /// <summary>A parser whose first invocation ignores cancellation and runs until released; later ones are healthy.</summary>
    private sealed class StuckParser
    {
        private readonly ManualResetEventSlim _release = new(false);

        private int _invocations;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ExtractAsync(ReadOnlyMemory<byte> bytes, ISessionAttachmentDocumentTextSink output, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _invocations) == 1)
            {
                Entered.TrySetResult();

                _release.Wait(TimeSpan.FromSeconds(30));

                Ended.TrySetResult();

                return ValueTask.CompletedTask;
            }

            output.TryAppend("healthy");

            return ValueTask.CompletedTask;
        }

        public void Release() => _release.Set();
    }
}
