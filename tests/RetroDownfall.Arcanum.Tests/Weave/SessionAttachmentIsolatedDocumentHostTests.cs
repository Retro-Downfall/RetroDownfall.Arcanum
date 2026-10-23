using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// The host of a format parsed only out of process, against a fake worker: the same family-slot semantics as the in-process
/// host, the snapshot read and its checks, the worker awaited inline, every verdict mapped to its code-owned result, and the
/// verdict's pages projected through the bounded sink with a "[PDF page n]" heading and a page mark for each page with text.
/// </summary>
public sealed class SessionAttachmentIsolatedDocumentHostTests
{
    private static readonly byte[] Snapshot = "%PDF-1.7 snapshot"u8.ToArray();

    // ---------------------------------------------------------------- projection

    [Fact]
    public async Task ExtractAsync_ExtractedPages_AreHeadedByTheirNumberAndMarkedWhereTheyStart()
    {
        SessionAttachmentIsolatedDocumentHost host = Host(Pages("PAGE-ONE-ALPHA", "PAGE-TWO-BETA\nsecond line"));

        SessionAttachmentExtractionResult result = await ExtractAsync(host);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("[PDF page 1]\nPAGE-ONE-ALPHA\n[PDF page 2]\nPAGE-TWO-BETA\nsecond line\n", result.Text);

        Assert.False(result.WasTruncated);

        Assert.Null(result.FailureReason);

        Assert.Equal([new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(28, 2)], result.Pages);
    }

    [Fact]
    public async Task ExtractAsync_PagesWithoutText_EmitNothingAndLaterPagesKeepTheirTrueNumbers()
    {
        // Page 2 is empty and page 4 is only whitespace: neither gets a heading, and page 3 is still page 3.
        SessionAttachmentIsolatedDocumentHost host = Host(Pages("PAGE-ONE-ALPHA", string.Empty, "PAGE-THREE-MARKER-GAMMA", " \n\t ", "FIVE"));

        SessionAttachmentExtractionResult result = await ExtractAsync(host);

        Assert.Equal("[PDF page 1]\nPAGE-ONE-ALPHA\n[PDF page 3]\nPAGE-THREE-MARKER-GAMMA\n[PDF page 5]\nFIVE\n", result.Text);

        Assert.Equal(
            [new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(28, 3), new SessionAttachmentPageMark(65, 5)],
            result.Pages);

        Assert.Equal(3, result.Pages![1].Page);

        Assert.StartsWith("[PDF page 3]\nPAGE-THREE-MARKER-GAMMA", result.Text[result.Pages[1].Offset..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_TruncatedVerdict_IsExtractedAndTruncated()
    {
        SessionAttachmentIsolatedDocumentHost host = Host(Pages(truncated: true, "PAGE-ONE-ALPHA"));

        SessionAttachmentExtractionResult result = await ExtractAsync(host);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.True(result.WasTruncated);

        Assert.Equal("[PDF page 1]\nPAGE-ONE-ALPHA\n", result.Text);
    }

    [Fact]
    public async Task ExtractAsync_ProjectionOverTheCeiling_IsCutByTheSinkLikeAnyOtherFormat()
    {
        // 30 characters fit the whole of page 1 (28) and the start of page 2's heading; page 3 never starts.
        SessionAttachmentIsolatedDocumentHost host = Host(Pages("PAGE-ONE-ALPHA", "PAGE-TWO-BETA", "PAGE-THREE-GAMMA"), maximumCharacters: 30);

        SessionAttachmentExtractionResult result = await ExtractAsync(host);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.True(result.WasTruncated);

        Assert.Equal("[PDF page 1]\nPAGE-ONE-ALPHA\n[P", result.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(28, 2)], result.Pages);
    }

    [Fact]
    public async Task ExtractAsync_PageNumbersAreInvariantDigits()
    {
        string[] pages = [.. Enumerable.Repeat(string.Empty, 1233), "LAST"];

        SessionAttachmentExtractionResult result = await ExtractAsync(Host(Pages(pages)));

        Assert.Equal("[PDF page 1234]\nLAST\n", result.Text);
    }

    [Fact]
    public async Task ExtractAsync_PageTextThatReadsLikeAPageNumber_CannotBeMistakenForTheHeading()
    {
        // A report whose printed numbering starts on PDF page 3 draws its footer "Page 1" first. The heading the host
        // inserts is a bracketed form page text does not use, so the line nearest the body is never the provenance.
        string[] pages = [string.Empty, string.Empty, "Page 1\nRevenue grew by 14 percent.", "Page 2\nCosts fell by 6 percent."];

        SessionAttachmentExtractionResult result = await ExtractAsync(Host(Pages(pages)));

        Assert.Equal(
            "[PDF page 3]\nPage 1\nRevenue grew by 14 percent.\n[PDF page 4]\nPage 2\nCosts fell by 6 percent.\n",
            result.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 3), new SessionAttachmentPageMark(48, 4)], result.Pages);
    }

