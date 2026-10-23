using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class SessionAttachmentComprehensionPolicyTests
{
    public static TheoryData<string, string> ExpectedFormats => new()
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
        { "application/pdf", "PDF document" },
    };

    [Fact]
    public void Formats_list_the_container_first_then_each_office_format_then_pdf_in_the_reviewed_order()
    {
        Assert.Equal(
            [
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.arcanum.comprehension-text", "Comprehension text container"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Word document"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.ms-word.document.macroenabled.12", "Word macro-enabled document (macros not read)"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Excel workbook"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.ms-excel.sheet.macroenabled.12", "Excel macro-enabled workbook (macros not read)"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.openxmlformats-officedocument.presentationml.presentation", "PowerPoint presentation"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.ms-powerpoint.presentation.macroenabled.12",
                    "PowerPoint macro-enabled presentation (macros not read)"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.oasis.opendocument.text", "OpenDocument text"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.oasis.opendocument.spreadsheet", "OpenDocument spreadsheet"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.oasis.opendocument.presentation", "OpenDocument presentation"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.ms-visio.drawing", "Visio drawing"),
                new SessionAttachmentComprehensionFormat(
                    "application/vnd.ms-visio.drawing.macroenabled.12", "Visio macro-enabled drawing (macros not read)"),
                new SessionAttachmentComprehensionFormat(
                    "application/pdf", "PDF document", SessionAttachmentRangeUnit.Pages),
            ],
            SessionAttachmentComprehensionPolicy.Formats);
    }

    [Fact]
    public void Range_unit_is_pages_for_pdf_and_lines_for_every_other_format()
    {
        Assert.Equal(
            [SessionAttachmentComprehensionPolicy.PdfMimeType],
            SessionAttachmentComprehensionPolicy.Formats
                .Where(static format => format.RangeUnit == SessionAttachmentRangeUnit.Pages)
                .Select(static format => format.MimeType));

        Assert.All(
            SessionAttachmentComprehensionPolicy.Formats.Where(
                static format => format.MimeType != SessionAttachmentComprehensionPolicy.PdfMimeType),
            static format => Assert.Equal(SessionAttachmentRangeUnit.Lines, format.RangeUnit));
    }

    [Fact]
    public void Range_unit_defaults_to_lines()
    {
        Assert.Equal(SessionAttachmentRangeUnit.Lines, new SessionAttachmentComprehensionFormat("a/b", "label").RangeUnit);

        Assert.Equal(SessionAttachmentRangeUnit.Lines, default(SessionAttachmentRangeUnit));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData(" Application/PDF ; charset=utf-8")]
    public void TryGetFormat_resolves_pdf_to_the_pages_unit(string mimeType)
    {
        Assert.True(SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format));

        Assert.Equal("PDF document", format.FormatName);

        Assert.Equal(SessionAttachmentRangeUnit.Pages, format.RangeUnit);
    }

    [Fact]
    public void Container_constants_are_unchanged_so_existing_pins_stay_valid()
    {
        Assert.Equal("application/vnd.arcanum.comprehension-text", SessionAttachmentComprehensionPolicy.MimeType);

        Assert.Equal("Comprehension text container", SessionAttachmentComprehensionPolicy.FormatName);

        Assert.Same(
            SessionAttachmentComprehensionPolicy.Formats[0],
            Assert.Single(
                SessionAttachmentComprehensionPolicy.Formats,
                static format => format.MimeType == SessionAttachmentComprehensionPolicy.MimeType));
    }

    [Fact]
    public void Mime_constants_name_the_registry_values()
    {
        Assert.Equal(
            [
                SessionAttachmentComprehensionPolicy.MimeType,
                SessionAttachmentComprehensionPolicy.WordDocumentMimeType,
                SessionAttachmentComprehensionPolicy.WordMacroEnabledDocumentMimeType,
                SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType,
                SessionAttachmentComprehensionPolicy.ExcelMacroEnabledWorkbookMimeType,
                SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType,
                SessionAttachmentComprehensionPolicy.PowerPointMacroEnabledPresentationMimeType,
                SessionAttachmentComprehensionPolicy.OpenDocumentTextMimeType,
                SessionAttachmentComprehensionPolicy.OpenDocumentSpreadsheetMimeType,
                SessionAttachmentComprehensionPolicy.OpenDocumentPresentationMimeType,
                SessionAttachmentComprehensionPolicy.VisioDrawingMimeType,
                SessionAttachmentComprehensionPolicy.VisioMacroEnabledDrawingMimeType,
                SessionAttachmentComprehensionPolicy.PdfMimeType,
            ],
            SessionAttachmentComprehensionPolicy.Formats.Select(static format => format.MimeType));
    }

    [Fact]
    public void SupportedMimeTypes_are_derived_from_the_formats()
    {
        Assert.Equal(
            SessionAttachmentComprehensionPolicy.Formats.Select(static format => format.MimeType),
            SessionAttachmentComprehensionPolicy.SupportedMimeTypes);
    }

    [Fact]
    public void Registry_mime_types_are_unique_lowercase_and_safe_to_inline_in_sql()
    {
        string[] mimeTypes = [.. SessionAttachmentComprehensionPolicy.Formats.Select(static format => format.MimeType)];

        Assert.Equal(mimeTypes.Length, mimeTypes.Distinct(StringComparer.Ordinal).Count());

        Assert.All(mimeTypes, mimeType =>
        {
            Assert.Equal(mimeType.ToLowerInvariant(), mimeType);

            Assert.Equal(mimeType.Trim(), mimeType);

            Assert.DoesNotContain('\'', mimeType);

            Assert.DoesNotContain(';', mimeType);
        });
    }

    [Theory]
    [MemberData(nameof(ExpectedFormats))]
    public void TryGetFormat_resolves_each_registry_mime_to_its_label(string mimeType, string label)
    {
        Assert.True(SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format));

        Assert.Equal(mimeType, format.MimeType);

        Assert.Equal(label, format.FormatName);
    }

    [Theory]
    [MemberData(nameof(ExpectedFormats))]
    public void TryGetFormat_normalizes_case_whitespace_and_parameters(string mimeType, string label)
    {
        string decorated = " " + mimeType.ToUpperInvariant() + " ; charset=utf-8";

        Assert.True(SessionAttachmentComprehensionPolicy.TryGetFormat(decorated, out SessionAttachmentComprehensionFormat? format));

        Assert.Equal(mimeType, format.MimeType);

        Assert.Equal(label, format.FormatName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("application/rtf")]
    [InlineData("application/octet-stream")]
    [InlineData("application/msword")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.template")]
    [InlineData("application/vnd.ms-word.template.macroenabled.12")]
    [InlineData("application/vnd.oasis.opendocument.text-template")]
    [InlineData("application/vnd.oasis.opendocument.text-flat-xml")]
    [InlineData("application/vnd.ms-visio.stencil")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document.extra")]
    public void TryGetFormat_refuses_everything_outside_the_closed_registry(string mimeType)
    {
        Assert.False(SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format));

        Assert.Null(format);
    }

    [Fact]
    public void TryGetFormat_rejects_a_null_mime_type()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SessionAttachmentComprehensionPolicy.TryGetFormat(null!, out _));
    }

    [Theory]
    [MemberData(nameof(ExpectedFormats))]
    public void IsEligibleDocument_accepts_each_registry_mime_only_as_an_opted_in_binary(string mimeType, string label)
    {
        Assert.NotEmpty(label);

        ArcanumSettings enabled = Settings(retrieval: true, comprehension: true);

        Assert.True(SessionAttachmentComprehensionPolicy.IsEligibleDocument(Record(mimeType), enabled));

        Assert.True(SessionAttachmentComprehensionPolicy.IsEligibleDocument(
            Record(" " + mimeType.ToUpperInvariant() + " ; charset=utf-8"), enabled));

        Assert.False(SessionAttachmentComprehensionPolicy.IsEligibleDocument(
            Record(mimeType, SessionAttachmentKind.Text), enabled));

        Assert.False(SessionAttachmentComprehensionPolicy.IsEligibleDocument(
            Record(mimeType), Settings(retrieval: true, comprehension: false)));

        Assert.False(SessionAttachmentComprehensionPolicy.IsEligibleDocument(
            Record(mimeType), Settings(retrieval: false, comprehension: true)));
    }

    [Theory]
    [InlineData("application/rtf")]
    [InlineData("application/octet-stream")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.template")]
    public void IsEligibleDocument_refuses_a_mime_outside_the_registry(string mimeType)
    {
        Assert.False(SessionAttachmentComprehensionPolicy.IsEligibleDocument(
            Record(mimeType), Settings(retrieval: true, comprehension: true)));
    }

    private static ArcanumSettings Settings(bool retrieval, bool comprehension) => new()
    {
        Features = new FeatureSettings { AttachmentRetrieval = retrieval, Comprehension = comprehension },
    };

    private static SessionAttachmentRecord Record(string mimeType, SessionAttachmentKind kind = SessionAttachmentKind.Binary) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
        "document", "document", 1, "untrusted/path", "hash", mimeType, 16, kind, DateTimeOffset.UtcNow);
}
