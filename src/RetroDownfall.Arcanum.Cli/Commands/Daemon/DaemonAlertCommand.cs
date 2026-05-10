using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

public sealed class DaemonAlertCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand
{

    [CommandArgument(0, "<MESSAGE>")]
    public required string Message { get; init; }

    [CommandOption("--title|-t")]
    public string Title { get; init; } = "Arcanum alert";

    [CommandOption("--severity|-s")]
    public string Severity { get; init; } = "Warning";

    [CommandOption("--source")]
    public string Source { get; init; } = "cli:daemon alert";

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        if (!Enum.TryParse(Severity.Trim(), ignoreCase: true, out CommLinkSeverity severity))
        {

            severity = CommLinkSeverity.Warning;

        }

        CommLinkMessageRequestDto dto = new(
            Title.Trim(),
            Message.Trim(),
            severity,
            Source.Trim());

        Result<bool> result = await apiClient
            .SendCommLinkAlertAsync(dto, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(result.Error.Message)));

            return 1;

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Comm Link sent:"),
                Markup.Escape($"{dto.Title} ({dto.Severity}).")));

        return 0;

    }

}
