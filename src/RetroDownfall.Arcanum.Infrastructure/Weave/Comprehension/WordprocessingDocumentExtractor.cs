using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Extractor for Word processing packages (<c>.docx</c> and <c>.docm</c>): bytes in, bounded visible text out.
/// </summary>
/// <remarks>
/// The main part is found through the package-root <c>officeDocument</c> relationship and the declared format is judged
/// by its root element; <c>[Content_Types].xml</c> is not consulted. Only parts reached through the main part's
/// relationships are read, and only the styles, footnotes and endnotes parts at that: comments, headers, footers,
/// macros, embeddings and every unreferenced entry are never opened or inflated. External relationships are never
/// followed. The work is synchronous and pure.
/// </remarks>
internal sealed class WordprocessingDocumentExtractor : ISessionAttachmentDocumentExtractor
{
    public ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        BoundedZipPackage package = BoundedZipPackage.Open(bytes, cancellationToken);

        IReadOnlyList<OpcRelationship> rootRelationships = OpcPackageRelationships.Read(package, string.Empty, cancellationToken);

        string mainEntry = FindRelatedPart(package, rootRelationships, "officeDocument")
            ?? throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);

        using BoundedXmlReader main = BoundedXmlReader.Create(package.ReadEntry(mainEntry, cancellationToken), cancellationToken);

        WordprocessingXml.MoveToRoot(main, "document");

        IReadOnlyList<OpcRelationship> relationships = OpcPackageRelationships.Read(package, mainEntry, cancellationToken);

        WordprocessingStyleSheet styles = ReadStyles(package, relationships, cancellationToken);

        WordprocessingPartWalker body = new(main, styles, output);

        body.Walk();

        if (body.Stopped)
        {
            return ValueTask.CompletedTask;
        }

        if (WalkNotes(package, relationships, "footnotes", "footnote", styles, output, cancellationToken))
        {
            WalkNotes(package, relationships, "endnotes", "endnote", styles, output, cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Projects one notes part, when the document relates one. Returns false once the sink has refused text, so the caller
    /// reads no further part.
    /// </summary>
    private static bool WalkNotes(
        BoundedZipPackage package,
        IReadOnlyList<OpcRelationship> relationships,
        string partName,
        string noteName,
        WordprocessingStyleSheet styles,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? entry = FindRelatedPart(package, relationships, partName);

        if (entry is null)
        {
            return true;
        }

        using BoundedXmlReader xml = BoundedXmlReader.Create(package.ReadEntry(entry, cancellationToken), cancellationToken);

        WordprocessingXml.MoveToRoot(xml, partName);

        WordprocessingPartWalker notes = new(xml, styles, output);

        notes.WalkNotes(noteName);

        return !notes.Stopped;
    }

    private static WordprocessingStyleSheet ReadStyles(
        BoundedZipPackage package,
        IReadOnlyList<OpcRelationship> relationships,
        CancellationToken cancellationToken)
    {
        string? stylesEntry = FindRelatedPart(package, relationships, "styles");

        return stylesEntry is null
            ? WordprocessingStyleSheet.Empty
            : WordprocessingStyleSheet.Read(package.ReadEntry(stylesEntry, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The entry of the one internal relationship of this officeDocument type, or null when there is none. A relationship
    /// that names an absent part is a broken package, and two of them are ambiguous (which part a consumer would use is
    /// not defined), so both are refused.
    /// </summary>
    private static string? FindRelatedPart(BoundedZipPackage package, IReadOnlyList<OpcRelationship> relationships, string localName)
    {
        OpcRelationship? found = null;

        foreach (OpcRelationship relationship in relationships)
        {
            if (!OpcPackageRelationships.IsOfficeDocumentType(relationship.Type, localName))
            {
                continue;
            }

            if (found is not null)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            found = relationship;
        }

        return found is null
            ? null
            : OpcPackageRelationships.ResolveEntry(package, found.TargetEntryName)
                ?? throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
    }
}
