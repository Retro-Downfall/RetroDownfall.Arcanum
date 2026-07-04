using System.ComponentModel;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Wards;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Wards;

public sealed class WardListCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand
{

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        Result<WardDto[]> result = await apiClient.GetWardsAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        WardDto[] wards = result.Value;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Ward ID")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Tool")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Placed")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Expires")));

        foreach (WardDto ward in wards)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(ward.WardId))),
                new Markup(themePalette.TextMarkup(Markup.Escape(ward.ToolName))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(ward.PlacedAt.ToString("u")))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(ward.ExpiresAt.ToString("u")))));

        }

        AnsiConsole.Write(table);

        if (wards.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No active wards.")));
        }

        return 0;

    }

}

public sealed class WardGetCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<WardGetCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<WardDto> result = await apiClient.GetWardAsync(settings.Id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            if (string.Equals(result.Error.Code, "Ward.NotFound", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Ward not found.")));
            }
            else
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            }

            return 1;

        }

        WardDto ward = result.Value;

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Ward ID:")), themePalette.HighlightMarkup(Markup.Escape(ward.WardId)));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Tool:")), themePalette.TextMarkup(Markup.Escape(ward.ToolName)));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Session:")),
            themePalette.TextMarkup(Markup.Escape(ward.SessionId ?? "(none)")));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Placed:")), themePalette.TextMarkup(Markup.Escape(ward.PlacedAt.ToString("u"))));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Expires:")), themePalette.TextMarkup(Markup.Escape(ward.ExpiresAt.ToString("u"))));

        Panel panel = new(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Ward: {ward.WardId}"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        };

        AnsiConsole.Write(panel);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public sealed class WardResolveCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<WardResolveCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (settings.Allow == settings.Deny)
        {
            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(Markup.Escape("Exactly one of --allow or --deny is required.")));

            return 1;
        }

        Result<WardResolutionDto> result = await apiClient
            .ResolveWardAsync(settings.Id, settings.Allow, settings.Reason, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            if (string.Equals(result.Error.Code, "Ward.NotFound", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Ward not found.")));
            }
            else if (string.Equals(result.Error.Code, "Ward.AlreadyResolved", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape("Ward already resolved.")));
            }
            else
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            }

            return 1;

        }

        WardResolutionDto resolution = result.Value;

        string verb = resolution.Allowed ? "allowed" : "denied";

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Ward resolved:"), Markup.Escape($"{resolution.WardId} ({verb})")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--allow")]
        [Description("Allow the warded tool call to proceed.")]
        public bool Allow { get; init; }

        [CommandOption("--deny")]
        [Description("Deny the warded tool call.")]
        public bool Deny { get; init; }

        [CommandOption("--reason <TEXT>")]
        [Description("Optional reason recorded with the resolution.")]
        public string? Reason { get; init; }

    }

}
