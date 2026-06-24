using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

public sealed class DaemonInitiativeCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<DaemonInitiativeCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<UnseenServantJobStatusDto> result = await apiClient
            .AdjustDaemonJobInitiativeAsync(settings.JobName, settings.Minutes, cancellationToken)
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
                    $"{dto.Name} — effective interval is now {dto.EffectiveIntervalMinutes} minute(s) (base {dto.BaseIntervalMinutes}).")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<JOB_NAME>")]
        public required string JobName { get; init; }

        [CommandArgument(1, "<MINUTES>")]
        public required int Minutes { get; init; }

    }

}
