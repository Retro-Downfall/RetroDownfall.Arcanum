using RetroDownfall.Arcanum.Core.Pattern.Entities;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

internal static class PatternSnapshotMarkup
{

    public static void WritePatternSnapshot(PatternSnapshot snapshot, IThemePalette palette)
    {
        AnsiConsole.MarkupLine(
            palette.MutedLabelMarkup(Markup.Escape("Domain:"), Markup.Escape(snapshot.Domain.ToString())));

        AnsiConsole.MarkupLine(
            palette.MutedLabelMarkup(Markup.Escape("Root:"), Markup.Escape(snapshot.RootPath)));

        AnsiConsole.MarkupLine(palette.HeadingBoldMarkup(Markup.Escape("Table of contents")));

        foreach (string thread in snapshot.Threads)
        {
            int colon = thread.IndexOf(':');

            if (colon > 0)
            {
                string label = thread[..(colon + 1)];

                string rest = thread[(colon + 1)..].TrimStart();

                AnsiConsole.MarkupLine(
                    $"{palette.MutedMarkup(Markup.Escape(label))} {palette.HighlightMarkup(Markup.Escape(rest))}");
            }
            else
            {
                AnsiConsole.MarkupLine(palette.HighlightMarkup(Markup.Escape(thread)));
            }
        }
    }

}
