using System.Collections.ObjectModel;

using System.Diagnostics.CodeAnalysis;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// One reviewed Comprehension source format: its lowercase MIME type and the human-readable label
/// every model-facing framing shows for it.
/// </summary>
public sealed record SessionAttachmentComprehensionFormat(string MimeType, string FormatName);

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

    /// <summary>
    /// The closed registry, container first. MIME constants are lowercase and free of quotes and
    /// parameters, so SQL can compare them against <c>lower(trim(...))</c> of a stored MIME and
    /// inline them as literals. A macro-enabled variant is accepted as text only.
    /// </summary>
    public static IReadOnlyList<SessionAttachmentComprehensionFormat> Formats { get; } =
        new ReadOnlyCollection<SessionAttachmentComprehensionFormat>(
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
        ]);

    public static IReadOnlyList<string> SupportedMimeTypes { get; } =
        new ReadOnlyCollection<string>([.. Formats.Select(static format => format.MimeType)]);

    /// <summary>
    /// Resolves a stored or declared MIME type, ignoring parameters, surrounding whitespace and case,
    /// to its registry entry. A MIME outside the registry resolves to nothing.
    /// </summary>
    public static bool TryGetFormat(
        string mimeType,
        [NotNullWhen(true)] out SessionAttachmentComprehensionFormat? format)
    {
        string normalized = NormalizeMimeType(mimeType);

        foreach (SessionAttachmentComprehensionFormat candidate in Formats)
        {
            if (string.Equals(candidate.MimeType, normalized, StringComparison.OrdinalIgnoreCase))
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

    public static string NormalizeMimeType(string mimeType)
    {
        ArgumentNullException.ThrowIfNull(mimeType);

        int separator = mimeType.IndexOf(';', StringComparison.Ordinal);

        return (separator < 0 ? mimeType : mimeType[..separator]).Trim();
    }
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
}
