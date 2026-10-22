namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The hidden columns of one worksheet, as a coverage count per column built from difference marks, so any number of
/// (possibly overlapping) <c>col</c> ranges costs one pass over at most 16,384 columns instead of a walk per range. The
/// array is allocated once, on the first hidden range, and reused by every later sheet.
/// </summary>
internal sealed class SpreadsheetHiddenColumns
{
    private int[]? _coverage;

    private bool _any;

    /// <summary>Forgets the previous sheet's hidden columns.</summary>
    public void Clear()
    {
        if (_any)
        {
            Array.Clear(_coverage!);

            _any = false;
        }
    }

    /// <summary>Hides columns <paramref name="first"/> to <paramref name="last"/> inclusive, both already within 1..16384.</summary>
    public void Hide(int first, int last)
    {
        _coverage ??= new int[SpreadsheetLimits.MaximumColumn + 2];

        _coverage[first]++;

        _coverage[last + 1]--;

        _any = true;
    }

    /// <summary>Turns the recorded marks into per-column coverage; call once after the last <see cref="Hide"/> of a sheet.</summary>
    public void Seal()
    {
        if (!_any)
        {
            return;
        }

        int running = 0;

        for (int column = 1; column <= SpreadsheetLimits.MaximumColumn; column++)
        {
            running += _coverage![column];

            _coverage[column] = running;
        }
    }

    public bool IsHidden(int column) => _any && column is >= 1 and <= SpreadsheetLimits.MaximumColumn && _coverage![column] > 0;
}
