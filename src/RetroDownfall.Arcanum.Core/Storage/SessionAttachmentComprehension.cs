using System.Collections.ObjectModel;

using System.Diagnostics.CodeAnalysis;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>The unit a stored chunk's start and end range counts in, for the format it was extracted from.</summary>
public enum SessionAttachmentRangeUnit
{
    /// <summary>Lines of the extracted text.</summary>
    Lines,

    /// <summary>Pages of the source document.</summary>
    Pages,
}

/// <summary>
/// One reviewed Comprehension source format: its lowercase MIME type, the human-readable label
/// every model-facing framing shows for it, the unit its chunk ranges are stated in, and whether
/// its extracted text may include text a reader of the document would not see.
/// </summary>
/// <param name="MayIncludeUnseenText">
/// The format's text is extracted from every run that shows text, with no rendered-visibility
/// projection (PDF, per #163), so it may include white-on-white, invisible, tiny or off-page text.
/// </param>
public sealed record SessionAttachmentComprehensionFormat(
    string MimeType,
    string FormatName,
    SessionAttachmentRangeUnit RangeUnit = SessionAttachmentRangeUnit.Lines,
    bool MayIncludeUnseenText = false)
{
    /// <summary>The clause that tells a reader text from this format may include text the document does not show.</summary>
    public const string UnseenTextClause = "may include text a reader would not see";

    /// <summary>
    /// What every model-facing framing of this format's extracted text says after the untrusted-data warning: the label,
    /// that the layout is not preserved, and, for a format whose text may include text a reader would not see, that too.
    /// </summary>
    public string ExtractedTextDescription =>
        FormatName + "; layout not preserved" + (MayIncludeUnseenText ? "; " + UnseenTextClause : string.Empty);
}

/// <summary>Closed document eligibility for Comprehension; durable attachment kinds remain unchanged.</summary>
public static class SessionAttachmentComprehensionPolicy
{
    public const string MimeType = "application/vnd.arcanum.comprehension-text";

    public const string FormatName = "Comprehension text container";

    public const string WordDocumentMimeType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public const string WordMacroEnabledDocumentMimeType =
        "application/vnd.ms-word.document.macroenabled.12";

    public const string ExcelWorkbookMimeType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string ExcelMacroEnabledWorkbookMimeType =
        "application/vnd.ms-excel.sheet.macroenabled.12";

    public const string PowerPointPresentationMimeType =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public const string PowerPointMacroEnabledPresentationMimeType =
        "application/vnd.ms-powerpoint.presentation.macroenabled.12";

    public const string OpenDocumentTextMimeType = "application/vnd.oasis.opendocument.text";

    public const string OpenDocumentSpreadsheetMimeType = "application/vnd.oasis.opendocument.spreadsheet";

    public const string OpenDocumentPresentationMimeType = "application/vnd.oasis.opendocument.presentation";

    public const string VisioDrawingMimeType = "application/vnd.ms-visio.drawing";

    public const string VisioMacroEnabledDrawingMimeType = "application/vnd.ms-visio.drawing.macroenabled.12";

    public const string PdfMimeType = "application/pdf";

    // The registry's entries. TryGetFormat walks this array rather than Formats: an array's enumeration is
    // closed, while a read-only collection's enumerator runs whatever list it wraps.
    private static readonly SessionAttachmentComprehensionFormat[] FormatTable =
        [
            new(MimeType, FormatName),
            new(WordDocumentMimeType, "Word document"),
            new(WordMacroEnabledDocumentMimeType, "Word macro-enabled document (macros not read)"),
            new(ExcelWorkbookMimeType, "Excel workbook"),
            new(ExcelMacroEnabledWorkbookMimeType, "Excel macro-enabled workbook (macros not read)"),
            new(PowerPointPresentationMimeType, "PowerPoint presentation"),
            new(PowerPointMacroEnabledPresentationMimeType, "PowerPoint macro-enabled presentation (macros not read)"),
            new(OpenDocumentTextMimeType, "OpenDocument text"),
            new(OpenDocumentSpreadsheetMimeType, "OpenDocument spreadsheet"),
            new(OpenDocumentPresentationMimeType, "OpenDocument presentation"),
            new(VisioDrawingMimeType, "Visio drawing"),
            new(VisioMacroEnabledDrawingMimeType, "Visio macro-enabled drawing (macros not read)"),
            new(PdfMimeType, "PDF document", SessionAttachmentRangeUnit.Pages, MayIncludeUnseenText: true),
        ];

