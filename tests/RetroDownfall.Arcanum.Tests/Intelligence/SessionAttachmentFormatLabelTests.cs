using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SessionAttachmentFormatLabelTests
{
    public static TheoryData<string, string> FormatLabels => new()
    {
        { "application/vnd.arcanum.comprehension-text", "Comprehension text container" },
        { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Word document" },
        { "application/vnd.ms-word.document.macroenabled.12", "Word macro-enabled document (macros not read)" },
        { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Excel workbook" },
        { "application/vnd.ms-excel.sheet.macroenabled.12", "Excel macro-enabled workbook (macros not read)" },
        { "application/vnd.openxmlformats-officedocument.presentationml.presentation", "PowerPoint presentation" },
        { "application/vnd.ms-powerpoint.presentation.macroenabled.12", "PowerPoint macro-enabled presentation (macros not read)" },
        { "application/vnd.oasis.opendocument.text", "OpenDocument text" },
        { "application/vnd.oasis.opendocument.spreadsheet", "OpenDocument spreadsheet" },
        { "application/vnd.oasis.opendocument.presentation", "OpenDocument presentation" },
        { "application/vnd.ms-visio.drawing", "Visio drawing" },
        { "application/vnd.ms-visio.drawing.macroenabled.12", "Visio macro-enabled drawing (macros not read)" },
    };

    [Theory]
    [MemberData(nameof(FormatLabels))]
    public void Retrieved_chunk_is_framed_with_the_label_of_its_source_format(string mimeType, string label)
    {
        string prompt = BuildPrompt(mimeType);

        string line = Assert.Single(SourceFormatLines(prompt));

        Assert.Equal($"source-format: {label}; layout not preserved", line);
    }

    public static TheoryData<string> LinesFormatMimeTypes()
    {
        TheoryData<string> data = [];

        foreach (SessionAttachmentComprehensionFormat format in SessionAttachmentComprehensionPolicy.Formats)
        {
            if (format.RangeUnit == SessionAttachmentRangeUnit.Lines)
            {
                data.Add(format.MimeType);
            }
        }

        return data;
    }

    [Theory]
    [InlineData(3, 3, "chunk/range: 5; chars 100-180; pages 3-3")]
    [InlineData(2, 3, "chunk/range: 5; chars 100-180; pages 2-3")]
    [InlineData(12, 40, "chunk/range: 5; chars 100-180; pages 12-40")]
    public void Retrieved_pdf_chunk_states_its_range_as_pages(int startPage, int endPage, string expected)
    {
        string prompt = BuildPrompt(SessionAttachmentComprehensionPolicy.PdfMimeType, startPage, endPage);

        Assert.Equal(expected, Assert.Single(RangeLines(prompt)));

        Assert.DoesNotContain("; lines ", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Retrieved_pdf_chunk_keeps_its_source_format_line_after_the_pages_range()
    {
        string prompt = BuildPrompt(SessionAttachmentComprehensionPolicy.PdfMimeType, 3, 3);

        Assert.Equal(
            "source-format: PDF document; layout not preserved; may include text a reader would not see",
            Assert.Single(SourceFormatLines(prompt)));

        int pagesIndex = prompt.IndexOf("; pages 3-3", StringComparison.Ordinal);

        Assert.True(pagesIndex >= 0);

        Assert.True(pagesIndex < prompt.IndexOf("source-format: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Retrieved_pdf_chunk_resolves_its_unit_through_the_normalized_mime()
    {
        string prompt = BuildPrompt(" Application/PDF ; charset=binary", 4, 6);

        Assert.Equal("chunk/range: 5; chars 100-180; pages 4-6", Assert.Single(RangeLines(prompt)));
    }

    [Theory]
    [MemberData(nameof(LinesFormatMimeTypes))]
    public void Retrieved_chunk_of_a_lines_format_keeps_stating_its_range_as_lines(string mimeType)
    {
        string prompt = BuildPrompt(mimeType, 8, 12);

        Assert.Equal("chunk/range: 5; chars 100-180; lines 8-12", Assert.Single(RangeLines(prompt)));

        Assert.DoesNotContain("; pages ", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/rtf")]
    [InlineData("application/octet-stream")]
    public void Retrieved_chunk_outside_the_registry_keeps_stating_its_range_as_lines(string mimeType)
    {
        Assert.Equal(
            "chunk/range: 5; chars 100-180; lines 8-12",
            Assert.Single(RangeLines(BuildPrompt(mimeType, 8, 12))));
    }

    [Fact]
    public void Retrieved_chunk_resolves_its_format_through_the_normalized_mime()
    {
        string prompt = BuildPrompt(
            " Application/Vnd.OpenXMLFormats-OfficeDocument.WordprocessingML.Document ; charset=utf-8");

        Assert.Equal(
            "source-format: Word document; layout not preserved",
            Assert.Single(SourceFormatLines(prompt)));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/rtf")]
    [InlineData("application/octet-stream")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.template")]
    public void Retrieved_chunk_outside_the_registry_has_no_source_format_line(string mimeType)
    {
        Assert.Empty(SourceFormatLines(BuildPrompt(mimeType)));
    }

    [Theory]
    [MemberData(nameof(FormatLabels))]
    public void Comprehension_notice_names_the_format_of_the_record(string mimeType, string label)
    {
        Assert.Equal(
            $"UNTRUSTED DATA; {label}; layout not preserved",
            SessionAttachmentToolInjection.ComprehensionDataNotice(Record(mimeType)));
    }

    [Theory]
    [MemberData(nameof(FormatLabels))]
    public void Comprehension_data_frame_carries_the_label_of_the_record_format(string mimeType, string label)
    {
        string framed = SessionAttachmentToolInjection.FormatComprehensionData(
            Record(mimeType), "report.bin", "extracted body");

        Assert.Equal(
            SystemPromptBuilder.FormatUntrusted(
                $"report.bin | UNTRUSTED DATA; {label}; layout not preserved", "extracted body"),
            framed);

        Assert.Contains($"UNTRUSTED DATA; {label}; layout not preserved", framed, StringComparison.Ordinal);

        Assert.Contains("extracted body", framed, StringComparison.Ordinal);
    }

    [Fact]
    public void Comprehension_notice_for_a_pdf_says_it_may_include_text_a_reader_would_not_see()
    {
        // #163 extracts every text-showing run, visible or not, so the frame has to say so in these exact words.
        Assert.Equal(
            "UNTRUSTED DATA; PDF document; layout not preserved; may include text a reader would not see",
            SessionAttachmentToolInjection.ComprehensionDataNotice(Record(SessionAttachmentComprehensionPolicy.PdfMimeType)));

        Assert.Equal(
            "UNTRUSTED DATA; PDF document; layout not preserved; may include text a reader would not see; truncated at the extraction limit",
            SessionAttachmentToolInjection.ComprehensionDataNotice(Record(" Application/PDF "), truncated: true));
    }

    [Fact]
    public void Comprehension_data_frame_for_a_pdf_carries_the_unseen_text_clause()
    {
        string framed = SessionAttachmentToolInjection.FormatComprehensionData(
            Record(SessionAttachmentComprehensionPolicy.PdfMimeType), "report.pdf", "extracted body");

        Assert.Equal(
            SystemPromptBuilder.FormatUntrusted(
                "report.pdf | UNTRUSTED DATA; PDF document; layout not preserved; may include text a reader would not see",
                "extracted body"),
            framed);
    }

    [Theory]
    [MemberData(nameof(FormatLabels))]
    public void Only_a_pdf_frame_says_it_may_include_text_a_reader_would_not_see(string mimeType, string label)
    {
        _ = label;

        Assert.DoesNotContain(
            "may include text a reader would not see",
            SessionAttachmentToolInjection.ComprehensionDataNotice(Record(mimeType)),
            StringComparison.Ordinal);

        Assert.DoesNotContain("may include text a reader would not see", BuildPrompt(mimeType), StringComparison.Ordinal);
    }

    [Fact]
    public void Comprehension_notice_keeps_the_container_text_byte_for_byte()
    {
        Assert.Equal(
            "UNTRUSTED DATA; Comprehension text container; layout not preserved",
            SessionAttachmentToolInjection.ComprehensionDataNotice(
                Record(SessionAttachmentComprehensionPolicy.MimeType)));
    }

    [Fact]
    public void Comprehension_notice_resolves_the_format_through_the_normalized_mime()
    {
        Assert.Equal(
            "UNTRUSTED DATA; OpenDocument text; layout not preserved",
            SessionAttachmentToolInjection.ComprehensionDataNotice(
                Record(" Application/Vnd.Oasis.OpenDocument.Text ; charset=utf-8")));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/rtf")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.template")]
    public void Comprehension_notice_refuses_a_record_outside_the_registry(string mimeType)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SessionAttachmentToolInjection.ComprehensionDataNotice(Record(mimeType)));
    }

    private static SessionAttachmentRecord Record(string mimeType) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
        "document", "document.bin", 1, "untrusted/path", "hash", mimeType, 16,
        SessionAttachmentKind.Binary, DateTimeOffset.UtcNow);

    private static string[] SourceFormatLines(string prompt) =>
    [
        .. prompt.Split('\n').Where(static line => line.StartsWith("source-format: ", StringComparison.Ordinal)),
    ];

    private static string[] RangeLines(string prompt) =>
    [
        .. prompt.Split('\n').Where(static line => line.StartsWith("chunk/range: ", StringComparison.Ordinal)),
    ];

    private static string BuildPrompt(string mimeType) => SystemPromptBuilder.Build(
        new PingRequest("hello"),
        codexContent: null,
        sessionAttachmentContext:
        [
            new SessionAttachmentRetrievedChunk(
                "chunk-1", Guid.NewGuid(), Guid.NewGuid(), "document", 1, "document.bin", mimeType,
                "abc123", 0, 0, 20, 1, 1, "retrieved document text", 0.9f),
        ]);

    private static string BuildPrompt(string mimeType, int startLine, int endLine) => SystemPromptBuilder.Build(
        new PingRequest("hello"),
        codexContent: null,
        sessionAttachmentContext:
        [
            new SessionAttachmentRetrievedChunk(
                "chunk-1", Guid.NewGuid(), Guid.NewGuid(), "document", 1, "document.bin", mimeType,
                "abc123", 4, 100, 180, startLine, endLine, "retrieved document text", 0.9f),
        ]);
}
