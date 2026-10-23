using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// Page provenance on the bounded document sink: a mark per page that retained at least one character, at the
/// offset of that character in the retained text, and nothing for a document that reports no pages.
/// </summary>
public sealed class SessionAttachmentPageMarkTests
{
    private const int MarkCap = 2_000;

    [Fact]
    public void BeginPage_RecordsTheOffsetOfTheFirstRetainedCharacterOfEachPage()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.BeginPage(1);

        sink.TryAppend("abc");

        sink.BeginPage(2);

        sink.TryAppend("de");

        sink.TryAppend("f");

        sink.BeginPage(3);

        sink.TryAppend("g");

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("abcdefg", result.Text);

        Assert.Equal(
            [new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(3, 2), new SessionAttachmentPageMark(6, 3)],
            result.Pages);
    }

    [Fact]
    public void BeginPage_KeepsTheDocumentsOwnPageNumbersWhateverTheirGaps()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.BeginPage(4);

        sink.TryAppend("ab");

        sink.BeginPage(9);

        sink.TryAppend("cd");

        Assert.Equal(
            [new SessionAttachmentPageMark(0, 4), new SessionAttachmentPageMark(2, 9)],
            sink.Complete().Pages);
    }

    [Fact]
    public void NoBeginPage_LeavesThePagesNull()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.TryAppend("plain text");

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("plain text", result.Text);

        Assert.Null(result.Pages);
    }

    [Fact]
    public void BeginPage_ForAPageWithNoCharacters_RecordsNoMark()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.BeginPage(1);

        sink.TryAppend("a");

        sink.BeginPage(2);

        sink.BeginPage(3);

        sink.TryAppend("b");

        sink.BeginPage(4);

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("ab", result.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(1, 3)], result.Pages);
    }

    [Fact]
    public void BeginPage_ForEveryPageEmpty_KeepsTheDocumentNotEligibleWithNoPages()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.BeginPage(1);

        sink.BeginPage(2);

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);

        Assert.Null(result.Pages);
    }

    [Fact]
    public void BeginPage_AfterACarriageReturnEndingThePreviousPage_AttributesThatLineBreakToThePreviousPage()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.BeginPage(1);

        sink.TryAppend("a\r");

        sink.BeginPage(2);

        sink.TryAppend("b");

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("a\nb", result.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(2, 2)], result.Pages);
    }

    [Fact]
    public void BeginPage_AfterACarriageReturnAndBeforeALineFeed_KeepsTwoLineBreaksOnTheirOwnPages()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.BeginPage(1);

        sink.TryAppend("a\r");

        sink.BeginPage(2);

        sink.TryAppend("\nb");

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("a\n\nb", result.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(2, 2)], result.Pages);
    }

    [Fact]
    public void BeginPage_ForAPageHoldingOnlyACarriageReturn_CountsItsLineBreakAsRetainedText()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.BeginPage(1);

        sink.TryAppend("\r");

        sink.BeginPage(2);

        sink.BeginPage(3);

        sink.TryAppend("x");

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("\nx", result.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(1, 3)], result.Pages);
    }

    [Fact]
    public void BeginPage_AfterTruncationByTheCeiling_RecordsNoLaterMark()
    {
        BoundedDocumentTextSink sink = new(4, CancellationToken.None);

        sink.BeginPage(1);

        Assert.True(sink.TryAppend("abc"));

        sink.BeginPage(2);

        Assert.False(sink.TryAppend("defg"));

        sink.BeginPage(3);

        Assert.False(sink.TryAppend("h"));

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("abcd", result.Text);

        Assert.True(result.WasTruncated);

        Assert.Equal([new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(3, 2)], result.Pages);
    }

    [Fact]
    public void BeginPage_AfterMarkTruncated_RecordsNoLaterMark()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.BeginPage(1);

        sink.TryAppend("kept");

        sink.MarkTruncated();

        sink.BeginPage(2);

        Assert.False(sink.TryAppend("dropped"));

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("kept", result.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 1)], result.Pages);
    }

    [Fact]
    public void BeginPage_ForAPageWhoseOnlyCharacterIsADanglingHighSurrogate_RecordsNoMarkWhenTruncationDropsIt()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        sink.BeginPage(1);

        sink.TryAppend("a");

        sink.BeginPage(2);

        sink.TryAppend("\uD83D");

        sink.MarkTruncated();

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("a", result.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 1)], result.Pages);
    }

    [Fact]
    public void BeginPage_ForAPageWhoseOnlyCharacterIsAHighSurrogateCutByTheCeiling_RecordsNoMark()
    {
        BoundedDocumentTextSink sink = new(2, CancellationToken.None);

        sink.BeginPage(1);

        sink.TryAppend("a");

        sink.BeginPage(2);

        sink.TryAppend("\uD83D");

        Assert.False(sink.TryAppend("x"));

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("a", result.Text);

        Assert.True(result.WasTruncated);

        Assert.Equal([new SessionAttachmentPageMark(0, 1)], result.Pages);
    }

    [Fact]
    public void BeginPage_AtTheMarkCap_KeepsEveryMark()
    {
        BoundedDocumentTextSink sink = new(1_000_000, CancellationToken.None);

        for (int page = 1; page <= MarkCap; page++)
        {
            sink.BeginPage(page);

            Assert.True(sink.TryAppend("x"));
        }

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.False(result.WasTruncated);

        Assert.Equal(MarkCap, result.Text.Length);

        Assert.Equal(MarkCap, result.Pages!.Count);

        Assert.Equal(new SessionAttachmentPageMark(MarkCap - 1, MarkCap), result.Pages[^1]);
    }

    [Fact]
    public void BeginPage_PastTheMarkCap_TruncatesRatherThanLeavingTextOnAWrongPage()
    {
        BoundedDocumentTextSink sink = new(1_000_000, CancellationToken.None);

        for (int page = 1; page <= MarkCap; page++)
        {
            sink.BeginPage(page);

            Assert.True(sink.TryAppend("x"));
        }

        sink.BeginPage(MarkCap + 1);

        Assert.False(sink.TryAppend("tail"));

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.True(result.WasTruncated);

        Assert.Equal(new string('x', MarkCap), result.Text);

        Assert.Equal(MarkCap, result.Pages!.Count);

        Assert.Equal(new SessionAttachmentPageMark(MarkCap - 1, MarkCap), result.Pages[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void BeginPage_RefusesAPageNumberBelowOne(int pageNumber)
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        Assert.Throws<ArgumentOutOfRangeException>(() => sink.BeginPage(pageNumber));
    }

    [Fact]
    public void BeginPage_OnAnInterfaceReferenceReachesTheSink()
    {
        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        ISessionAttachmentDocumentTextSink output = sink;

        output.BeginPage(7);

        output.TryAppend("z");

        Assert.Equal([new SessionAttachmentPageMark(0, 7)], sink.Complete().Pages);
    }

    [Fact]
    public void BeginPage_OnASinkThatKeepsNoProvenance_IsANoOp()
    {
        ISessionAttachmentDocumentTextSink output = new ProvenanceBlindSink();

        output.BeginPage(3);

        Assert.True(output.TryAppend("text"));
    }

    [Fact]
    public async Task Host_CarriesThePageMarksOfAnExtractorThatReportsPages()
    {
        DelegateExtractor extractor = new((_, output, _) =>
        {
            output.BeginPage(1);

            output.TryAppend("first page");

            output.BeginPage(2);

            output.BeginPage(3);

            output.TryAppend("third page");

            return ValueTask.CompletedTask;
        });

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromSeconds(5));

        SessionAttachmentExtractionResult result = await ExtractAsync(host);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("first pagethird page", result.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(10, 3)], result.Pages);
    }

    [Fact]
    public async Task Host_ReportsNoPagesForAnExtractorThatNeverReportsOne()
    {
        DelegateExtractor extractor = new((_, output, _) =>
        {
            output.TryAppend("no pages here");

            return ValueTask.CompletedTask;
        });

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromSeconds(5));

        SessionAttachmentExtractionResult result = await ExtractAsync(host);

        Assert.Equal("no pages here", result.Text);

        Assert.Null(result.Pages);
    }

    [Fact]
    public async Task Host_TurnsARefusedPageNumberIntoTheGenericFailure()
    {
        DelegateExtractor extractor = new((_, output, _) =>
        {
            output.BeginPage(0);

            return ValueTask.CompletedTask;
        });

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromSeconds(5));

        SessionAttachmentExtractionResult result = await ExtractAsync(host);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document extraction failed.", result.FailureReason);

        Assert.Null(result.Pages);
    }

    private static Task<SessionAttachmentExtractionResult> ExtractAsync(SessionAttachmentDocumentExtractionHost host)
    {
        byte[] bytes = "payload"u8.ToArray();

        return SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            new SessionAttachmentRecord(
                Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
                "document", "document.arccomp", 1, "untrusted/path", "hash",
                SessionAttachmentComprehensionPolicy.MimeType, bytes.Length, SessionAttachmentKind.Binary,
                DateTimeOffset.UtcNow),
            new ArcanumSettings
            {
                Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
            },
            1024,
            CancellationToken.None,
            host);
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

    private sealed class ProvenanceBlindSink : ISessionAttachmentDocumentTextSink
    {
        public bool TryAppend(ReadOnlySpan<char> text) => true;

        public void MarkTruncated()
        {
        }
    }
}