    // ---------------------------------------------------------------- verdicts without text

    [Fact]
    public async Task ExtractAsync_NoText_IsNotEligibleWithNothingElse()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(Host(Verdict(PdfExtractionKind.NoText)));

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);

        Assert.False(result.WasTruncated);

        Assert.Null(result.FailureReason);

        Assert.Null(result.Pages);
    }

    public static TheoryData<string, string> TerminalVerdicts() => new()
    {
        { nameof(PdfExtractionKind.PasswordRequired), "Password-protected PDFs are not supported." },
        { nameof(PdfExtractionKind.Malformed), "The PDF document is malformed." },
        { nameof(PdfExtractionKind.Bounds), "The PDF document exceeds its extraction bounds." },
    };

    [Theory]
    [MemberData(nameof(TerminalVerdicts))]
    public async Task ExtractAsync_TerminalVerdict_FailsWithItsCodeOwnedReason(string kindName, string reason)
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(Host(Verdict(Enum.Parse<PdfExtractionKind>(kindName))));

        AssertFailed(reason, result);
    }

    [Theory]
    [InlineData(ProcessDocumentExtractionWorker.FailedReason)]
    [InlineData(ProcessDocumentExtractionWorker.DeadlineReason)]
    [InlineData(ProcessDocumentExtractionWorker.ResourceLimitsReason)]
    [InlineData(ProcessDocumentExtractionWorker.UnavailableReason)]
    public async Task ExtractAsync_WorkerWithoutAVerdict_FailsWithTheWorkersReason(string reason)
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(Host((_, _, _) => Task.FromResult(DocumentWorkerResult.Failed(reason))));

        AssertFailed(reason, result);
    }

    [Fact]
    public async Task ExtractAsync_WorkerThatThrows_FailsWithACodeOwnedReasonAndNoneOfItsText()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(Host(
            (_, _, _) => throw new InvalidOperationException("worker exploded at /Users/someone/secret.pdf")));

        AssertFailed("Document extraction failed.", result);
    }

    [Fact]
    public async Task ExtractAsync_VerdictTextTheSinkRefuses_FailsWithACodeOwnedReason()
    {
        // The frame never carries NUL, so only a worker that is not the frame's could; the host still fails closed.
        SessionAttachmentExtractionResult result = await ExtractAsync(Host(Pages("NUL\0INSIDE")));

        AssertFailed("Document extraction failed.", result);
    }

    // ---------------------------------------------------------------- the worker call

    [Fact]
    public async Task ExtractAsync_HandsTheWorkerExactlyTheSnapshotAndItsFormat()
    {
        byte[]? seen = null;

        DocumentWorkerFormat? format = null;

        SessionAttachmentIsolatedDocumentHost host = Host((requested, bytes, _) =>
        {
            format = requested;

            seen = bytes.ToArray();

            return Task.FromResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText)));
        });

        await ExtractAsync(host);

        Assert.Equal(DocumentWorkerFormat.Pdf, format);

        Assert.Equal(Snapshot, seen);
    }

    [Fact]
    public async Task ExtractAsync_AwaitsTheWorkerToTheEnd()
    {
        TaskCompletionSource<DocumentWorkerResult> worker = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SessionAttachmentIsolatedDocumentHost host = Host((_, _, _) => worker.Task);

        Task<SessionAttachmentExtractionResult> extraction = ExtractAsync(host);

        await Task.Delay(200);

        Assert.False(extraction.IsCompleted, "The host returned while its worker was still running.");

        worker.SetResult(DocumentWorkerResult.Completed(new PdfExtractionOutcome(PdfExtractionKind.Extracted, ["LATE"], false)));

        Assert.Equal("[PDF page 1]\nLATE\n", (await extraction.WaitAsync(TimeSpan.FromSeconds(5))).Text);
    }

    [Fact]
    public async Task ExtractAsync_CallerCancellation_CancelsTheWorkerAndPropagates()
    {
        using CancellationTokenSource caller = new();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationToken workerToken = default;

        SessionAttachmentIsolatedDocumentHost host = Host(async (_, _, cancellationToken) =>
        {
            workerToken = cancellationToken;

            entered.TrySetResult();

            await Task.Delay(Timeout.Infinite, cancellationToken);

            throw new InvalidOperationException("unreachable");
        });

        Task<SessionAttachmentExtractionResult> extraction = ExtractAsync(host, cancellationToken: caller.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extraction.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(workerToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ExtractAsync_RevocationDuringTheWorker_CancelsItAndIsDeferred()
    {
        using CancellationTokenSource revocation = new();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationToken workerToken = default;

        SessionAttachmentIsolatedDocumentHost host = Host(async (_, _, cancellationToken) =>
        {
            workerToken = cancellationToken;

            entered.TrySetResult();

            await Task.Delay(Timeout.Infinite, cancellationToken);

            throw new InvalidOperationException("unreachable");
        });

        Task<SessionAttachmentExtractionResult> extraction = host.ExtractIsolatedAsync(
            ReadSnapshot, Snapshot.Length, 1024, CancellationToken.None, waitForSlot: false, revocation.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await revocation.CancelAsync();

        SessionAttachmentExtractionResult result = await extraction.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(workerToken.IsCancellationRequested);

        Assert.Equal(SessionAttachmentExtractionStatus.Deferred, result.Status);

        Assert.Equal(SessionAttachmentDocumentHost.BusyReason, result.FailureReason);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractAsync_AlreadyRevoked_IsDeferredWithoutReadingOrRunning()
    {
        using CancellationTokenSource revocation = new();

        await revocation.CancelAsync();

        int reads = 0;

        int runs = 0;

        SessionAttachmentIsolatedDocumentHost host = Host((_, _, _) =>
        {
            runs++;

            return Task.FromResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText)));
        });

        SessionAttachmentExtractionResult result = await host.ExtractIsolatedAsync(
            _ =>
            {
                reads++;

                return Task.FromResult<ReadOnlyMemory<byte>>(Snapshot);
            },
            Snapshot.Length,
            1024,
            CancellationToken.None,
            waitForSlot: false,
            revocation.Token);

        Assert.Equal(SessionAttachmentExtractionStatus.Deferred, result.Status);

        Assert.Equal(0, reads);

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ExtractAsync_WorkerCancellationThatIsNeitherTheCallersNorRevocation_IsACodeOwnedFailure()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(Host((_, _, _) => throw new OperationCanceledException("its own")));

        AssertFailed("Document extraction failed.", result);
    }

    // ---------------------------------------------------------------- snapshot

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ExtractAsync_SnapshotOfAnotherLength_FailsWithoutRunningTheWorker(int difference)
    {
        int runs = 0;

        SessionAttachmentIsolatedDocumentHost host = Host((_, _, _) =>
        {
            runs++;

            return Task.FromResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText)));
        });

        SessionAttachmentExtractionResult result = await host.ExtractAsync(ReadSnapshot, Snapshot.Length + difference, 1024, CancellationToken.None);

        AssertFailed("Document input does not match its bounded snapshot.", result);

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ExtractAsync_SnapshotOverItsMaximum_FailsWithoutRunningTheWorker()
    {
        int runs = 0;

        SessionAttachmentIsolatedDocumentHost host = Host((_, _, _) =>
        {
            runs++;

            return Task.FromResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText)));
        });

        SessionAttachmentExtractionResult result = await host.ExtractAsync(ReadSnapshot, Snapshot.Length, Snapshot.Length - 1, CancellationToken.None);

        AssertFailed("Document input does not match its bounded snapshot.", result);

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ExtractAsync_ReadPastItsDeadline_FailsWithoutRunningTheWorker()
    {
        int runs = 0;

        SessionAttachmentIsolatedDocumentHost host = new(
            new DelegateWorker((_, _, _) =>
            {
                runs++;

                return Task.FromResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText)));
            }),
            DocumentWorkerFormat.Pdf,
            readTimeout: TimeSpan.FromMilliseconds(100));

        SessionAttachmentExtractionResult result = await host.ExtractAsync(
            async cancellationToken =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);

                return Snapshot;
            },
            Snapshot.Length,
            1024,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        AssertFailed("Document extraction exceeded its deadline while reading its snapshot.", result);

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ExtractAsync_CallerCancellationDuringTheRead_Propagates()
    {
        using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(100));

        SessionAttachmentIsolatedDocumentHost host = Host(Pages("UNUSED"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ExtractAsync(
            async cancellationToken =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);

                return Snapshot;
            },
            Snapshot.Length,
            1024,
            caller.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ---------------------------------------------------------------- family slot

    [Fact]
    public async Task ExtractAsync_CallerThatMustNotWait_IsDeferredWhileTheFamilyIsBusyWithoutReadingOrRunning()
    {
        TaskCompletionSource<DocumentWorkerResult> holder = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int runs = 0;

        SessionAttachmentIsolatedDocumentHost host = Host((_, _, _) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                entered.TrySetResult();

                return holder.Task;
            }

            return Task.FromResult(DocumentWorkerResult.Completed(new PdfExtractionOutcome(PdfExtractionKind.Extracted, ["SECOND"], false)));
        });

        Task<SessionAttachmentExtractionResult> first = ExtractAsync(host);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        int reads = 0;

        // At once: a caller that must not wait never queues for the slot.
        SessionAttachmentExtractionResult deferred = await host.ExtractAsync(
            _ =>
            {
                reads++;

                return Task.FromResult<ReadOnlyMemory<byte>>(Snapshot);
            },
            Snapshot.Length,
            1024,
            CancellationToken.None,
            waitForSlot: false).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionAttachmentExtractionStatus.Deferred, deferred.Status);

        Assert.Equal(SessionAttachmentDocumentHost.BusyReason, deferred.FailureReason);

        Assert.Equal(0, reads);

        Assert.Equal(1, runs);

        holder.SetResult(DocumentWorkerResult.Completed(new PdfExtractionOutcome(PdfExtractionKind.Extracted, ["FIRST"], false)));

        Assert.Equal("[PDF page 1]\nFIRST\n", (await first.WaitAsync(TimeSpan.FromSeconds(5))).Text);

        // The slot came back with the first document, so the same caller now runs.
        Assert.Equal("[PDF page 1]\nSECOND\n", (await ExtractAsync(host, waitForSlot: false)).Text);
    }

    [Fact]
    public async Task ExtractAsync_InteractiveCaller_WaitsForTheSlotAndThenRuns()
    {
        TaskCompletionSource<DocumentWorkerResult> holder = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int runs = 0;

        SessionAttachmentIsolatedDocumentHost host = Host((_, _, _) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                entered.TrySetResult();

                return holder.Task;
            }

            return Task.FromResult(DocumentWorkerResult.Completed(new PdfExtractionOutcome(PdfExtractionKind.Extracted, ["SECOND"], false)));
        });

        Task<SessionAttachmentExtractionResult> first = ExtractAsync(host);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<SessionAttachmentExtractionResult> second = ExtractAsync(host);

        await Task.Delay(200);

        Assert.False(second.IsCompleted, "An interactive caller did not wait for the busy family.");

        holder.SetResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText)));

        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("[PDF page 1]\nSECOND\n", (await second.WaitAsync(TimeSpan.FromSeconds(5))).Text);
    }

    [Fact]
    public async Task ExtractAsync_SlotNotFreedWithinTheSlotWait_IsDeferredWithoutReading()
    {
        TaskCompletionSource<DocumentWorkerResult> holder = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SessionAttachmentIsolatedDocumentHost host = new(
            new DelegateWorker((_, _, _) =>
            {
                entered.TrySetResult();

                return holder.Task;
            }),
            DocumentWorkerFormat.Pdf,
            slotWait: TimeSpan.FromMilliseconds(100));

        Task<SessionAttachmentExtractionResult> first = ExtractAsync(host);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        int reads = 0;

        SessionAttachmentExtractionResult deferred = await host.ExtractAsync(
            _ =>
            {
                reads++;

                return Task.FromResult<ReadOnlyMemory<byte>>(Snapshot);
            },
            Snapshot.Length,
            1024,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionAttachmentExtractionStatus.Deferred, deferred.Status);

        Assert.Equal(0, reads);

        holder.SetResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText)));

        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExtractAsync_EveryWayOut_GivesTheSlotBack()
    {
        int call = 0;

        SessionAttachmentIsolatedDocumentHost host = Host((_, _, _) => ++call switch
        {
            1 => throw new InvalidOperationException("fault"),
            2 => throw new OperationCanceledException(),
            _ => Task.FromResult(DocumentWorkerResult.Completed(new PdfExtractionOutcome(PdfExtractionKind.Extracted, ["AFTER"], false))),
        });

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, (await ExtractAsync(host, waitForSlot: false)).Status);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, (await ExtractAsync(host, waitForSlot: false)).Status);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, (await host.ExtractAsync(ReadSnapshot, Snapshot.Length + 1, 1024, CancellationToken.None, waitForSlot: false)).Status);

        using CancellationTokenSource cancelled = new();

        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExtractAsync(host, cancellationToken: cancelled.Token));

        Assert.Equal("[PDF page 1]\nAFTER\n", (await ExtractAsync(host, waitForSlot: false)).Text);
    }

    private static Task<ReadOnlyMemory<byte>> ReadSnapshot(CancellationToken cancellationToken) =>
        Task.FromResult<ReadOnlyMemory<byte>>(Snapshot);

    private static Task<SessionAttachmentExtractionResult> ExtractAsync(
        SessionAttachmentIsolatedDocumentHost host,
        bool waitForSlot = true,
        CancellationToken cancellationToken = default) =>
        host.ExtractAsync(ReadSnapshot, Snapshot.Length, 1024, cancellationToken, waitForSlot);

    private static void AssertFailed(string reason, SessionAttachmentExtractionResult result)
    {
        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal(reason, result.FailureReason);

        Assert.Empty(result.Text);

        Assert.False(result.WasTruncated);

        Assert.Null(result.Pages);
    }

    private static SessionAttachmentIsolatedDocumentHost Host(
        Func<DocumentWorkerFormat, ReadOnlyMemory<byte>, CancellationToken, Task<DocumentWorkerResult>> run,
        int maximumCharacters = 262_144) =>
        new(new DelegateWorker(run), DocumentWorkerFormat.Pdf, maximumCharacters);

    private static Func<DocumentWorkerFormat, ReadOnlyMemory<byte>, CancellationToken, Task<DocumentWorkerResult>> Pages(params string[] pages) =>
        Pages(false, pages);

    private static Func<DocumentWorkerFormat, ReadOnlyMemory<byte>, CancellationToken, Task<DocumentWorkerResult>> Pages(bool truncated, params string[] pages) =>
        (_, _, _) => Task.FromResult(DocumentWorkerResult.Completed(new PdfExtractionOutcome(PdfExtractionKind.Extracted, pages, truncated)));

    private static Func<DocumentWorkerFormat, ReadOnlyMemory<byte>, CancellationToken, Task<DocumentWorkerResult>> Verdict(PdfExtractionKind kind) =>
        (_, _, _) => Task.FromResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(kind)));

    private sealed class DelegateWorker(
        Func<DocumentWorkerFormat, ReadOnlyMemory<byte>, CancellationToken, Task<DocumentWorkerResult>> run) : IDocumentExtractionWorker
    {
        public Task<DocumentWorkerResult> RunAsync(DocumentWorkerFormat format, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            run(format, bytes, cancellationToken);
    }
}
