using System.Xml;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Extracts the visible text of an Excel workbook package (<c>.xlsx</c> and <c>.xlsm</c>) as a tab-separated projection of
/// its visible sheets. Parts are reached only through relationships from the package root; hidden content, formulas,
/// comments, drawings, macros and external targets are never read.
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

        IReadOnlyList<OpcRelationship> workbookRelationships = OpcPackageRelationships.Read(package, workbookEntry, cancellationToken);

        Dictionary<string, OpcRelationship> relationships = [];

        foreach (OpcRelationship relationship in workbookRelationships)
        {
            relationships.Add(relationship.Id, relationship);
        }

        SpreadsheetSharedStrings sharedStrings = ReadSharedStrings(package, workbookRelationships, _limits, cancellationToken);

        SpreadsheetProjection projection = new(output);

        SpreadsheetWorksheetReader worksheets = new(projection, sharedStrings, _limits, cancellationToken);

        if (!SpreadsheetXml.EnterChildren(workbook, out int depth))
        {
            return ValueTask.CompletedTask;
        }

        // One running count for the whole workbook, so a repeated (schema-invalid) <sheets> element cannot restart the bound.
        int sheetsSeen = 0;

        while (SpreadsheetXml.NextChildElement(workbook, depth))
        {
            if (!SpreadsheetXml.IsMain(workbook.Reader, "sheets"))
            {
                workbook.SkipElement();

                continue;
            }

            if (!ReadSheets(workbook, package, relationships, worksheets, _limits, ref sheetsSeen, cancellationToken))
            {
                break;
            }
        }

        return ValueTask.CompletedTask;
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
        ref int sheetsSeen,
        CancellationToken cancellationToken)
    {
        if (!SpreadsheetXml.EnterChildren(workbook, out int depth))
        {
            return true;
        }

        while (SpreadsheetXml.NextChildElement(workbook, depth))
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

            ReadOnlyMemory<byte> part = package.ReadEntry(entry, cancellationToken);

            if (!worksheets.Read(part, name))
            {
                return false;
            }
        }

        return true;
    }
}
