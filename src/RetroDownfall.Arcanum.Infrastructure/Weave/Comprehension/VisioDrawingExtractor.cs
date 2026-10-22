using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>Extractor slot for Visio drawing packages (<c>.vsdx</c> and <c>.vsdm</c>).</summary>
internal sealed class VisioDrawingExtractor : ISessionAttachmentDocumentExtractor
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
