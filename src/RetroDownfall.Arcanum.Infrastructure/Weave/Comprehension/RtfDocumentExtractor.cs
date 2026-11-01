using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>Pure bounded RTF text projection. Validates the complete input before any sink output.</summary>
internal sealed class RtfDocumentExtractor : ISessionAttachmentDocumentExtractor
{
    internal const int MaximumInputBytes = 16_777_216;

    internal const int MaximumGroupDepth = 256;

    internal const int MaximumControls = 1_000_000;

    public ValueTask ExtractAsync(ReadOnlyMemory<byte> bytes, ISessionAttachmentDocumentTextSink output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (bytes.Length > MaximumInputBytes || !bytes.Span.StartsWith("{\\rtf1"u8))
        {
            throw new InvalidDataException("Invalid RTF document.");
        }

        // The first pass prevents malformed tails being hidden by the retained output ceiling. Both passes use
        // the same iterative grammar and fixed bounds; neither builds a document tree or retains document text.
        new RtfTextScanner(bytes.Span, null, cancellationToken).Run();

        new RtfTextScanner(bytes.Span, output, cancellationToken).Run();

        return ValueTask.CompletedTask;
    }
}
