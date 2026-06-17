using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Llama;

public sealed class LlamaStopCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand<LlamaStopCommand.Settings>
{

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "[CACHE_KEY]")]
        public string? CacheKey { get; init; }

    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<bool> result = await apiClient.StopLlamaServerAsync(settings.CacheKey, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(result.Error.Message)));

            return 1;
        }

        string message = string.IsNullOrWhiteSpace(settings.CacheKey)
            ? "Stopped all llama-server instances."
            : $"Stopped llama-server for '{settings.CacheKey}'.";

        AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape(message)));

        return 0;

    }

}
