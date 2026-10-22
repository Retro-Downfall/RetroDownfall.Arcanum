namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The structural and retention bounds of one spreadsheet extraction. <see cref="Default"/> holds the code-owned
/// production values; tests construct smaller ones so each bound can be exercised without a huge fixture.
/// </summary>
/// <param name="Sheets">Most <c>sheet</c> elements one workbook may list (hidden ones included).</param>
/// <param name="ColumnElements">Most <c>col</c> elements one worksheet may declare.</param>
/// <param name="Cells">Most <c>c</c> elements processed across the whole extraction.</param>
/// <param name="SharedStringCharacters">Most UTF-16 characters of shared string text retained in total.</param>
/// <param name="SharedStrings">Most shared strings retained.</param>
internal sealed record SpreadsheetLimits(int Sheets, int ColumnElements, long Cells, int SharedStringCharacters, int SharedStrings)
{
    /// <summary>The highest column number a worksheet can address (<c>XFD</c>).</summary>
    internal const int MaximumColumn = 16384;

    public static SpreadsheetLimits Default { get; } = new(
        Sheets: 4096,
        ColumnElements: 16384,
        Cells: 1_048_576,
        SharedStringCharacters: 1_048_576,
        SharedStrings: 1_048_576);
}
