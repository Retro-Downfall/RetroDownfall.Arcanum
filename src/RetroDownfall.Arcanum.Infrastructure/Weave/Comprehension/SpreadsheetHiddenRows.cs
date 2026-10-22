namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The hidden rows of one worksheet, as one bit per row number up to one beyond the last row (the number every larger one
/// saturates to). A row is hidden when any <c>row</c> element numbered for it says so, so a cell that names a hidden row
/// by its own reference can be told apart from one in a visible row wherever the hidden element sits in the part. The
/// bit set (128 KiB) is allocated once, on the first hidden row, and reused by every later sheet.
/// </summary>
internal sealed class SpreadsheetHiddenRows
{
    private ulong[]? _bits;

    private bool _any;

    /// <summary>Forgets the previous sheet's hidden rows.</summary>
    public void Clear()
    {
        if (_any)
        {
            Array.Clear(_bits!);

            _any = false;
        }
    }

    /// <summary>Marks <paramref name="row"/> (1 to one beyond the last row) hidden.</summary>
    public void Hide(int row)
    {
        if (row is < 1 or > SpreadsheetLimits.MaximumRow + 1)
        {
            return;
        }

        _bits ??= new ulong[((SpreadsheetLimits.MaximumRow + 1) >> 6) + 1];

        _bits[row >> 6] |= 1UL << (row & 63);

        _any = true;
    }

    public bool IsHidden(int row) =>
        _any && row is >= 1 and <= SpreadsheetLimits.MaximumRow + 1 && (_bits![row >> 6] & (1UL << (row & 63))) != 0;
}
