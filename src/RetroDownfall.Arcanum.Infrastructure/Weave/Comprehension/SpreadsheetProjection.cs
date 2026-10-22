using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Writes the spreadsheet projection to the bounded sink: <c>Sheet: &lt;name&gt;</c> once a sheet produces its first
/// non-empty row, then each non-empty row as its non-empty cell values joined by a tab and ended by a newline. Cell text
/// arrives in chunks and is streamed through, so no cell is ever materialized here. Every write that the sink refuses
/// returns false so the caller stops reading.
/// </summary>
internal sealed class SpreadsheetProjection(ISessionAttachmentDocumentTextSink output) : ISpreadsheetTextTarget
{
    private string _sheetName = string.Empty;

    private bool _headerWritten;

    private bool _rowHasCells;

    private bool _cellStarted;

    public void BeginSheet(string name)
    {
        _sheetName = name;

        _headerWritten = false;

        _rowHasCells = false;

        _cellStarted = false;
    }

    public void BeginRow()
    {
        _rowHasCells = false;

        _cellStarted = false;
    }

    public void BeginCell() => _cellStarted = false;

    /// <summary>Appends a chunk of the current cell's text. Empty chunks are ignored, so an empty cell leaves no trace.</summary>
    public bool Append(string text)
    {
        if (text.Length == 0)
        {
            return true;
        }

        if (!_headerWritten)
        {
            if (!output.TryAppend("Sheet: ") || !output.TryAppend(_sheetName) || !output.TryAppend("\n"))
            {
                return false;
            }

            _headerWritten = true;
        }

        if (!_cellStarted)
        {
            _cellStarted = true;

            if (_rowHasCells && !output.TryAppend("\t"))
            {
                return false;
            }

            _rowHasCells = true;
        }

        return output.TryAppend(text);
    }

    /// <summary>Reports that text was withheld because it could not be retained; the sink refuses everything after this.</summary>
    public void MarkTruncated() => output.MarkTruncated();

    /// <summary>Ends the current row; a row with no non-empty cell writes nothing.</summary>
    public bool EndRow() => !_rowHasCells || output.TryAppend("\n");
}

/// <summary>
/// A bounded text accumulator. Text that would exceed <c>capacity</c> is not stored and sets <see cref="Overflowed"/>;
/// the append then also reports false (stop reading) when <c>stopWhenFull</c>, otherwise the read carries on to the end of
/// the element so the caller can keep using the same reader.
/// </summary>
internal sealed class SpreadsheetTextBuffer(int capacity, bool stopWhenFull) : ISpreadsheetTextTarget
{
    private readonly StringBuilder _text = new();

    public bool Overflowed { get; private set; }

    public bool Append(string text)
    {
        if (Overflowed || text.Length > capacity - _text.Length)
        {
            Overflowed = true;

            return !stopWhenFull;
        }

        _text.Append(text);

        return true;
    }

    public override string ToString() => _text.ToString();
}
