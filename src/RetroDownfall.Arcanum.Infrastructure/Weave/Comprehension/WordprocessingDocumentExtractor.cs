using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>Extractor slot for Word processing packages (<c>.docx</c> and <c>.docm</c>).</summary>
internal sealed class WordprocessingDocumentExtractor : ISessionAttachmentDocumentExtractor
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
