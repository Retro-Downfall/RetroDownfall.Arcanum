using RetroDownfall.Arcanum.Core.Pattern;

using RetroDownfall.Arcanum.Core.Pattern.Entities;

using Spectre.Console;

using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class LookCommand(IEyeOfTheWorld eye) : AsyncCommand
{

    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        PatternSnapshot snapshot = await eye

            .PerceivePatternAsync(Environment.CurrentDirectory, cancellationToken)

            .ConfigureAwait(false);

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

        return 0;

    }

}
