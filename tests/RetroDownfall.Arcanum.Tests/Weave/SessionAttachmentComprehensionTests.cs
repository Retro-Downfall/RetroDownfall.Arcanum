using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class SessionAttachmentComprehensionTests
{
    [Fact]
    public async Task ExtractDocument_ComprehensionContainer_ExtractsNormalizedPayloadThroughStore()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("ARCCOMP1\nalpha\r\nbeta\rcharlie");

        SessionAttachmentRecord record = CreateRecord(bytes.Length);

        int reads = 0;

        NoOpSessionAttachmentStore store = new(readBytes: (_, _) =>
        {
            reads++;

            return Task.FromResult<ReadOnlyMemory<byte>>(bytes);
        }, openRead: (_, _) => throw new InvalidOperationException("Document facade may only read bytes."));

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            store,
            record,
            Settings(),
            1024,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("alpha\nbeta\ncharlie", result.Text);

        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ExtractDocument_DisabledParentOrChild_DoesNotReadBytes(bool retrieval, bool comprehension)
    {
        ArcanumSettings settings = Settings();

        settings.Features.AttachmentRetrieval = retrieval;

        settings.Features.Comprehension = comprehension;

        NoOpSessionAttachmentStore store = new(readBytes: (_, _) => throw new InvalidOperationException("Disabled document read."));

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            store, CreateRecord(9), settings, 1024, CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);
    }

    [Theory]
    [InlineData("application/rtf")]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    public async Task ExtractDocument_UnsupportedMime_DoesNotReadBytes(string mimeType)
    {
        NoOpSessionAttachmentStore store = new(readBytes: (_, _) => throw new InvalidOperationException("Unsupported document read."));

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            store, CreateRecord(9) with { MimeType = mimeType }, Settings(), 1024, CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);
    }

    [Fact]
    public async Task ExtractDocument_Pdf_IsExtractedByTheRegistrysIsolatedWorkerWithPageHeadings()
    {
        // PDF is a registry format that only the isolated worker parses; the registry's host projects the worker's pages.
        byte[] bytes = PdfFixtures.Read("multi-page.pdf");

        NoOpSessionAttachmentStore store = new(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes));

        using IDisposable worker = InProcessPdfWorker.Use(out InProcessPdfWorker pdf);

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            store,
            CreateRecord(bytes.Length) with { MimeType = SessionAttachmentComprehensionPolicy.PdfMimeType },
            Settings(),
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("Page 1\nPAGE-ONE-ALPHA\nPage 3\nPAGE-THREE-MARKER-GAMMA\nPage 4\nPAGE-FOUR-DELTA\n", result.Text);

        Assert.Equal([3], result.Pages!.Where(static mark => mark.Offset == 22).Select(static mark => mark.Page));

        Assert.Equal(1, pdf.Runs);
    }

    [Fact]
    public async Task ExtractDocument_PdfInAHostThatCannotRelaunchItself_FailsAsUnavailable()
    {
        // The registry's own worker, unsubstituted: this test platform cannot re-launch itself as the worker.
        byte[] bytes = PdfFixtures.Read("multi-page.pdf");

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            CreateRecord(bytes.Length) with { MimeType = SessionAttachmentComprehensionPolicy.PdfMimeType },
            Settings(),
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("PDF extraction is unavailable on this host.", result.FailureReason);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractDocument_MimeParametersAndCase_UseClosedRegistry()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(
            "ARCCOMP1\nhello", record: CreateRecord(14) with { MimeType = " Application/Vnd.Arcanum.Comprehension-Text ; charset=utf-8" });

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("hello", result.Text);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1025L)]
    [InlineData(2147483648L)]
    public async Task ExtractDocument_OversizedDeclaredInput_FailsBeforeAllocation(long byteLength)
    {
        NoOpSessionAttachmentStore store = new(readBytes: (_, _) => throw new InvalidOperationException("Oversized document read."));

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            store, CreateRecord(byteLength), Settings(), 1024, CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Theory]
    [InlineData("not-a-container")]
    [InlineData("ARCCOMP1\nsecret\0payload")]
    public async Task ExtractDocument_MalformedContainer_FailsWithoutText(string content)
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(content);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractDocument_InvalidUtf8_FailsWithoutReplacementText()
    {
        byte[] bytes = [.. "ARCCOMP1\n"u8.ToArray(), 0xC3, 0x28];

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            CreateRecord(bytes.Length), Settings(), 1024, CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xC3, 0x28 })]
    public async Task ExtractDocument_MalformedPayloadBeyondOutputCeiling_FailsWithoutRetainingPrefix(byte[] malformedSuffix)
    {
        byte[] prefix = Encoding.UTF8.GetBytes("ARCCOMP1\n" + new string('a', 262_145));

        byte[] bytes = [.. prefix, .. malformedSuffix];

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            CreateRecord(bytes.Length), Settings(), bytes.Length, CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);

        Assert.False(result.WasTruncated);
    }

    [Theory]
    [InlineData("é", 2047)]
    [InlineData("€", 2046)]
    [InlineData("😀", 2045)]
    public async Task ExtractDocument_MultibyteScalarsAcrossBoundedDecodeSlices_PreserveTextAndNewlines(
        string scalar,
        int prefixCharacters)
    {
        string prefix = new('a', prefixCharacters);

        string repeated = string.Concat(Enumerable.Repeat(scalar, 2050));

        byte[] bytes = Encoding.UTF8.GetBytes("ARCCOMP1\n" + prefix + scalar + "\r\n" + repeated + "\r" + scalar);

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            CreateRecord(bytes.Length), Settings(), bytes.Length, CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal(prefix + scalar + "\n" + repeated + "\n" + scalar, result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public async Task ExtractDocument_ValidEmptyContainer_IsNotEligible()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync("ARCCOMP1\n");

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractDocument_ReturnedLengthMismatch_FailsWithoutExtraction()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync("ARCCOMP1\nhello", record: CreateRecord(1));

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractDocument_OutputCeiling_TruncatesWithoutSplittingSurrogateAndUsesExistingChunks()
    {
        SessionAttachmentDocumentExtractionHost host = new(
            new ComprehensionTextContainerExtractor().ExtractAsync, TimeSpan.FromSeconds(1), maximumCharacters: 4);

        SessionAttachmentExtractionResult result = await ExtractAsync("ARCCOMP1\na\r\nb😀tail", host: host);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.True(result.WasTruncated);

        Assert.Equal("a\nb", result.Text);

        SessionAttachmentChunkAssembler assembler = new(2, 0);

        foreach (char character in result.Text)
        {
            assembler.Append(character);
        }

        List<SessionAttachmentTextChunk> chunks = [];

        while (assembler.TryTakeChunk(true, out SessionAttachmentTextChunk chunk))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("a\nb", string.Concat(chunks.Select(chunk => chunk.Text)));

        Assert.Equal(3, chunks[^1].CharacterEnd);

        Assert.Equal(2, chunks[^1].StartLine);
    }

    [Fact]
    public async Task ExtractDocument_ConcurrentHealthyInvocations_BothExtractSuccessfully()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        DelegateExtractor extractor = new(async (_, output, _) =>
        {
            entered.TrySetResult();

            await release.Task;

            output.TryAppend("healthy");
        });

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromSeconds(5));

        Task<SessionAttachmentExtractionResult> first = ExtractAsync("ARCCOMP1\nhello", host: host);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<SessionAttachmentExtractionResult> second = ExtractAsync("ARCCOMP1\nhello", host: host);

        release.TrySetResult();

        SessionAttachmentExtractionResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status));

        Assert.All(results, result => Assert.Equal("healthy", result.Text));
    }

    [Fact]
    public async Task ExtractDocument_DeadlineCancelsBorrowedReadButWaitsForItsCompletionBeforeReturning()
    {
        TaskCompletionSource<ReadOnlyMemory<byte>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int parserCalls = 0;

        NoOpSessionAttachmentStore store = new(readBytes: (_, token) =>
        {
            token.Register(() => cancellationObserved.TrySetResult());

            entered.TrySetResult();

            return release.Task;
        });

        DelegateExtractor parser = new((_, output, _) =>
        {
            Interlocked.Increment(ref parserCalls);

            output.TryAppend("healthy");

            return ValueTask.CompletedTask;
        });

        SessionAttachmentDocumentExtractionHost host = new(parser.ExtractAsync, TimeSpan.FromMilliseconds(100));

        Task<SessionAttachmentExtractionResult>? extraction = null;

        try
        {
            extraction = SessionAttachmentTextExtractor.ExtractDocumentAsync(
                store, CreateRecord(14), Settings(), 1024, CancellationToken.None, host);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task completed = await Task.WhenAny(extraction, Task.Delay(50));

            Assert.NotSame(extraction, completed);

            Assert.False(extraction.IsCompleted);
        }
        finally
        {
            release.TrySetResult("ARCCOMP1\nhello"u8.ToArray());
        }

        Assert.NotNull(extraction);

        SessionAttachmentExtractionResult result = await extraction.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Empty(result.Text);

        Assert.Equal(0, parserCalls);

        SessionAttachmentExtractionResult healthy = await ExtractAsync("ARCCOMP1\nhello", host: host);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, healthy.Status);

        Assert.Equal(1, parserCalls);
    }

    [Fact]
    public async Task ExtractDocument_StorageFailurePropagatesRatherThanBecomingAParserFailure()
    {
        IOException failure = new("Encrypted bytes unavailable.");

        NoOpSessionAttachmentStore store = new(readBytes: (_, _) => Task.FromException<ReadOnlyMemory<byte>>(failure));

        IOException observed = await Assert.ThrowsAsync<IOException>(() => SessionAttachmentTextExtractor.ExtractDocumentAsync(
            store, CreateRecord(14), Settings(), 1024, CancellationToken.None));

        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task ExtractDocument_BlockingExtractor_TimesOutAndQuarantinesUntilItEnds()
    {
        using ManualResetEventSlim release = new(false);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int invocations = 0;

        DelegateExtractor extractor = new((_, output, _) =>
        {
            Interlocked.Increment(ref invocations);

            entered.TrySetResult();

            release.Wait();

            Assert.False(output.TryAppend("late output"));

            ended.TrySetResult();

            return ValueTask.CompletedTask;
        });

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromMilliseconds(100));

        try
        {
            Task<SessionAttachmentExtractionResult> first = ExtractAsync("ARCCOMP1\nhello", host: host);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            SessionAttachmentExtractionResult timedOut = await first.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SessionAttachmentExtractionStatus.Failed, timedOut.Status);

            Assert.Empty(timedOut.Text);

            SessionAttachmentExtractionResult quarantined = await ExtractAsync("ARCCOMP1\nhello", host: host);

            // Quarantine defers other documents (never fails them) and still admits no second parser.
            Assert.Equal(SessionAttachmentExtractionStatus.Deferred, quarantined.Status);

            Assert.Equal(1, invocations);

            Assert.Equal("healthy", SessionAttachmentTextExtractor.Extract("healthy"u8, "text/plain", "plain.txt").Text);
        }
        finally
        {
            release.Set();

            await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ExtractDocument_UncooperativeAsyncExtractor_IsBoundedAndHostCancellationPropagates()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        DelegateExtractor extractor = new(async (_, output, _) =>
        {
            entered.TrySetResult();

            await release.Task;

            Assert.False(output.TryAppend("late"));
        });

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromSeconds(5));

        using CancellationTokenSource cancellation = new();

        try
        {
            Task<SessionAttachmentExtractionResult> extraction = ExtractAsync(
                "ARCCOMP1\nhello", host: host, cancellationToken: cancellation.Token);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extraction);

            SessionAttachmentExtractionResult quarantined = await ExtractAsync("ARCCOMP1\nhello", host: host);

            // Quarantine defers other documents (never fails them) and still admits no second parser.
            Assert.Equal(SessionAttachmentExtractionStatus.Deferred, quarantined.Status);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    public static TheoryData<string> PackageRejections()
    {
        TheoryData<string> data = [];

        foreach (DocumentPackageRejection rejection in Enum.GetValues<DocumentPackageRejection>())
        {
            data.Add(rejection.ToString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PackageRejections))]
    public async Task ExtractDocument_PackageRejection_MapsToItsCodeOwnedStatusAndReason(string rejectionName)
    {
        DocumentPackageRejection rejection = Enum.Parse<DocumentPackageRejection>(rejectionName);

        DelegateExtractor extractor = new((_, _, _) => throw new DocumentPackageRejectedException(rejection));

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromSeconds(5));

        SessionAttachmentExtractionResult result = await ExtractAsync("ARCCOMP1\nhello", host: host);

        Assert.Equal(DocumentPackageRejections.Status(rejection), result.Status);

        Assert.Equal(DocumentPackageRejections.Reason(rejection), result.FailureReason);

        Assert.Empty(result.Text);

        Assert.False(result.WasTruncated);
    }

    [Theory]
    [InlineData("Encrypted", "NotEligible", "Encrypted document packages are not supported.")]
    [InlineData("UnsupportedContainer", "NotEligible", "Document package uses an unsupported ZIP feature.")]
    [InlineData("NotZip", "Failed", "Document package is not a valid ZIP container.")]
    [InlineData("EntryBounds", "Failed", "Document package exceeds its entry bounds.")]
    [InlineData("ExpansionBounds", "Failed", "Document package exceeds its expansion bounds.")]
    [InlineData("UnsafeEntryName", "Failed", "Document package contains an unsafe or duplicate entry name.")]
    [InlineData("MissingPart", "Failed", "Document package is missing a required part.")]
    [InlineData("FormatMismatch", "Failed", "Document package does not match its declared format.")]
    [InlineData("MalformedXml", "Failed", "Document part is not well-formed XML or declares a DTD.")]
    [InlineData("StructureBounds", "Failed", "Document structure exceeds its bounds.")]
    public async Task ExtractDocument_PackageRejection_PinsTheReviewedStatusTable(
        string rejectionName,
        string statusName,
        string reason)
    {
        DocumentPackageRejection rejection = Enum.Parse<DocumentPackageRejection>(rejectionName);

        SessionAttachmentExtractionStatus status = Enum.Parse<SessionAttachmentExtractionStatus>(statusName);

        DelegateExtractor extractor = new((_, _, _) => throw new DocumentPackageRejectedException(rejection));

        SessionAttachmentExtractionResult result = await ExtractAsync(
            "ARCCOMP1\nhello", host: new(extractor.ExtractAsync, TimeSpan.FromSeconds(5)));

        Assert.Equal(status, result.Status);

        Assert.Equal(reason, result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_PackageRejectionAfterAnAwait_IsMappedTheSameWay()
    {
        DelegateExtractor extractor = new(async (_, _, _) =>
        {
            await Task.Yield();

            throw new DocumentPackageRejectedException(DocumentPackageRejection.Encrypted);
        });

        SessionAttachmentExtractionResult result = await ExtractAsync(
            "ARCCOMP1\nhello", host: new(extractor.ExtractAsync, TimeSpan.FromSeconds(5)));

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Equal(DocumentPackageRejections.Reason(DocumentPackageRejection.Encrypted), result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_PackageRejection_DoesNotQuarantineTheHost()
    {
        int calls = 0;

        DelegateExtractor extractor = new((_, output, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.ExpansionBounds);
            }

            output.TryAppend("healthy");

            return ValueTask.CompletedTask;
        });

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromSeconds(5));

        SessionAttachmentExtractionResult rejected = await ExtractAsync("ARCCOMP1\nhello", host: host);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, rejected.Status);

        SessionAttachmentExtractionResult healthy = await ExtractAsync("ARCCOMP1\nhello", host: host);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, healthy.Status);

        Assert.Equal("healthy", healthy.Text);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExtractDocument_AnyOtherException_KeepsTheGenericReasonWithoutEchoingItsMessage()
    {
        DelegateExtractor extractor = new((_, _, _) =>
            throw new InvalidDataException("secret/entry-name.xml is malformed"));

        SessionAttachmentExtractionResult result = await ExtractAsync(
            "ARCCOMP1\nhello", host: new(extractor.ExtractAsync, TimeSpan.FromSeconds(5)));

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document extraction failed.", result.FailureReason);

        Assert.Empty(result.Text);
    }

    [Fact]
    public void BoundedSink_MarkTruncated_StopsLaterAppendsAndReportsTruncation()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        Assert.True(sink.TryAppend("kept"));

        sink.MarkTruncated();

        Assert.False(sink.TryAppend("dropped"));

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("kept", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void BoundedSink_MarkTruncated_DropsADanglingHighSurrogate()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        Assert.True(sink.TryAppend("a\uD83D"));

        sink.MarkTruncated();

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("a", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public async Task ExtractDocument_ExtractorThatMarksTruncation_ReportsTheRetainedTextAsTruncated()
    {
        DelegateExtractor extractor = new((_, output, _) =>
        {
            output.TryAppend("partial");

            output.MarkTruncated();

            return ValueTask.CompletedTask;
        });

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromSeconds(5));

        SessionAttachmentExtractionResult result = await ExtractAsync("ARCCOMP1\nhello", host: host);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("partial", result.Text);

        Assert.True(result.WasTruncated);
    }

    private static Task<SessionAttachmentExtractionResult> ExtractAsync(
        string content,
        SessionAttachmentRecord? record = null,
        SessionAttachmentDocumentExtractionHost? host = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        return SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            record ?? CreateRecord(bytes.Length), Settings(), 1024, cancellationToken, host);
    }

    private sealed class DelegateExtractor(
        Func<ReadOnlyMemory<byte>, ISessionAttachmentDocumentTextSink, CancellationToken, ValueTask> extract)
        : ISessionAttachmentDocumentExtractor
    {
        public ValueTask ExtractAsync(
            ReadOnlyMemory<byte> bytes,
            ISessionAttachmentDocumentTextSink output,
            CancellationToken cancellationToken) => extract(bytes, output, cancellationToken);
    }

    private static ArcanumSettings Settings() => new()
    {
        Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
    };

    private static SessionAttachmentRecord CreateRecord(long length) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
        "document", "document.arccomp", 1, "untrusted/path", "hash",
        "application/vnd.arcanum.comprehension-text", length, SessionAttachmentKind.Binary,
        DateTimeOffset.UtcNow);
}
