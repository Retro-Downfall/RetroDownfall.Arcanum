namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The merged ranges of one worksheet, answering for a cell whether Excel hides it: Excel shows only the top-left cell of a
/// merged range, so every other cell of the range is merged away and its value is not text a reader sees.
/// </summary>
/// <remarks>
/// <para>
/// A cell is merged away when some range covers it without the cell being that range's top-left cell. Ranges may overlap
/// (a schema-invalid file); the count of ranges covering a cell, minus the count of ranges the cell anchors, decides.
/// </para>
/// <para>
/// A lookup is not a scan of the ranges. Each range contributes two marks, one where it starts and one after it ends,
/// kept sorted by row. A cursor over the marks keeps a binary indexed tree of how many ranges cover each column at the
/// current row, so a cell costs the marks the cursor has to cross to reach its row plus one tree query. Rows that ascend,
/// as every producer writes them, cross each mark once; rows that jump back and forth cross marks again, which
/// <see cref="SpreadsheetLimits.MergeEventMoves"/> bounds. Marks and anchors are packed in <c>long</c>s, so the whole
/// structure is a few arrays sized by the range bound.
/// </para>
/// </remarks>
internal sealed class SpreadsheetMergedCells(SpreadsheetLimits limits)
{
    private const int ColumnBits = 14;

    private const int MarkRowShift = (2 * ColumnBits) + 1;

    private const int MarkEndShift = 2 * ColumnBits;

    private const long ColumnMask = (1L << ColumnBits) - 1;

    private const int InitialCapacity = 16;

    private long[] _marks = [];

    private long[] _anchors = [];

    private int _markCount;

    private int _anchorCount;

    private int[]? _tree;

    private int _applied;

    private long _moves;

    /// <summary>
    /// Forgets the previous sheet's ranges and rewinds the cursor. The moves already made stay counted: the bound is on the
    /// whole extraction, not on each sheet.
    /// </summary>
    public void Clear()
    {
        _markCount = 0;

        _anchorCount = 0;

        if (_applied > 0)
        {
            Array.Clear(_tree!);
        }

        _applied = 0;
    }

    /// <summary>
    /// Adds the range named by a <c>mergeCell</c> <c>ref</c> such as <c>B2:D4</c>. A reference that is not two cell
    /// references joined by a colon names nothing and is ignored, as is a single cell and a range wholly beyond the
    /// sheet; corners may come in either order and a far corner is clamped to the sheet.
    /// </summary>
    public void Add(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        int colon = reference.IndexOf(':');

        if (colon < 0
            || reference.IndexOf(':', colon + 1) >= 0
            || !TryParseCorner(reference.AsSpan(0, colon), out int rowA, out int columnA)
            || !TryParseCorner(reference.AsSpan(colon + 1), out int rowB, out int columnB))
        {
            return;
        }

        int firstRow = Math.Min(rowA, rowB);

        int firstColumn = Math.Min(columnA, columnB);

        if (firstRow > SpreadsheetLimits.MaximumRow || firstColumn > SpreadsheetLimits.MaximumColumn)
        {
            return;
        }

        int lastRow = Math.Min(Math.Max(rowA, rowB), SpreadsheetLimits.MaximumRow);

        int lastColumn = Math.Min(Math.Max(columnA, columnB), SpreadsheetLimits.MaximumColumn);

        if (firstRow == lastRow && firstColumn == lastColumn)
        {
            return;
        }

        Append(ref _marks, ref _markCount, Mark(firstRow, isEnd: false, firstColumn, lastColumn));

        Append(ref _marks, ref _markCount, Mark(lastRow + 1, isEnd: true, firstColumn, lastColumn));

        Append(ref _anchors, ref _anchorCount, AnchorKey(firstRow, firstColumn));
    }

    /// <summary>Orders the ranges for lookup; call once after the last <see cref="Add"/> of a sheet.</summary>
    public void Seal()
    {
        if (_markCount == 0)
        {
            return;
        }

        Array.Sort(_marks, 0, _markCount);

        Array.Sort(_anchors, 0, _anchorCount);

        _tree ??= new int[SpreadsheetLimits.MaximumColumn + 2];
    }

