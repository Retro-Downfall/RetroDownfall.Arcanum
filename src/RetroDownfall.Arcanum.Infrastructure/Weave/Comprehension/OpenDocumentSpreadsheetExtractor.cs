using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>Extracts the text a reader sees in an OpenDocument spreadsheet package (<c>.ods</c>).</summary>
/// <remarks>
/// Hidden-content posture is plan decision D8; the projection is D9; the bounds are D5. Only
/// <c>META-INF/manifest.xml</c>, <c>content.xml</c> and (when declared) <c>styles.xml</c> are ever read.
/// </remarks>
internal sealed class OpenDocumentSpreadsheetExtractor : ISessionAttachmentDocumentExtractor
{
    /// <summary>The most physical cell elements (covered or not) processed in one package.</summary>
    internal const int MaximumCellElements = 1_048_576;

    /// <summary>The most column elements read for one sheet.</summary>
    internal const int MaximumColumnElements = 16_384;

    public ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OpenDocumentPackageContent content = OpenDocumentPackageContent.Load(
            bytes,
            SessionAttachmentComprehensionPolicy.OpenDocumentSpreadsheetMimeType,
            "spreadsheet",
            cancellationToken);

        using BoundedXmlReader xml = content.OpenBody("spreadsheet", cancellationToken);

        new OpenDocumentSpreadsheetWalker(xml, content.Styles, output).Walk();

        return ValueTask.CompletedTask;
    }
}
