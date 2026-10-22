using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>Extractor slot for OpenDocument spreadsheet packages (<c>.ods</c>).</summary>
internal sealed class OpenDocumentSpreadsheetExtractor : ISessionAttachmentDocumentExtractor
{
    public ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        // The reviewed extractor for this format lands in a later stage.
        throw new NotSupportedException();
    }
}
