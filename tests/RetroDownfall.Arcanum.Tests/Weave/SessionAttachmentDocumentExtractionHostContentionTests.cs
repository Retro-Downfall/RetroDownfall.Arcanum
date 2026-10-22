using System.Diagnostics;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// What one parser family's slot does to documents that did not cause the contention: waiting for the
/// slot never spends a document's own budget, and a refusal caused by other documents is transient.
/// </summary>
public sealed class SessionAttachmentDocumentExtractionHostContentionTests
{
    private static readonly byte[] Healthy = "ARCCOMP1\nhealthy"u8.ToArray();

    [Fact]
    public async Task ExtractAsync_HealthyDocumentQueuedBehindALongSlotHolder_GetsItsOwnFullBudget()
    {
        // The slot holder is a borrowed snapshot read that ignores its deadline. The host must wait for
        // it, so it holds the family slot for as long as the test chooses, without any parser running.
        TaskCompletionSource<ReadOnlyMemory<byte>> heldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SessionAttachmentDocumentExtractionHost host = new(
            new ComprehensionTextContainerExtractor().ExtractAsync,
            TimeSpan.FromMilliseconds(200));

        Task<SessionAttachmentExtractionResult> holder = host.ExtractAsync(
            _ =>
            {
                readEntered.TrySetResult();

                return heldRead.Task;
            },
            Healthy.Length,
            1024,
            CancellationToken.None);

        Task<SessionAttachmentExtractionResult>? healthy = null;

        try
        {
            await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            healthy = ExtractAsync(host, Healthy);

            // Three times the document budget spent queueing for the slot is not the document's cost.
            await Task.Delay(TimeSpan.FromMilliseconds(600));

            if (healthy.IsCompleted)
            {
                SessionAttachmentExtractionResult premature = await healthy;

                Assert.Fail($"The queued document concluded {premature.Status} while another document held its slot: {premature.FailureReason}");
            }
        }
        finally
        {
            heldRead.TrySetResult(Healthy);
        }

        SessionAttachmentExtractionResult holderResult = await holder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, holderResult.Status);

        Assert.Equal("Document extraction exceeded its deadline while reading its snapshot.", holderResult.FailureReason);

