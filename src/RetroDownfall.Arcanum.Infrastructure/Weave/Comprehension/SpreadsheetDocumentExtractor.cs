using System.Xml;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Extracts the visible text of an Excel workbook package (<c>.xlsx</c> and <c>.xlsm</c>) as a tab-separated projection of
/// its visible sheets. Parts are reached only through relationships from the package root; hidden content (a workbook
/// whose every window is hidden, hidden sheets, rows and columns, and cells merged away), formulas, comments, drawings,
/// macros and external targets are never read. Each worksheet part is read for one sheet only.
/// </summary>
internal sealed class SpreadsheetDocumentExtractor : ISessionAttachmentDocumentExtractor
{
    private readonly SpreadsheetLimits _limits;

    public SpreadsheetDocumentExtractor()
        : this(SpreadsheetLimits.Default)
    {
    }

    internal SpreadsheetDocumentExtractor(SpreadsheetLimits limits) => _limits = limits;

    public ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        BoundedZipPackage package = BoundedZipPackage.Open(bytes, cancellationToken);

        string workbookEntry = ResolveWorkbookEntry(package, cancellationToken);

        ReadOnlyMemory<byte> workbookPart = package.ReadEntry(workbookEntry, cancellationToken);

        using BoundedXmlReader workbook = BoundedXmlReader.Create(workbookPart, cancellationToken);

        SpreadsheetXml.EnterRoot(workbook, "workbook");

        if (IsHiddenFromEveryWindow(workbookPart, cancellationToken))
        {
            // Excel opens it with no window at all: nothing of the workbook is on a reader's screen.
            return ValueTask.CompletedTask;
        }

        IReadOnlyList<OpcRelationship> workbookRelationships = OpcPackageRelationships.Read(package, workbookEntry, cancellationToken);

        Dictionary<string, OpcRelationship> relationships = [];

        foreach (OpcRelationship relationship in workbookRelationships)
        {
            relationships.Add(relationship.Id, relationship);
        }

        SpreadsheetSharedStrings sharedStrings = ReadSharedStrings(package, workbookRelationships, _limits, cancellationToken);

        SpreadsheetProjection projection = new(output);

        SpreadsheetWorksheetReader worksheets = new(projection, sharedStrings, _limits, cancellationToken);

        if (!SpreadsheetChildren.Enter(workbook, out SpreadsheetChildren children))
        {
            return ValueTask.CompletedTask;
        }

        // One running count for the whole workbook, so a repeated (schema-invalid) <sheets> element cannot restart the bound.
        int sheetsSeen = 0;

        // Every worksheet part read so far, by entry name (names match without regard to case, as in the package). A part is
        // read for one sheet only: a file whose sheets share one would make the extractor inflate and parse it again for each.
        HashSet<string> worksheetEntries = new(StringComparer.OrdinalIgnoreCase);

