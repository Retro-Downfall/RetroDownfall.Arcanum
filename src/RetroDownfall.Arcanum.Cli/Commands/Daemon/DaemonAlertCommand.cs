using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

public sealed class DaemonAlertCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<DaemonAlertCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Message))
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape("A non-empty message is required.")));

            return 1;

        }

        if (!Enum.TryParse(settings.Severity.Trim(), ignoreCase: true, out CommLinkSeverity severity))
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorLabelMarkup(
                    Markup.Escape("--severity"),
                    Markup.Escape("must be one of: Info, Warning, Critical.")));

            return 1;

        }

        CommLinkMessageRequestDto dto = new(
            settings.Title.Trim(),
            settings.Message.Trim(),
            severity,
            settings.Source.Trim());

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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<MESSAGE>")]
        public required string Message { get; init; }

        [CommandOption("--title|-t")]
        public string Title { get; init; } = "Arcanum alert";

        [CommandOption("--severity|-s")]
        public string Severity { get; init; } = "Warning";

        [CommandOption("--source")]
        public string Source { get; init; } = "cli:daemon alert";

    }

}
