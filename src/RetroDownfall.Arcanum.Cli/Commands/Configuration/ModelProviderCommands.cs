using ConsoleAppFramework;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Configuration;

/// <summary>
/// Native model listing across configured providers (requires arcanum serve).
/// </summary>
public sealed class ModelCommands(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// List configured models across all providers (GET /api/models).
    /// </summary>
    [Command("list")]
    public async Task<int> List(CancellationToken cancellationToken)
    {

        Result<ModelInfoDto[]> result = await apiClient.GetModelsAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Model")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Provider")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Type")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Context Window")));

        foreach (ModelInfoDto model in result.Value)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(model.Model))),
                new Markup(themePalette.TextMarkup(Markup.Escape(model.ProviderName))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(model.ProviderType))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(model.ContextWindowLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

        }

        AnsiConsole.Write(table);

        if (result.Value.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No models are configured.")));
        }

        return 0;

    }

}

/// <summary>
/// Native provider listing and configuration summary (requires arcanum serve).
/// </summary>
public sealed class ProviderCommands(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// List configured providers with redacted secrets (GET /api/providers).
    /// </summary>
    [Command("list")]
    public async Task<int> List(CancellationToken cancellationToken)
    {

        Result<ProviderInfoDto[]> result = await apiClient.GetProvidersAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Name")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Type")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Endpoint")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Models")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Context Window")));

        foreach (ProviderInfoDto provider in result.Value)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(provider.Name))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(provider.Type))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(provider.Endpoint))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(provider.Models.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(provider.ContextWindowLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

        }

        AnsiConsole.Write(table);

        if (result.Value.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No providers are configured.")));
        }

        return 0;

    }

}