        while (children.MoveNext())
        {
            if (!SpreadsheetXml.IsMain(workbook.Reader, "sheets"))
            {
                workbook.SkipElement();

                continue;
            }

            if (!ReadSheets(workbook, package, relationships, worksheets, _limits, worksheetEntries, ref sheetsSeen, cancellationToken))
            {
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// True when the workbook declares at least one window (<c>bookViews/workbookView</c>, wherever <c>bookViews</c> sits) and
    /// every one of them has <c>visibility</c> <c>hidden</c> or <c>veryHidden</c>. A workbook with no window declared, or with
    /// a visible one, is shown.
    /// </summary>
    private static bool IsHiddenFromEveryWindow(ReadOnlyMemory<byte> workbookPart, CancellationToken cancellationToken)
    {
        using BoundedXmlReader xml = BoundedXmlReader.Create(workbookPart, cancellationToken);

        SpreadsheetXml.EnterRoot(xml, "workbook");

        if (!SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            return false;
        }

        bool anyWindow = false;

        while (children.MoveNext())
        {
            if (!SpreadsheetXml.IsMain(xml.Reader, "bookViews"))
            {
                xml.SkipElement();

                continue;
            }

            if (!SpreadsheetChildren.Enter(xml, out SpreadsheetChildren views))
            {
                continue;
            }

            while (views.MoveNext())
            {
                XmlReader reader = xml.Reader;

                if (!SpreadsheetXml.IsMain(reader, "workbookView"))
                {
                    xml.SkipElement();

                    continue;
                }

                string? visibility = SpreadsheetXml.Attribute(reader, "visibility");

                if (!string.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(visibility, "veryHidden", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                anyWindow = true;

                xml.SkipElement();
            }
        }

        return anyWindow;
    }

    private static SpreadsheetSharedStrings ReadSharedStrings(
        BoundedZipPackage package,
        IReadOnlyList<OpcRelationship> workbookRelationships,
        SpreadsheetLimits limits,
        CancellationToken cancellationToken)
    {
        foreach (OpcRelationship relationship in workbookRelationships)
        {
            if (!OpcPackageRelationships.IsOfficeDocumentType(relationship.Type, "sharedStrings"))
            {
                continue;
            }

            string entry = OpcPackageRelationships.ResolveEntry(package, relationship.TargetEntryName)
                ?? throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);

            return SpreadsheetSharedStrings.Read(package.ReadEntry(entry, cancellationToken), limits, cancellationToken);
        }

        return SpreadsheetSharedStrings.Empty;
    }

    private static string ResolveWorkbookEntry(BoundedZipPackage package, CancellationToken cancellationToken)
    {
        foreach (OpcRelationship relationship in OpcPackageRelationships.Read(package, string.Empty, cancellationToken))
        {
            if (!OpcPackageRelationships.IsOfficeDocumentType(relationship.Type, "officeDocument"))
            {
                continue;
            }

            return OpcPackageRelationships.ResolveEntry(package, relationship.TargetEntryName)
                ?? throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
        }

        throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
    }

    private static bool ReadSheets(
        BoundedXmlReader workbook,
        BoundedZipPackage package,
        Dictionary<string, OpcRelationship> relationships,
        SpreadsheetWorksheetReader worksheets,
        SpreadsheetLimits limits,
        HashSet<string> worksheetEntries,
        ref int sheetsSeen,
        CancellationToken cancellationToken)
    {
        if (!SpreadsheetChildren.Enter(workbook, out SpreadsheetChildren children))
        {
            return true;
        }

        while (children.MoveNext())
        {
            XmlReader reader = workbook.Reader;

            if (!SpreadsheetXml.IsMain(reader, "sheet"))
            {
                workbook.SkipElement();

                continue;
            }

            if (++sheetsSeen > limits.Sheets)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            string? state = SpreadsheetXml.Attribute(reader, "state");

            if (!string.IsNullOrEmpty(state) && !string.Equals(state, "visible", StringComparison.OrdinalIgnoreCase))
            {
                // hidden, veryHidden, and any state this reader does not know: not what a reader of the workbook sees.
                // Its name and relationship are never even read.
                workbook.SkipElement();

                continue;
            }

            string name = SpreadsheetXml.Attribute(reader, "name") ?? string.Empty;

            string? relationshipId = SpreadsheetXml.RelationshipId(reader);

            workbook.SkipElement();

            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out OpcRelationship? relationship))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
            }

            if (!OpcPackageRelationships.IsOfficeDocumentType(relationship.Type, "worksheet"))
            {
                continue;
            }

            string entry = OpcPackageRelationships.ResolveEntry(package, relationship.TargetEntryName)
                ?? throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);

            if (!worksheetEntries.Add(entry))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            ReadOnlyMemory<byte> part = package.ReadEntry(entry, cancellationToken);

            if (!worksheets.Read(part, name))
            {
                return false;
            }
        }

        return true;
    }
}
