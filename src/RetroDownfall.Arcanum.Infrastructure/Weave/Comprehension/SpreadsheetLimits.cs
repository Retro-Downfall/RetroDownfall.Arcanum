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
/// <param name="MergedRanges">Most <c>mergeCell</c> elements one worksheet may declare.</param>
/// <param name="MergeEventMoves">
/// Most range start and end marks the merged-cell lookup may step over while one extraction's cells are read. Rows that
/// ascend, as every producer writes them, cost each mark once; rows that jump back and forth make the lookup travel again.
/// </param>
internal sealed record SpreadsheetLimits(
    int Sheets,
    int ColumnElements,
    long Cells,
    int SharedStringCharacters,
    int SharedStrings,
    int MergedRanges = 65_536,
    long MergeEventMoves = 16_777_216)
{
    /// <summary>The highest column number a worksheet can address (<c>XFD</c>).</summary>
    internal const int MaximumColumn = 16384;

    /// <summary>The highest row number a worksheet can address.</summary>
    internal const int MaximumRow = 1_048_576;

    public static SpreadsheetLimits Default { get; } = new(
        Sheets: 4096,
        ColumnElements: 16384,
        Cells: 1_048_576,
        SharedStringCharacters: 1_048_576,
        SharedStrings: 1_048_576);
}
