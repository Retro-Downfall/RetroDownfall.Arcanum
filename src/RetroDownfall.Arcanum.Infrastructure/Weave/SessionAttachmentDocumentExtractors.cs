using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Platform;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// The closed set of extraction hosts, one per parser family. Each in-process host binds a concrete
/// extractor method group at construction, so quarantine is per family and the set of extractors that can
/// run is visible to review and to the hosted-producer analysis. A macro-enabled variant shares the host of
/// its base format, since both are read by the same parser and macros are never opened. PDF is parsed only
/// out of process, by the jailed worker its isolated host awaits.
/// </summary>
internal static class SessionAttachmentDocumentExtractors
{
    private static readonly SessionAttachmentDocumentExtractionHost TextContainer = new(
        new ComprehensionTextContainerExtractor().ExtractAsync,
        TimeSpan.FromSeconds(5));

    private static readonly SessionAttachmentDocumentExtractionHost Wordprocessing = new(
        new WordprocessingDocumentExtractor().ExtractAsync,
        TimeSpan.FromSeconds(5));

    private static readonly SessionAttachmentDocumentExtractionHost Spreadsheet = new(
        new SpreadsheetDocumentExtractor().ExtractAsync,
        TimeSpan.FromSeconds(5));

    private static readonly SessionAttachmentDocumentExtractionHost Presentation = new(
        new PresentationDocumentExtractor().ExtractAsync,
        TimeSpan.FromSeconds(5));

    private static readonly SessionAttachmentDocumentExtractionHost OpenDocumentText = new(
        new OpenDocumentTextExtractor().ExtractAsync,
        TimeSpan.FromSeconds(5));

    private static readonly SessionAttachmentDocumentExtractionHost OpenDocumentSpreadsheet = new(
        new OpenDocumentSpreadsheetExtractor().ExtractAsync,
        TimeSpan.FromSeconds(5));

    private static readonly SessionAttachmentDocumentExtractionHost OpenDocumentPresentation = new(
        new OpenDocumentPresentationExtractor().ExtractAsync,
        TimeSpan.FromSeconds(5));

    private static readonly SessionAttachmentDocumentExtractionHost Visio = new(
        new VisioDrawingExtractor().ExtractAsync,
        TimeSpan.FromSeconds(5));

    private static readonly SessionAttachmentIsolatedDocumentHost Pdf = new(
        new ProcessDocumentExtractionWorker(new ProcessResourceLimiter()),
        DocumentWorkerFormat.Pdf);

    /// <summary>The host of the Comprehension text container, which is also the default host.</summary>
    internal static SessionAttachmentDocumentExtractionHost TextContainerHost => TextContainer;

    /// <summary>
    /// Resolves the host for a registry MIME type, ignoring parameters, whitespace and case. A MIME
    /// outside the registry has no host.
    /// </summary>
    internal static SessionAttachmentDocumentHost? For(string mimeType)
    {
        if (!SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format))
        {
            return null;
        }

        return format.MimeType switch
        {
            SessionAttachmentComprehensionPolicy.MimeType => TextContainer,
            SessionAttachmentComprehensionPolicy.WordDocumentMimeType
                or SessionAttachmentComprehensionPolicy.WordMacroEnabledDocumentMimeType => Wordprocessing,
            SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType
                or SessionAttachmentComprehensionPolicy.ExcelMacroEnabledWorkbookMimeType => Spreadsheet,
            SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType
                or SessionAttachmentComprehensionPolicy.PowerPointMacroEnabledPresentationMimeType => Presentation,
            SessionAttachmentComprehensionPolicy.OpenDocumentTextMimeType => OpenDocumentText,
            SessionAttachmentComprehensionPolicy.OpenDocumentSpreadsheetMimeType => OpenDocumentSpreadsheet,
            SessionAttachmentComprehensionPolicy.OpenDocumentPresentationMimeType => OpenDocumentPresentation,
            SessionAttachmentComprehensionPolicy.VisioDrawingMimeType
                or SessionAttachmentComprehensionPolicy.VisioMacroEnabledDrawingMimeType => Visio,
            SessionAttachmentComprehensionPolicy.PdfMimeType => Pdf,
            _ => null,
        };
    }
}
