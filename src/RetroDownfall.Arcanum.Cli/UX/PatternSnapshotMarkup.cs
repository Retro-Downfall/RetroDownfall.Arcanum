using RetroDownfall.Arcanum.Core.Pattern.Entities;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

internal static class PatternSnapshotMarkup
{

    public static void WritePatternSnapshot(PatternSnapshot snapshot)
    {
        AnsiConsole.MarkupLine($"[#C0C0C0]Domain:[/] [#87CEEB]{Markup.Escape(snapshot.Domain.ToString())}[/]");

        AnsiConsole.MarkupLine($"[#C0C0C0]Root:[/] [#87CEEB]{Markup.Escape(snapshot.RootPath)}[/]");

        AnsiConsole.MarkupLine("[#C0C0C0]Table of contents[/]");

        foreach (string thread in snapshot.Threads)
        {
            int colon = thread.IndexOf(':');

            if (colon > 0)
            {
                string label = thread[..(colon + 1)];

                string rest = thread[(colon + 1)..].TrimStart();

                AnsiConsole.MarkupLine(
                    $"[#C0C0C0]{Markup.Escape(label)}[/] [#87CEEB]{Markup.Escape(rest)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[#87CEEB]{Markup.Escape(thread)}[/]");
            }
        }
    }

}
