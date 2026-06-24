using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Llama;

public sealed class LlamaStatusCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand
{

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        Result<LlamaServerInfo[]> serversResult = await apiClient.ListLlamaServersAsync(cancellationToken).ConfigureAwait(false);

        if (serversResult.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(serversResult.Error));

            return 1;
        }

        Result<CachedModelInfo[]> modelsResult = await apiClient.ListCachedModelsAsync(cancellationToken).ConfigureAwait(false);

        if (modelsResult.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(modelsResult.Error));

            return 1;
        }

        Table serversTable = new();

        serversTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Cache key")));

        serversTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("State")));

        serversTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Endpoint")));

        serversTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("PID")));

        foreach (LlamaServerInfo server in serversResult.Value)
        {
            serversTable.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(server.CacheKey))),
                new Markup(themePalette.TextMarkup(Markup.Escape(server.State.ToString()))),
                new Markup(themePalette.TextMarkup(Markup.Escape(server.Endpoint))),
                new Markup(themePalette.TextMarkup(Markup.Escape(server.ProcessId?.ToString() ?? "-"))));
        }

        AnsiConsole.Write(new Panel(serversTable)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Running servers"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        });

        if (serversResult.Value.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No llama-server instances are running.")));
        }

        Table modelsTable = new();

        modelsTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Cache key")));

        modelsTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Size")));

        modelsTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Last accessed")));

        foreach (CachedModelInfo model in modelsResult.Value)
        {
            string sizeText = FormatBytes(model.Size);

            modelsTable.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(model.CacheKey))),
                new Markup(themePalette.TextMarkup(Markup.Escape(sizeText))),
                new Markup(themePalette.TextMarkup(Markup.Escape(model.LastAccessedAt.ToString("u")))));
        }

        AnsiConsole.Write(new Panel(modelsTable)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Cached models"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        });

        if (modelsResult.Value.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No GGUF models are cached.")));
        }

        return 0;

    }

    internal static string FormatBytes(long bytes)
    {

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KiB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MiB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):F1} GiB";

    }

}
