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
    [InlineData("application/pdf")]
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
    [InlineData("application/pdf")]
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

    private static string BuildPrompt(string mimeType) => SystemPromptBuilder.Build(
        new PingRequest("hello"),
        codexContent: null,
        sessionAttachmentContext:
        [
            new SessionAttachmentRetrievedChunk(
                "chunk-1", Guid.NewGuid(), Guid.NewGuid(), "document", 1, "document.bin", mimeType,
                "abc123", 0, 0, 20, 1, 1, "retrieved document text", 0.9f),
        ]);
}
