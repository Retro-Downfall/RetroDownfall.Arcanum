using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Llama;

public sealed class LlamaStartCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand<LlamaStartCommand.Settings>
{

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<CACHE_KEY>")]
        public string? CacheKey { get; init; }

        [CommandOption("--gpu-layers")]
        public int? GpuLayers { get; init; }

        [CommandOption("--port")]
        public int? Port { get; init; }

    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.CacheKey))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Cache key is required.")));

            return 1;
        }

        Result<LlamaServerInfo> result = await apiClient
            .StartLlamaServerAsync(settings.CacheKey, settings.GpuLayers, settings.Port, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(result.Error.Message)));

            return 1;
        }

        LlamaServerInfo server = result.Value;

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(
                Markup.Escape($"llama-server healthy at {server.Endpoint} (state: {server.State}, port: {server.Port}).")));

        return 0;

    }

}