    /// <summary>
    /// True when a range covers (<paramref name="row"/>, <paramref name="column"/>) and the cell is not the top-left cell
    /// of every range that covers it.
    /// </summary>
    /// <exception cref="DocumentPackageRejectedException">The lookup has travelled further than the move bound allows.</exception>
    public bool IsMergedAway(int row, int column)
    {
        if (_markCount == 0 || column < 1 || column > SpreadsheetLimits.MaximumColumn)
        {
            return false;
        }

        row = Math.Clamp(row, 1, SpreadsheetLimits.MaximumRow + 1);

        MoveTo(row);

        int coverage = CoverageAt(column);

        return coverage > 0 && coverage > CountAnchors(AnchorKey(row, column));
    }

    private static long Mark(int row, bool isEnd, int firstColumn, int lastColumn) =>
        ((long)row << MarkRowShift) | ((isEnd ? 1L : 0L) << MarkEndShift) | ((long)(firstColumn - 1) << ColumnBits) | (long)(lastColumn - 1);

    private static long AnchorKey(int row, int column) => ((long)row << ColumnBits) | (long)(column - 1);

    private static void Append(ref long[] items, ref int count, long item)
    {
        if (count == items.Length)
        {
            Array.Resize(ref items, Math.Max(InitialCapacity, items.Length * 2));
        }

        items[count++] = item;
    }

    /// <summary>A cell reference such as <c>C5</c>: ASCII letters, then digits, nothing else; a number past the sheet saturates one beyond it.</summary>
    private static bool TryParseCorner(ReadOnlySpan<char> text, out int row, out int column)
    {
        row = 0;

        column = 0;

        int index = 0;

        while (index < text.Length && text[index] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
        {
            column = Math.Min((column * 26) + (char.ToUpperInvariant(text[index]) - 'A' + 1), SpreadsheetLimits.MaximumColumn + 1);

            index++;
        }

        if (index == 0 || index == text.Length)
        {
            return false;
        }

        for (; index < text.Length; index++)
        {
            if (text[index] is < '0' or > '9')
            {
                return false;
            }

            row = Math.Min((row * 10) + (text[index] - '0'), SpreadsheetLimits.MaximumRow + 1);
        }

        return row > 0;
    }

    /// <summary>Moves the cursor so that exactly the marks at or before <paramref name="row"/> are applied.</summary>
    private void MoveTo(int row)
    {
        int target = CountMarksThrough(row);

        while (_applied < target)
        {
            Step();

            Apply(_marks[_applied], direction: 1);

            _applied++;
        }

        while (_applied > target)
        {
            Step();

            _applied--;

            Apply(_marks[_applied], direction: -1);
        }
    }

    private void Step()
    {
        if (++_moves > limits.MergeEventMoves)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }
    }

    private void Apply(long mark, int direction)
    {
        int delta = ((mark >> MarkEndShift) & 1) == 0 ? direction : -direction;

        int firstColumn = (int)((mark >> ColumnBits) & ColumnMask) + 1;

        int lastColumn = (int)(mark & ColumnMask) + 1;

        AddToTree(firstColumn, delta);

        AddToTree(lastColumn + 1, -delta);
    }

    private void AddToTree(int index, int delta)
    {
        int[] tree = _tree!;

        for (; index < tree.Length; index += index & -index)
        {
            tree[index] += delta;
        }
    }

    private int CoverageAt(int column)
    {
        int[] tree = _tree!;

        int sum = 0;

        for (int index = column; index > 0; index -= index & -index)
        {
            sum += tree[index];
        }

        return sum;
    }

    /// <summary>The number of marks whose row is at most <paramref name="row"/>.</summary>
    private int CountMarksThrough(int row)
    {
        long limit = (long)(row + 1) << MarkRowShift;

        int low = 0;

        int high = _markCount;

        while (low < high)
        {
            int middle = low + ((high - low) / 2);

            if (_marks[middle] < limit)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>The number of ranges whose top-left cell is the one named by <paramref name="key"/>.</summary>
    private int CountAnchors(long key) => LowerBound(key + 1) - LowerBound(key);

    private int LowerBound(long key)
    {
        int low = 0;

        int high = _anchorCount;

        while (low < high)
        {
            int middle = low + ((high - low) / 2);

            if (_anchors[middle] < key)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
