using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// One real package per Comprehension registry MIME, for tests that drive a model-context path from the
/// attachment record onwards. The Office, OpenDocument and Visio bytes are the checked-in real-producer
/// files; the text container is built inline.
/// </summary>
internal static class ComprehensionSeamDocuments
{
    private static readonly Dictionary<string, string> RealProducerFiles = new(StringComparer.Ordinal)
    {
        [SessionAttachmentComprehensionPolicy.WordDocumentMimeType] = "Word/textutil-multi.docx",
        [SessionAttachmentComprehensionPolicy.WordMacroEnabledDocumentMimeType] = "Word/textutil-multi.docx",
        [SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType] = "Excel/openpyxl-basic.xlsx",
        [SessionAttachmentComprehensionPolicy.ExcelMacroEnabledWorkbookMimeType] = "Excel/xlsxwriter-macro.xlsm",
        [SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType] = "PowerPoint/python-pptx-three-slides.pptx",
        [SessionAttachmentComprehensionPolicy.PowerPointMacroEnabledPresentationMimeType] = "PowerPoint/python-pptx-three-slides.pptx",
        [SessionAttachmentComprehensionPolicy.OpenDocumentTextMimeType] = "OpenDocument/textutil-rich.odt",
        [SessionAttachmentComprehensionPolicy.OpenDocumentSpreadsheetMimeType] = "OpenDocument/handmade-libreoffice-style.ods",
        [SessionAttachmentComprehensionPolicy.OpenDocumentPresentationMimeType] = "OpenDocument/handmade-libreoffice-style.odp",
        [SessionAttachmentComprehensionPolicy.VisioDrawingMimeType] = "Visio/hand-built-stencil.vsdx",
        [SessionAttachmentComprehensionPolicy.VisioMacroEnabledDrawingMimeType] = "Visio/hand-built-stencil.vsdm",
    };

    /// <summary>The bytes of a document of the registry type <paramref name="mimeType"/>.</summary>
    public static byte[] Bytes(string mimeType) =>
        string.Equals(mimeType, SessionAttachmentComprehensionPolicy.MimeType, StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes("ARCCOMP1\nContainer sentinel text")
            : File.ReadAllBytes(
                Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", RealProducerFiles[mimeType]));

    /// <summary>A Bound durable <see cref="SessionAttachmentKind.Binary"/> record, as intake stores a document.</summary>
    public static SessionAttachmentRecord Record(
        Guid sessionId,
        string mimeType,
        int byteLength,
        string logicalKey = "document",
        int version = 1) =>
        new(
            Guid.NewGuid(),
            sessionId,
            null,
            null,
            SessionAttachmentState.Bound,
            logicalKey,
            logicalKey + ".bin",
            version,
            "missing/" + logicalKey,
            "document-hash",
            mimeType,
            byteLength,
            SessionAttachmentKind.Binary,
            DateTimeOffset.UtcNow);
}
