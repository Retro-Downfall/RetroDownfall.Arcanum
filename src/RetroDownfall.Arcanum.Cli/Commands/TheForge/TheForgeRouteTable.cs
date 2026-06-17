using RetroDownfall.Arcanum.Cli.UX;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

internal static class TheForgeRouteTable
{

    internal static void Print(string title, IReadOnlyList<(string Verb, string Path, string Purpose)> rows, IThemePalette themePalette)
    {

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Verb")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Path")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Purpose")));

        foreach ((string verb, string path, string purpose) in rows)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(verb))),
                new Markup(themePalette.TextMarkup(Markup.Escape(path))),
                new Markup(themePalette.TextMarkup(Markup.Escape(purpose))));

        }

        AnsiConsole.Write(new Panel(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape(title))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        });

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(
            Markup.Escape("Stub command — call these routes via HTTP while arcanum serve is running (see README).")));

    }

}
