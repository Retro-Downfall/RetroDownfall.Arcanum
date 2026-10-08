using Spectre.Console;
using Spectre.Console.Rendering;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// How a listing column behaves when the terminal is narrower than the listing.
/// </summary>
internal enum ListingColumnRole
{
    /// <summary>
    /// Never wrapped and never dropped: an identifier an operator copies into the next command, or a short
    /// state.
    /// </summary>
    Fixed,

    /// <summary>
    /// The one column that gives way: free text that takes the width the others leave and wraps.
    /// </summary>
    Flexible,

    /// <summary>
    /// Dropped, highest <see cref="ListingColumn{TRow}.DropOrder"/> first, when the listing does not fit; shown
    /// whole and unwrapped otherwise.
    /// </summary>
    Optional,
}

/// <summary>
/// One column of a <see cref="ListingTable"/>.
/// </summary>
/// <typeparam name="TRow">The listed row.</typeparam>
/// <param name="Header">The column heading.</param>
/// <param name="Text">The cell text for a row, unescaped.</param>
/// <param name="Role">What the column does when the terminal is too narrow.</param>
/// <param name="Muted">Whether the cell is secondary text.</param>
/// <param name="DropOrder">Among optional columns, the one with the highest value is dropped first.</param>
internal sealed record ListingColumn<TRow>(
    string Header,
    Func<TRow, string> Text,
    ListingColumnRole Role,
    bool Muted = false,
    int DropOrder = 0);

/// <summary>
/// A built table and the headings of the optional columns that did not fit beside the others.
/// </summary>
/// <param name="Table">The table to write.</param>
/// <param name="HiddenHeaders">The optional columns left out, in the order they were dropped.</param>
internal sealed record ListingTableResult(Table Table, string[] HiddenHeaders);

/// <summary>
/// Builds a listing table that stays readable at the terminal's width.
/// </summary>
/// <remarks>
/// A listing prints an identifier in full because it is what the next command is handed, and a full
/// identifier cannot wrap or shrink. Spectre fits a table that is too wide by squeezing every wrappable
/// column, which at 80 columns leaves a goal or a memory's text three characters wide and truncates the
/// headings. This decides what fits first: the fixed columns keep their whole content, the flexible column
/// keeps a usable minimum, and optional columns are left out, least valuable first, until the rest fit. The
/// caller says which were left out, so the operator is told and never left to wonder.
/// </remarks>
internal static class ListingTable
{
    /// <summary>The width the flexible column is guaranteed, or its whole content when that is shorter.</summary>
    public const int FlexibleMinimumWidth = 20;

    /// <summary>
    /// Builds the table for <paramref name="rows"/> within <paramref name="availableWidth"/> columns.
    /// </summary>
    /// <param name="palette">The theme the cells are drawn with.</param>
    /// <param name="columns">Every column the listing can show, in display order.</param>
    /// <param name="rows">The rows to list.</param>
    /// <param name="availableWidth">The terminal width.</param>
    public static ListingTableResult Build<TRow>(
        IThemePalette palette,
        IReadOnlyList<ListingColumn<TRow>> columns,
        IReadOnlyList<TRow> rows,
        int availableWidth)
    {
        ArgumentNullException.ThrowIfNull(palette);

        ArgumentNullException.ThrowIfNull(columns);

        ArgumentNullException.ThrowIfNull(rows);

        // Cell text is listed data the operator did not write (a goal, a memory, a title), and Markup
        // escaping leaves the control sequences a terminal acts on in place, so every cell is stripped
        // before it is measured or drawn.
        string[][] cells =
        [
            .. rows.Select(row => columns
                .Select(column => TerminalTextSanitizer.SanitizeLine(column.Text(row)))
                .ToArray()),
        ];

        List<int> visible = [.. Enumerable.Range(0, columns.Count)];

        List<string> hidden = [];

        while (!Fits(columns, cells, visible, availableWidth))
        {
            int drop = visible
                .Where(index => columns[index].Role == ListingColumnRole.Optional)
                .OrderByDescending(index => columns[index].DropOrder)
                .FirstOrDefault(-1);

            if (drop < 0)
            {
                break;
            }

            visible.Remove(drop);

            hidden.Add(columns[drop].Header);
        }

        Table table = new();

        foreach (int index in visible)
        {
            TableColumn column = new(palette.HeadingTableColumn(Markup.Escape(columns[index].Header)));

            if (columns[index].Role != ListingColumnRole.Flexible)
            {
                column.NoWrap();
            }

            table.AddColumn(column);
        }

        foreach (string[] row in cells)
        {
            table.AddRow(
                visible
                    .Select(index => (IRenderable)new Markup(
                        columns[index].Muted
                            ? palette.MutedMarkup(Markup.Escape(row[index]))
                            : palette.TextMarkup(Markup.Escape(row[index]))))
                    .ToArray());
        }

        return new ListingTableResult(table, [.. hidden]);
    }

    /// <summary>
    /// The line that tells the operator which columns were left out, or <see langword="null"/> when none were.
    /// </summary>
    /// <param name="built">The built table.</param>
    /// <param name="availableWidth">The terminal width the table was built for.</param>
    public static string? HiddenColumnsNotice(ListingTableResult built, int availableWidth)
    {
        ArgumentNullException.ThrowIfNull(built);

        return built.HiddenHeaders.Length == 0
            ? null
            : $"Not shown at {availableWidth} columns: {string.Join(", ", built.HiddenHeaders)}. Widen the terminal to see them.";
    }

    /// <summary>
    /// Whether the visible columns fit: each non-flexible column at its widest cell, the flexible one at its
    /// minimum, and the borders and padding Spectre draws between them (one border and two spaces per column,
    /// and the closing border).
    /// </summary>
    private static bool Fits<TRow>(
        IReadOnlyList<ListingColumn<TRow>> columns,
        string[][] cells,
        List<int> visible,
        int availableWidth)
    {
        int needed = (3 * visible.Count) + 1;

        foreach (int index in visible)
        {
            int widest = Math.Max(
                columns[index].Header.Length,
                cells.Length == 0 ? 0 : cells.Max(row => row[index].Length));

            needed += columns[index].Role == ListingColumnRole.Flexible
                ? Math.Min(widest, FlexibleMinimumWidth)
                : widest;
        }

        return needed <= availableWidth;
    }
}
