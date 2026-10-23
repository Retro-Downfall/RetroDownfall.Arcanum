using System.Xml;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Walks the <c>office:spreadsheet</c> element of an OpenDocument package and projects each visible sheet as
/// <c>Sheet: &lt;name&gt;</c> followed by one line per visible row of visible, non-empty cells joined by tabs.
/// </summary>
/// <remarks>
/// <para>
/// A repeat count is arithmetic on an index, never a loop: a cell repeated across 16,384 columns inside a row
/// repeated 1,048,576 times costs one cell and one row of work, and its text is emitted once. Hidden columns are kept
/// as a short sorted list of ranges (saturating <see cref="long"/> arithmetic, clamped to the 16,384 real columns), so
/// asking whether any column a repeated cell covers is visible is a binary search, not a scan.
/// </para>
/// <para>
/// A cell with a numeric, date, time or boolean value shows that value, so it projects its raw value attribute (see
/// <see cref="OpenDocumentCellValue"/>) and never its cached paragraph; any other cell's text is its paragraphs joined
/// by single spaces. A cell's text and a sheet's name carry no control character and no U+2028 or U+2029: each folds to
/// a space, so a row stays one line and a name cannot forge a header. Formulas are attributes and are never read. A
/// covered (merged-away) cell contributes no text but still occupies its column index.
/// </para>
/// <para>
/// Rows are counted the way columns are: a row positioned at or past <see cref="MaximumRows"/> is on no sheet and is
/// hidden, and hidden rows and the rows of collapsed groups still occupy their indexes.
/// </para>
/// </remarks>
internal sealed class OpenDocumentSpreadsheetWalker(
    BoundedXmlReader xml,
    OpenDocumentStyleTable styles,
    ISessionAttachmentDocumentTextSink output)
{
    /// <summary>The widest sheet: column indexes at or past this are not columns, so cells there are hidden.</summary>
    internal const int MaximumColumns = 16_384;

    /// <summary>The tallest sheet: row indexes at or past this are not rows, so rows there are hidden.</summary>
    internal const long MaximumRows = 1_048_576;

    private readonly XmlReader _reader = xml.Reader;

    private readonly OpenDocumentContentWalker _content = new(xml, styles, output);

    private readonly HiddenColumns _hiddenColumns = new();

    private long _cellElements;

    private int _columnElements;

    private long _columnIndex;

    private long _rowIndex;

    private bool _rowsSeen;

    private bool _headerWritten;

    private bool _rowHasCell;

    private string _sheetName = string.Empty;

    private bool Stopped => _content.Stopped;

    /// <summary>Projects every sheet under the <c>office:spreadsheet</c> element the reader is on.</summary>
    public void Walk()
    {
        if (_reader.IsEmptyElement)
        {
            xml.Read();

            return;
        }

        xml.Read();

        while (!Stopped && !_reader.EOF && _reader.NodeType != XmlNodeType.EndElement)
        {
            if (_reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();

                continue;
            }

            if (IsTable("table"))
            {
                ProcessSheet();
            }
            else
            {
                // Calculation settings, validations, tracked changes, named ranges and the like are never projected.
                xml.SkipElement();
            }
        }

        _content.EmitNotes();
    }

    private void ProcessSheet()
    {
        string? styleName = _reader.GetAttribute("style-name", OpenDocumentNames.Table);

        if (styles.IsHidden(OpenDocumentStyleTable.Switch.Table, OpenDocumentStyleTable.TableFamily, styleName))
        {
            xml.SkipElement();

            return;
        }

        _sheetName = Sanitize(_reader.GetAttribute("name", OpenDocumentNames.Table) ?? string.Empty);

        _hiddenColumns.Clear();

        _columnElements = 0;

        _columnIndex = 0;

        _rowIndex = 0;

        _rowsSeen = false;

        _headerWritten = false;

        ProcessContainer(columnsHidden: false);
    }

    /// <summary>Walks a sheet or one of its row and column groupings, then moves past it.</summary>
    private void ProcessContainer(bool columnsHidden)
    {
        if (_reader.IsEmptyElement)
        {
            xml.Read();

            return;
        }

        xml.Read();

        while (!Stopped && !_reader.EOF && _reader.NodeType != XmlNodeType.EndElement)
        {
            if (_reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();

                continue;
            }

            if (_reader.NamespaceURI != OpenDocumentNames.Table)
            {
                xml.SkipElement();

                continue;
            }

            switch (_reader.LocalName)
            {
                case "table-column":
                    ProcessColumn(columnsHidden);

                    break;

                case "table-column-group":
                    ProcessContainer(columnsHidden || IsCollapsedGroup());

                    break;

                case "table-columns" or "table-header-columns" or "table-rows" or "table-header-rows":
                    ProcessContainer(columnsHidden);

                    break;

                case "table-row-group":
                    if (IsCollapsedGroup())
                    {
                        CountCollapsedRows();
                    }
                    else
                    {
                        ProcessContainer(columnsHidden);
                    }

                    break;

                case "table-row":
                    ProcessRow();

                    break;

                case "shapes":
                    ProcessShapes();

                    break;

                default:
                    // Titles, descriptions, scenarios, sources and forms are not rendered cell content.
                    xml.SkipElement();

                    break;
            }
        }

        xml.Read();
    }

    private void ProcessColumn(bool groupHidden)
    {
        if (_rowsSeen)
        {
            // A column defined after a row could hide cells that were already read, and a consumer may honour it.
            throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
        }

        if (++_columnElements > OpenDocumentSpreadsheetExtractor.MaximumColumnElements)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }

        long repeat = OpenDocumentNames.ParseCount(_reader.GetAttribute("number-columns-repeated", OpenDocumentNames.Table), OpenDocumentNames.MaximumRepeat);

        if (groupHidden || _reader.GetAttribute("visibility", OpenDocumentNames.Table) is "collapse" or "filter")
        {
            _hiddenColumns.Add(_columnIndex, _columnIndex + repeat);
        }

        _columnIndex += repeat;

        xml.SkipElement();
    }

    /// <summary>
    /// Walks a collapsed row group, then moves past it. Its rows are never shown, but they still occupy row indexes, so
    /// the rows after the group are placed exactly where a consumer places them.
    /// </summary>
    private void CountCollapsedRows()
    {
        if (_reader.IsEmptyElement)
        {
            xml.Read();

            return;
        }

        xml.Read();

        while (!Stopped && !_reader.EOF && _reader.NodeType != XmlNodeType.EndElement)
        {
            if (_reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();

                continue;
            }

            if (IsTable("table-row"))
            {
                EnterRow();

                xml.SkipElement();
            }
            else if (IsTable("table-row-group") || IsTable("table-rows") || IsTable("table-header-rows"))
            {
                CountCollapsedRows();
            }
            else
            {
                xml.SkipElement();
            }
        }

        xml.Read();
    }

    /// <summary>
    /// Counts the row the reader is on, with its repeat count, as occupying row indexes (saturating at the last row)
    /// and returns true when its first row lies on the sheet.
    /// </summary>
    private bool EnterRow()
    {
        long repeat = OpenDocumentNames.ParseCount(_reader.GetAttribute("number-rows-repeated", OpenDocumentNames.Table), OpenDocumentNames.MaximumRepeat);

        bool onSheet = _rowIndex < MaximumRows;

        _rowIndex = Math.Min(_rowIndex + repeat, MaximumRows);

        return onSheet;
    }

    private void ProcessRow()
    {
        _rowsSeen = true;

        bool onSheet = EnterRow();

        if (!onSheet || _reader.GetAttribute("visibility", OpenDocumentNames.Table) is "collapse" or "filter")
        {
            xml.SkipElement();

            return;
        }

        _rowHasCell = false;

        long column = 0;

        if (_reader.IsEmptyElement)
        {
            xml.Read();

            return;
        }

        xml.Read();

        while (!Stopped && !_reader.EOF && _reader.NodeType != XmlNodeType.EndElement)
        {
            if (_reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();

                continue;
            }

            if (IsTable("table-cell"))
            {
                column = ProcessCell(column);
            }
            else if (IsTable("covered-table-cell"))
            {
                column = SkipCoveredCell(column);
            }
            else
            {
                xml.SkipElement();
            }
        }

        xml.Read();

        if (_rowHasCell)
        {
            _content.Emit("\n");
        }
    }

    private long ProcessCell(long column)
    {
        CountCell();

        long repeat = OpenDocumentNames.ParseCount(_reader.GetAttribute("number-columns-repeated", OpenDocumentNames.Table), OpenDocumentNames.MaximumRepeat);

        long end = column + repeat;

        string? valueType = _reader.GetAttribute("value-type", OpenDocumentNames.Office);

        bool valueCell = OpenDocumentCellValue.IsValueType(valueType);

        string? value = valueCell ? OpenDocumentCellValue.Read(_reader, valueType!) : null;

        string? stringValue = valueType == "string" ? _reader.GetAttribute("string-value", OpenDocumentNames.Office) : null;

        if (_reader.IsEmptyElement && value is null && stringValue is null)
        {
            xml.Read();

            return end;
        }

        string? cellStyle = _reader.GetAttribute("style-name", OpenDocumentNames.Table);

        if (!_hiddenColumns.AnyVisible(column, end)
            || (cellStyle is not null && styles.IsHidden(OpenDocumentStyleTable.Switch.Text, OpenDocumentStyleTable.TableCellFamily, cellStyle)))
        {
            xml.SkipElement();

            return end;
        }

        string text = valueCell ? _content.CaptureValueCell(value) : _content.Capture(cell: true, fallback: stringValue);

        if (text.Length > 0)
        {
            WriteHeader();

            if (_rowHasCell)
            {
                _content.Emit("\t");
            }

            _rowHasCell = true;

            _content.Emit(text);
        }

        if (_content.CaptureTruncated)
        {
            _content.Truncate();
        }

        return end;
    }

    private long SkipCoveredCell(long column)
    {
        CountCell();

        long repeat = OpenDocumentNames.ParseCount(_reader.GetAttribute("number-columns-repeated", OpenDocumentNames.Table), OpenDocumentNames.MaximumRepeat);

        xml.SkipElement();

        return column + repeat;
    }

    private void CountCell()
    {
        if (++_cellElements > OpenDocumentSpreadsheetExtractor.MaximumCellElements)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }
    }

    private void ProcessShapes()
    {
        string text = _content.Capture(cell: false);

        if (text.Length > 0)
        {
            WriteHeader();

            _content.Emit(text);
        }

        if (_content.CaptureTruncated)
        {
            _content.Truncate();
        }
    }

    private void WriteHeader()
    {
        if (_headerWritten)
        {
            return;
        }

        _headerWritten = true;

        _content.Emit("Sheet: " + _sheetName + "\n");
    }

    private bool IsCollapsedGroup() => _reader.GetAttribute("display", OpenDocumentNames.Table) == "false";

    private bool IsTable(string localName) =>
        _reader.LocalName == localName && _reader.NamespaceURI == OpenDocumentNames.Table;

    /// <summary>
    /// Replaces control characters (C0 and C1) and the line and paragraph separators U+2028 and U+2029, which would let
    /// a sheet name forge a line of the projection, with spaces.
    /// </summary>
    private static string Sanitize(string name)
    {
        foreach (char current in name)
        {
            if (OpenDocumentContentWalker.IsCellLineBreak(current))
            {
                return string.Create(name.Length, name, static (span, source) =>
                {
                    for (int index = 0; index < source.Length; index++)
                    {
                        span[index] = OpenDocumentContentWalker.IsCellLineBreak(source[index]) ? ' ' : source[index];
                    }
                });
            }
        }

        return name;
    }

    /// <summary>Sorted, disjoint ranges of hidden columns clamped to the real columns, with prefix counts for binary search.</summary>
    private sealed class HiddenColumns
    {
        private readonly List<long> _starts = [];

        private readonly List<long> _ends = [];

        private readonly List<long> _before = [];

        private long _total;

        public void Clear()
        {
            _starts.Clear();

            _ends.Clear();

            _before.Clear();

            _total = 0;
        }

        /// <summary>Adds the hidden columns <c>[start, end)</c>. Calls arrive in increasing column order.</summary>
        public void Add(long start, long end)
        {
            start = Math.Min(start, MaximumColumns);

            end = Math.Min(end, MaximumColumns);

            if (end <= start)
            {
                return;
            }

            _starts.Add(start);

            _ends.Add(end);

            _before.Add(_total);

            _total += end - start;
        }

        /// <summary>True when at least one real, visible column lies in <c>[start, end)</c>.</summary>
        public bool AnyVisible(long start, long end)
        {
            end = Math.Min(end, MaximumColumns);

            if (start >= end)
            {
                return false;
            }

            return end - start > HiddenBefore(end) - HiddenBefore(start);
        }

        private long HiddenBefore(long column)
        {
            int low = 0;

            int high = _starts.Count - 1;

            int found = -1;

            while (low <= high)
            {
                int middle = low + ((high - low) / 2);

                if (_starts[middle] < column)
                {
                    found = middle;

                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found < 0 ? 0 : _before[found] + Math.Min(_ends[found], column) - _starts[found];
        }
    }
}