        SessionAttachmentExtractionResult healthyResult = await healthy.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, healthyResult.Status);

        Assert.Equal("healthy", healthyResult.Text);
    }

    [Fact]
    public async Task ExtractAsync_SlotNotAcquiredWithinTheSlotWait_IsDeferredWithoutReadingOrParsing()
    {
        TaskCompletionSource<ReadOnlyMemory<byte>> heldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int parses = 0;

        SessionAttachmentDocumentExtractionHost host = new(
            (bytes, output, cancellationToken) =>
            {
                Interlocked.Increment(ref parses);

                return new ComprehensionTextContainerExtractor().ExtractAsync(bytes, output, cancellationToken);
            },
            TimeSpan.FromSeconds(5),
            slotWait: TimeSpan.FromMilliseconds(100));

        Task<SessionAttachmentExtractionResult> holder = host.ExtractAsync(
            _ =>
            {
                readEntered.TrySetResult();

                return heldRead.Task;
            },
            Healthy.Length,
            1024,
            CancellationToken.None);

        try
        {
            await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            int reads = 0;

            SessionAttachmentExtractionResult deferred = await host.ExtractAsync(
                    _ =>
                    {
                        Interlocked.Increment(ref reads);

                        return Task.FromResult<ReadOnlyMemory<byte>>(Healthy);
                    },
                    Healthy.Length,
                    1024,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SessionAttachmentExtractionStatus.Deferred, deferred.Status);

            Assert.Equal(SessionAttachmentDocumentExtractionHost.BusyReason, deferred.FailureReason);

            Assert.Empty(deferred.Text);

            Assert.False(deferred.WasTruncated);

            Assert.Equal(0, reads);

            Assert.Equal(0, parses);
        }
        finally
        {
            heldRead.TrySetResult(Healthy);
        }

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, (await holder.WaitAsync(TimeSpan.FromSeconds(5))).Status);

        SessionAttachmentExtractionResult later = await ExtractAsync(host, Healthy).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, later.Status);

        Assert.Equal(2, parses);
    }

    [Fact]
    public async Task ExtractAsync_QuarantinedFamily_IsDeferredAtOnceAndRecoversWhenTheParserEnds()
    {
        using ManualResetEventSlim release = new(false);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int invocations = 0;

        SessionAttachmentDocumentExtractionHost host = new(
            (bytes, output, _) =>
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
            SessionAttachmentExtractionResult overran = await ExtractAsync(host, Healthy).WaitAsync(TimeSpan.FromSeconds(5));

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The document's own parse overran its budget after it held the slot: terminal, not deferred.
            Assert.Equal(SessionAttachmentExtractionStatus.Failed, overran.Status);

            Assert.Equal("Document extraction exceeded its deadline.", overran.FailureReason);

            Stopwatch refusal = Stopwatch.StartNew();

            SessionAttachmentExtractionResult quarantined = await ExtractAsync(host, Healthy).WaitAsync(TimeSpan.FromSeconds(5));

            refusal.Stop();

            Assert.Equal(SessionAttachmentExtractionStatus.Deferred, quarantined.Status);

            Assert.Equal(SessionAttachmentDocumentExtractionHost.BusyReason, quarantined.FailureReason);

            // Quarantine refuses immediately rather than queueing behind a parser that may never end.
            Assert.True(refusal.Elapsed < TimeSpan.FromSeconds(2), $"Quarantine refusal took {refusal.Elapsed}.");

            // Still exactly one outstanding parser for the family.
            Assert.Equal(1, Volatile.Read(ref invocations));
        }
        finally
        {
            release.Set();
        }

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

        SessionAttachmentExtractionResult recovered = await EventuallyExtractedAsync(host);

        Assert.Equal("healthy", recovered.Text);
    }

    [Fact]
    public async Task ExtractAsync_CallerCancellationWhileWaitingForTheSlot_PropagatesAndLeaksNoSlot()
    {
        TaskCompletionSource<ReadOnlyMemory<byte>> heldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SessionAttachmentDocumentExtractionHost host = new(
            new ComprehensionTextContainerExtractor().ExtractAsync,
            TimeSpan.FromSeconds(5));

        Task<SessionAttachmentExtractionResult> holder = host.ExtractAsync(
            _ =>
            {
                readEntered.TrySetResult();

                return heldRead.Task;
            },
            Healthy.Length,
            1024,
            CancellationToken.None);

        try
        {
            await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource cancellation = new();

            Task<SessionAttachmentExtractionResult> waiting = ExtractAsync(host, Healthy, cancellation.Token);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            heldRead.TrySetResult(Healthy);
        }

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, (await holder.WaitAsync(TimeSpan.FromSeconds(5))).Status);

        Assert.Equal(
            SessionAttachmentExtractionStatus.Extracted,
            (await ExtractAsync(host, Healthy).WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public void Deferred_IsANewStatusThatNoCallerCanMistakeForExtractedText()
    {
        SessionAttachmentExtractionResult deferred = SessionAttachmentDocumentExtractionHost.Deferred();

        Assert.Equal(SessionAttachmentExtractionStatus.Deferred, deferred.Status);

        Assert.NotEqual(SessionAttachmentExtractionStatus.Extracted, deferred.Status);

        Assert.NotEqual(SessionAttachmentExtractionStatus.Failed, deferred.Status);

        Assert.Empty(deferred.Text);

        Assert.Equal("Document extraction is temporarily busy.", deferred.FailureReason);

        Assert.Equal(TimeSpan.FromSeconds(30), SessionAttachmentDocumentExtractionHost.DefaultSlotWait);
    }

    private static async Task<SessionAttachmentExtractionResult> EventuallyExtractedAsync(SessionAttachmentDocumentExtractionHost host)
    {
        // The parser's end releases the slot through a continuation; allow it to land.
        Stopwatch clock = Stopwatch.StartNew();

        while (true)
        {
            SessionAttachmentExtractionResult result = await ExtractAsync(host, Healthy).WaitAsync(TimeSpan.FromSeconds(5));

            if (result.Status != SessionAttachmentExtractionStatus.Deferred || clock.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

                return result;
            }

            await Task.Delay(10);
        }
    }

    private static Task<SessionAttachmentExtractionResult> ExtractAsync(
        SessionAttachmentDocumentExtractionHost host,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        SessionAttachmentRecord record = new(
            Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
            "document", "document.arccomp", 1, "untrusted/path", "hash",
            "application/vnd.arcanum.comprehension-text", bytes.Length, SessionAttachmentKind.Binary,
            DateTimeOffset.UtcNow);

        return SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            record,
            new ArcanumSettings
            {
                Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
            },
            1024,
            cancellationToken,
            host);
    }
}
