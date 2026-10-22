using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>Extracts the text a reader sees in an OpenDocument text package (<c>.odt</c>).</summary>
/// <remarks>
/// Hidden-content posture is plan decision D8; the projection is D9. Only <c>META-INF/manifest.xml</c>,
/// <c>content.xml</c> and (when declared) <c>styles.xml</c> are ever read.
/// </remarks>
internal sealed class OpenDocumentTextExtractor : ISessionAttachmentDocumentExtractor
{
    public ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OpenDocumentPackageContent content = OpenDocumentPackageContent.Load(
            bytes,
            SessionAttachmentComprehensionPolicy.OpenDocumentTextMimeType,
            "text",
            cancellationToken);

        using BoundedXmlReader xml = content.OpenBody("text", cancellationToken);

        new OpenDocumentContentWalker(xml, content.Styles, output).WalkElement();

        return ValueTask.CompletedTask;
    }
}