    /// <summary>
    /// The closed registry, container first. MIME constants are lowercase and free of quotes and
    /// parameters, so SQL can compare them against <c>lower(trim(...))</c> of a stored MIME and
    /// inline them as literals. A macro-enabled variant is accepted as text only.
    /// </summary>
    public static IReadOnlyList<SessionAttachmentComprehensionFormat> Formats { get; } =
        new ReadOnlyCollection<SessionAttachmentComprehensionFormat>(FormatTable);

    public static IReadOnlyList<string> SupportedMimeTypes { get; } =
        new ReadOnlyCollection<string>([.. Formats.Select(static format => format.MimeType)]);

    /// <summary>
    /// Resolves a stored or declared MIME type, ignoring parameters, surrounding space, tab, CR and LF, and
    /// ASCII case (<see cref="NormalizeMimeType"/>), to its registry entry. A MIME outside the registry
    /// resolves to nothing.
    /// </summary>
    public static bool TryGetFormat(
        string mimeType,
        [NotNullWhen(true)] out SessionAttachmentComprehensionFormat? format)
    {
        string normalized = NormalizeMimeType(mimeType);

        foreach (SessionAttachmentComprehensionFormat candidate in FormatTable)
        {
            if (string.Equals(candidate.MimeType, normalized, StringComparison.Ordinal))
            {
                format = candidate;

                return true;
            }
        }

        format = null;

        return false;
    }

    public static bool IsEnabled(ArcanumSettings settings) =>
        settings.Features?.AttachmentRetrieval == true && settings.Features?.Comprehension == true;

    public static bool IsEligibleDocument(SessionAttachmentRecord record, ArcanumSettings settings) =>
        IsEnabled(settings)
        && record.Kind == SessionAttachmentKind.Binary
        && TryGetFormat(record.MimeType, out _);

    /// <summary>
    /// The one normalisation of a stored or declared MIME type: parameters are cut at the first semicolon,
    /// only space, tab, CR and LF are trimmed from the ends, and A-Z fold to a-z. Nothing else is touched, so a
    /// no-break space, a form feed or a non-ASCII letter stays part of the value. SQLite's
    /// <c>lower(trim(substr(...), ' ' || char(9) || char(10) || char(13)))</c> does exactly this, and
    /// <c>SessionAttachmentDocumentMimeSql</c> in Infrastructure spells it for every statement that classifies a MIME,
    /// so C# and SQL read a spelling as the same registry type or both read it as none.
    /// </summary>
    public static string NormalizeMimeType(string mimeType)
    {
        ArgumentNullException.ThrowIfNull(mimeType);

        int separator = mimeType.IndexOf(';', StringComparison.Ordinal);

        ReadOnlySpan<char> media = (separator < 0 ? mimeType.AsSpan() : mimeType.AsSpan(0, separator)).Trim(AsciiTrimCharacters);

        return string.Create(media.Length, media, static (destination, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];

                destination[index] = character is >= 'A' and <= 'Z' ? (char)(character | 0x20) : character;
            }
        });
    }

    private static ReadOnlySpan<char> AsciiTrimCharacters => " \t\r\n";
}

/// <summary>
/// Pure bytes-to-text implementation for one reviewed document format. Implementations must observe
/// cancellation between bounded operations, avoid allocations proportional to untrusted expanded
/// output, and stop immediately when the sink returns false. They may not access files, services,
/// providers, persistence or ambient authority. Effectful extractors are not supported. The host bounds
/// parser waiting and retained output; it cannot preempt arbitrary in-process code or bound an
/// implementation's private allocations.
/// </summary>
public interface ISessionAttachmentDocumentExtractor
{
    ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken);
}

/// <summary>Bounded text output. False means the ceiling was reached and extraction must stop.</summary>
public interface ISessionAttachmentDocumentTextSink
{
    bool TryAppend(ReadOnlySpan<char> text);

    /// <summary>
    /// Reports that the extractor withheld text it could not retain. Later appends return false and the
    /// result reports truncation, so a retention overflow is never presented as a complete document.
    /// </summary>
    void MarkTruncated();

    /// <summary>
    /// Marks that the text appended from now on starts the 1-based page <paramref name="pageNumber"/> of the
    /// source document, so a retained character can be traced back to its page. A sink that keeps no page
    /// provenance ignores the call, which is the default.
    /// </summary>
    void BeginPage(int pageNumber)
    {
    }
}
