using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

public sealed class DaemonJobsCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand
{

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        Result<UnseenServantJobStatusDto[]> result =
            await apiClient.GetDaemonJobsAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        UnseenServantJobStatusDto[] jobs = result.Value;

        Table table = new();

        table.AddColumn(new TableColumn(themePalette.HeadingTableColumn(Markup.Escape("Job name"))));

        table.AddColumn(new TableColumn(themePalette.HeadingTableColumn(Markup.Escape("Target spell"))));

        table.AddColumn(new TableColumn(themePalette.HeadingTableColumn(Markup.Escape("Base (min)"))));

        table.AddColumn(new TableColumn(themePalette.HeadingTableColumn(Markup.Escape("Effective (min)"))));

        table.AddColumn(new TableColumn(themePalette.HeadingTableColumn(Markup.Escape("Status"))));

        table.AddColumn(new TableColumn(themePalette.HeadingTableColumn(Markup.Escape("Last run"))));

        table.AddColumn(new TableColumn(themePalette.HeadingTableColumn(Markup.Escape("Next due"))));

        table.AddColumn(new TableColumn(themePalette.HeadingTableColumn(Markup.Escape("Last result"))));

        foreach (UnseenServantJobStatusDto job in jobs)
        {

            string baseText = $"{job.BaseIntervalMinutes}";

            string baseCell = themePalette.TextMarkup(Markup.Escape(baseText));

            string effectiveText = $"{job.EffectiveIntervalMinutes}";

            string effectiveCell = job.EffectiveIntervalMinutes != job.BaseIntervalMinutes
                ? themePalette.HighlightMarkup(Markup.Escape(effectiveText))
                : themePalette.TextMarkup(Markup.Escape(effectiveText));

            string statusText = job.IsEnabled ? "Enabled" : "Disabled";

            string statusCell = job.IsEnabled
                ? themePalette.TextMarkup(Markup.Escape(statusText))
                : themePalette.MutedMarkup(Markup.Escape(statusText));

            string lastRunText = job.LastRunAt?.ToString("u") ?? "-";

            string nextDueText = job.NextDueAt?.ToString("u") ?? "-";

            string lastResultText = job.LastResult ?? "-";

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(job.Name))),
                new Markup(themePalette.TextMarkup(Markup.Escape(job.TargetSpell))),
                new Markup(baseCell),
                new Markup(effectiveCell),
                new Markup(statusCell),
                new Markup(themePalette.MutedMarkup(Markup.Escape(lastRunText))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(nextDueText))),
                new Markup(themePalette.TextMarkup(Markup.Escape(lastResultText))));
        }

        AnsiConsole.Write(table);

        if (jobs.Length == 0)
        {

            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(Markup.Escape("No Unseen Servant jobs are configured under Arcanum:Daemon:Jobs.")));
        }

        return 0;
    }

}
