using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

/// <summary>
/// Manage the Arcanum background daemon.
/// </summary>
public sealed class DaemonCommands(IDaemonManager daemonManager, ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// Install and start the Arcanum background daemon.
    /// </summary>
    public async Task<int> Install(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Installing launchd agent\u2026")));

        Result result = await daemonManager.InstallAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorLabelMarkup(Markup.Escape("Error:"), result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape("Daemon installed and bootstrapped.")));

        return 0;
    }

    /// <summary>
    /// Stop and uninstall the Arcanum background daemon.
    /// </summary>
    public async Task<int> Uninstall(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Removing launchd agent\u2026")));

        Result result = await daemonManager.UninstallAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorLabelMarkup(Markup.Escape("Error:"), result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape("Daemon uninstall finished.")));

        return 0;
    }

    /// <summary>
    /// Show whether the Arcanum daemon is running.
    /// </summary>
    public async Task<int> Status(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Checking launchd status\u2026")));

        Result<string> result = await daemonManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorLabelMarkup(Markup.Escape("Error:"), result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape(result.Value)));

        return 0;
    }

    /// <summary>
    /// List Unseen Servant jobs (requires API: arcanum serve on Arcanum:Host:Port).
    /// </summary>
    public async Task<int> Jobs(CancellationToken cancellationToken)
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

    /// <summary>
    /// Set adaptive polling interval for a job (requires API: arcanum serve on Arcanum:Host:Port).
    /// </summary>
    /// <param name="jobName">The Unseen Servant job name.</param>
    /// <param name="minutes">The new polling interval in minutes (&gt;= 1).</param>
    public async Task<int> Initiative(string jobName, int minutes, CancellationToken cancellationToken)
    {

        // W4.1: validate the interval client-side so an obviously-invalid value fails fast with a
        // clear message instead of round-tripping to the API.
        if (minutes < 1)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Minutes must be a positive integer (>= 1).")));

            return 1;

        }

        Result<UnseenServantJobStatusDto> result = await apiClient
            .AdjustDaemonJobInitiativeAsync(jobName, minutes, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        UnseenServantJobStatusDto dto = result.Value;

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Initiative updated:"),
                Markup.Escape(
                    $"{dto.Name} \u2014 effective interval is now {dto.EffectiveIntervalMinutes} minute(s) (base {dto.BaseIntervalMinutes}).")));

        return 0;

    }

    /// <summary>
    /// Send a Comm Link test alert via POST /api/commlink/send (requires API: arcanum serve on Arcanum:Host:Port).
    /// </summary>
    /// <param name="message">The alert message.</param>
    /// <param name="title">-t, Alert title.</param>
    /// <param name="severity">-s, Severity: Info, Warning, or Critical.</param>
    /// <param name="source">The alert source label.</param>
    public async Task<int> Alert(
        string message,
        string title = "Arcanum alert",
        string severity = "Warning",
        string source = "cli:daemon alert",
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(message))
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape("A non-empty message is required.")));

            return 1;

        }

        if (!Enum.TryParse(severity.Trim(), ignoreCase: true, out CommLinkSeverity parsedSeverity))
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorLabelMarkup(
                    Markup.Escape("--severity"),
                    Markup.Escape("must be one of: Info, Warning, Critical.")));

            return 1;

        }

        CommLinkMessageRequestDto dto = new(
            title.Trim(),
            message.Trim(),
            parsedSeverity,
            source.Trim());

        Result<bool> result = await apiClient
            .SendCommLinkAlertAsync(dto, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Comm Link sent:"),
                Markup.Escape($"{dto.Title} ({dto.Severity}).")));

        return 0;

    }

}
