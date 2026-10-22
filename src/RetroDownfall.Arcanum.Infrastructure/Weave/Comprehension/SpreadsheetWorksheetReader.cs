using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Reads one worksheet part and writes its visible cells to the projection: not those of a hidden row or column, and not
/// those merged away under another cell. A cell is placed by its row element and by its own reference alike; where the two
/// name different rows, a hidden row or a merged range at either hides it. Formulas are never read: only the cached value
/// of a cell is projected.
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

    private readonly SpreadsheetHiddenRows _hiddenRows = new();

    private readonly SpreadsheetMergedCells _mergedCells = new(limits);

    private long _cells;

    private int _previousColumn;

    private int _previousRow;

    private int _row;

    /// <summary>
    /// Reads <paramref name="part"/> as the sheet <paramref name="name"/>. The part, inflated once, is parsed twice: the
    /// first pass collects the hidden columns, the hidden rows and the merged ranges wherever their <c>cols</c>,
    /// <c>row</c> and <c>mergeCells</c> elements sit (the schema puts <c>mergeCells</c> after the cells it hides, and a cell
    /// can name a row listed later) and proves the part well-formed, the second projects the cells. Returns false when the
    /// sink is full.
    /// </summary>
    public bool Read(ReadOnlyMemory<byte> part, string name)
    {
        ReadHiddenColumnsHiddenRowsAndMergedRanges(part);

        _previousRow = 0;

        using BoundedXmlReader xml = BoundedXmlReader.Create(part, cancellationToken);

        SpreadsheetXml.EnterRoot(xml, "worksheet");

        projection.BeginSheet(name);

        if (!SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            return true;
        }

        while (children.MoveNext())
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

    private void ReadHiddenColumnsHiddenRowsAndMergedRanges(ReadOnlyMemory<byte> part)
    {
        _hiddenColumns.Clear();

        _hiddenRows.Clear();

        _mergedCells.Clear();

        _previousRow = 0;

        using BoundedXmlReader xml = BoundedXmlReader.Create(part, cancellationToken);

        SpreadsheetXml.EnterRoot(xml, "worksheet");

        int columnElements = 0;

        int mergeCellElements = 0;

        if (SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            while (children.MoveNext())
            {
                if (SpreadsheetXml.IsMain(xml.Reader, "cols"))
                {
                    ReadColumns(xml, ref columnElements);
                }
                else if (SpreadsheetXml.IsMain(xml.Reader, "mergeCells"))
                {
                    ReadMergeCells(xml, ref mergeCellElements);
                }
                else if (SpreadsheetXml.IsMain(xml.Reader, "sheetData"))
                {
                    ReadHiddenRows(xml);
                }
                else
                {
                    xml.SkipElement();
                }
            }
        }

        _hiddenColumns.Seal();

        _mergedCells.Seal();
    }

    /// <summary>
    /// Records the rows the <c>row</c> elements of a <c>sheetData</c> hide, numbering them as the second pass does and
    /// skipping their cells.
    /// </summary>
    private void ReadHiddenRows(BoundedXmlReader xml)
    {
        if (!SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            return;
        }

        while (children.MoveNext())
        {
            if (SpreadsheetXml.IsMain(xml.Reader, "row"))
            {
                int row = ResolveRowNumber(xml.Reader);

                if (SpreadsheetXml.IsTrue(SpreadsheetXml.Attribute(xml.Reader, "hidden")))
                {
                    _hiddenRows.Hide(row);
                }
            }

            xml.SkipElement();
        }
    }

    private void ReadMergeCells(BoundedXmlReader xml, ref int mergeCellElements)
    {
        if (!SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            return;
        }

        while (children.MoveNext())
        {
            XmlReader reader = xml.Reader;

            if (!SpreadsheetXml.IsMain(reader, "mergeCell"))
            {
                xml.SkipElement();

                continue;
            }

            if (++mergeCellElements > limits.MergedRanges)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            _mergedCells.Add(SpreadsheetXml.Attribute(reader, "ref"));

            xml.SkipElement();
        }
    }

    private void ReadColumns(BoundedXmlReader xml, ref int columnElements)
    {
        if (!SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            return;
        }

        while (children.MoveNext())
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
        if (!SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            return true;
        }

        while (children.MoveNext())
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
        _row = ResolveRowNumber(xml.Reader);

        if (SpreadsheetXml.IsTrue(SpreadsheetXml.Attribute(xml.Reader, "hidden")))
        {
            xml.SkipElement();

            return true;
        }

        projection.BeginRow();

        _previousColumn = 0;

        if (SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            while (children.MoveNext())
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

        int column = ParseCellReference(SpreadsheetXml.Attribute(xml.Reader, "r"), out int referenceRow);

        if (column == 0)
        {
            column = _previousColumn + 1;
        }

        _previousColumn = column;

        if (++_cells > limits.Cells)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }

        if (_hiddenColumns.IsHidden(column) || IsInHiddenRow(referenceRow) || IsMergedAway(referenceRow, column))
        {
            xml.SkipElement();

            return true;
        }

        projection.BeginCell();

        if (!SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            return true;
        }

        bool valueRead = false;

        bool inline = type == "inlineStr";

        while (children.MoveNext())
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
    /// The row number a <c>row</c> element places its cells on: its <c>r</c> attribute, or the row after the previous one
    /// when that is absent or not a row number. A number past the last row saturates one beyond it.
    /// </summary>
    private int ResolveRowNumber(XmlReader row)
    {
        if (!SpreadsheetXml.TryParseDigits(SpreadsheetXml.Attribute(row, "r"), out long number) || number == 0)
        {
            number = (long)_previousRow + 1;
        }

        _previousRow = (int)Math.Min(number, SpreadsheetLimits.MaximumRow + 1);

        return _previousRow;
    }

    /// <summary>
    /// True when a hidden row holds the cell. The row it sits on is the row element's, which another element of the same
    /// number may have hidden; a cell reference that names another row places it in that row as well, and the cell is hidden
    /// if either is hidden, since a reader cannot tell which one the file means.
    /// </summary>
    private bool IsInHiddenRow(int referenceRow) =>
        _hiddenRows.IsHidden(_row) || (referenceRow != 0 && referenceRow != _row && _hiddenRows.IsHidden(referenceRow));

    /// <summary>
    /// True when Excel hides the cell under a merge. The row it sits on is the row element's; a cell reference that names
    /// another row is placed by that row as well, and merged away if either places it inside a merged range, since a
    /// reader cannot tell which one the file means.
    /// </summary>
    private bool IsMergedAway(int referenceRow, int column) =>
        _mergedCells.IsMergedAway(_row, column) || (referenceRow != 0 && referenceRow != _row && _mergedCells.IsMergedAway(referenceRow, column));

    /// <summary>
    /// The column number (1..16384) of an A1-style cell reference such as <c>C5</c>, or 0 when the reference is absent
    /// or malformed: ASCII letters, in either case, naming a column no higher than XFD, then optional digits and nothing
    /// else. <paramref name="row"/> is the row the digits name (a number past the last row saturates one beyond it), or 0
    /// when there are no digits, the row is 0, or the column is 0.
    /// </summary>
    private static int ParseCellReference(string? reference, out int row)
    {
        row = 0;

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

        int number = 0;

        for (; index < reference.Length; index++)
        {
            if (reference[index] is < '0' or > '9')
            {
                return 0;
            }

            number = Math.Min((number * 10) + (reference[index] - '0'), SpreadsheetLimits.MaximumRow + 1);
        }

        row = column == 0 ? 0 : number;

        return column;
    }
}
