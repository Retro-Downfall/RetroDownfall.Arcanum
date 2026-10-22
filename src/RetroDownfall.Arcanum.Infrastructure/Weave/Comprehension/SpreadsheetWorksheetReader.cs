using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Reads one worksheet part and writes its visible cells to the projection. Formulas are never read: only the cached
/// value of a cell is projected.
/// </summary>
internal sealed class SpreadsheetWorksheetReader(
    SpreadsheetProjection projection,
    SpreadsheetSharedStrings sharedStrings,
    SpreadsheetLimits limits,
    CancellationToken cancellationToken)
{
    private const int MaximumBooleanTextCharacters = 8;

    private const int MaximumIndexTextCharacters = 16;

    private readonly SpreadsheetHiddenColumns _hiddenColumns = new();

    private long _cells;

    private int _previousColumn;

    /// <summary>
    /// Reads <paramref name="part"/> as the sheet <paramref name="name"/>. The part is read twice: the first pass collects
    /// the hidden columns wherever their <c>cols</c> elements sit (and proves the part well-formed), the second projects
    /// the cells. Returns false when the sink is full.
    /// </summary>
    public bool Read(ReadOnlyMemory<byte> part, string name)
    {
        ReadHiddenColumns(part);

        using BoundedXmlReader xml = BoundedXmlReader.Create(part, cancellationToken);

        SpreadsheetXml.EnterRoot(xml, "worksheet");

        projection.BeginSheet(name);

        if (!SpreadsheetXml.EnterChildren(xml, out int depth))
        {
            return true;
        }

        while (SpreadsheetXml.NextChildElement(xml, depth))
        {
            if (SpreadsheetXml.IsMain(xml.Reader, "sheetData"))
            {
                if (!ReadSheetData(xml))
                {
                    return false;
                }
            }
            else
            {
                xml.SkipElement();
            }
        }

        return true;
    }

    private void ReadHiddenColumns(ReadOnlyMemory<byte> part)
    {
        _hiddenColumns.Clear();

        using BoundedXmlReader xml = BoundedXmlReader.Create(part, cancellationToken);

        SpreadsheetXml.EnterRoot(xml, "worksheet");

        int columnElements = 0;

        if (SpreadsheetXml.EnterChildren(xml, out int depth))
        {
            while (SpreadsheetXml.NextChildElement(xml, depth))
            {
                if (SpreadsheetXml.IsMain(xml.Reader, "cols"))
                {
                    ReadColumns(xml, ref columnElements);
                }
                else
                {
                    xml.SkipElement();
                }
            }
        }

        _hiddenColumns.Seal();
    }

    private void ReadColumns(BoundedXmlReader xml, ref int columnElements)
    {
        if (!SpreadsheetXml.EnterChildren(xml, out int depth))
        {
            return;
        }

        while (SpreadsheetXml.NextChildElement(xml, depth))
        {
            XmlReader reader = xml.Reader;

            if (!SpreadsheetXml.IsMain(reader, "col"))
            {
                xml.SkipElement();

                continue;
            }

            if (++columnElements > limits.ColumnElements)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            if (SpreadsheetXml.IsTrue(SpreadsheetXml.Attribute(reader, "hidden"))
                && SpreadsheetXml.TryParseDigits(SpreadsheetXml.Attribute(reader, "min"), out long first)
                && SpreadsheetXml.TryParseDigits(SpreadsheetXml.Attribute(reader, "max"), out long last))
            {
                first = Math.Max(first, 1);

                last = Math.Min(last, SpreadsheetLimits.MaximumColumn);

                if (first <= last)
                {
                    _hiddenColumns.Hide((int)first, (int)last);
                }
            }

            xml.SkipElement();
        }
    }

    private bool ReadSheetData(BoundedXmlReader xml)
    {
        if (!SpreadsheetXml.EnterChildren(xml, out int depth))
        {
            return true;
        }

        while (SpreadsheetXml.NextChildElement(xml, depth))
        {
            if (SpreadsheetXml.IsMain(xml.Reader, "row"))
            {
                if (!ReadRow(xml))
                {
                    return false;
                }
            }
            else
            {
                xml.SkipElement();
            }
        }

        return true;
    }

    private bool ReadRow(BoundedXmlReader xml)
    {
        if (SpreadsheetXml.IsTrue(SpreadsheetXml.Attribute(xml.Reader, "hidden")))
        {
            xml.SkipElement();

            return true;
        }

        projection.BeginRow();

        _previousColumn = 0;

        if (SpreadsheetXml.EnterChildren(xml, out int depth))
        {
            while (SpreadsheetXml.NextChildElement(xml, depth))
            {
                if (SpreadsheetXml.IsMain(xml.Reader, "c"))
                {
                    if (!ReadCell(xml))
                    {
                        return false;
                    }
                }
                else
                {
                    xml.SkipElement();
                }
            }
        }

        return projection.EndRow();
    }

    private bool ReadCell(BoundedXmlReader xml)
    {
        string? type = SpreadsheetXml.Attribute(xml.Reader, "t");

        int column = ParseColumn(SpreadsheetXml.Attribute(xml.Reader, "r"));

        if (column == 0)
        {
            column = _previousColumn + 1;
        }

        _previousColumn = column;

        if (++_cells > limits.Cells)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }

        if (_hiddenColumns.IsHidden(column))
        {
            xml.SkipElement();

            return true;
        }

        projection.BeginCell();

        if (!SpreadsheetXml.EnterChildren(xml, out int depth))
        {
            return true;
        }

        bool valueRead = false;

        bool inline = type == "inlineStr";

        while (SpreadsheetXml.NextChildElement(xml, depth))
        {
            XmlReader reader = xml.Reader;

            if (!valueRead && inline && SpreadsheetXml.IsMain(reader, "is"))
            {
                valueRead = true;

                if (!SpreadsheetXml.ReadRichText(xml, projection))
                {
                    return false;
                }
            }
            else if (!valueRead && !inline && SpreadsheetXml.IsMain(reader, "v"))
            {
                valueRead = true;

                if (!ReadValue(xml, type))
                {
                    return false;
                }
            }
            else
            {
                xml.SkipElement();
            }
        }

        return true;
    }

    private bool ReadValue(BoundedXmlReader xml, string? type) => type switch
    {
        "s" => ReadSharedStringValue(xml),
        "b" => ReadBooleanValue(xml),
        _ => SpreadsheetXml.ReadText(xml, projection),
    };

    private bool ReadBooleanValue(BoundedXmlReader xml) =>
        SpreadsheetXml.ReadBoundedText(xml, MaximumBooleanTextCharacters) switch
        {
            "1" or "true" => projection.Append("TRUE"),
            "0" or "false" => projection.Append("FALSE"),
            _ => true,
        };

    private bool ReadSharedStringValue(BoundedXmlReader xml)
    {
        if (!SpreadsheetXml.TryParseIndex(SpreadsheetXml.ReadBoundedText(xml, MaximumIndexTextCharacters), out int index))
        {
            return true;
        }

        if (sharedStrings.TryGet(index, out string text))
        {
            return projection.Append(text);
        }

        if (sharedStrings.Overflowed)
        {
            // The reference is past what was retained: say so instead of silently dropping the cell, and stop.
            projection.MarkTruncated();

            return false;
        }

        return true;
    }

    /// <summary>
    /// The column number (1..16384) of an A1-style cell reference such as <c>C5</c>, or 0 when the reference is absent
    /// or malformed: ASCII letters, in either case, naming a column no higher than XFD, then optional digits and nothing else.
    /// </summary>
    private static int ParseColumn(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return 0;
        }

        int column = 0;

        int index = 0;

        while (index < reference.Length && reference[index] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
        {
            column = (column * 26) + (char.ToUpperInvariant(reference[index]) - 'A' + 1);

            if (column > SpreadsheetLimits.MaximumColumn)
            {
                return 0;
            }

            index++;
        }

        for (; index < reference.Length; index++)
        {
            if (reference[index] is < '0' or > '9')
            {
                return 0;
            }
        }

        return column;
    }
}
