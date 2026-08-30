using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Wards;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Wards;

/// <summary>
/// Retained Ward record compatibility API (requires arcanum serve).
/// </summary>
public sealed class WardCommands(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// List active compatibility wards (GET /api/wards).
    /// </summary>
    public async Task<int> List(CancellationToken cancellationToken)
    {

        Result<WardDto[]> result = await apiClient.GetWardsAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

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

    /// <summary>
    /// Show compatibility ward detail (GET /api/wards/{id}).
    /// </summary>
    /// <param name="id">Ward ID.</param>
    public async Task<int> Get(string id, CancellationToken cancellationToken)
    {

        Result<WardDto> result = await apiClient.GetWardAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            if (string.Equals(result.Error.Code, "Ward.NotFound", StringComparison.Ordinal))
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("Ward not found.")));
            }
            else
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));
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

    /// <summary>
    /// Resolve a compatibility ward (POST /api/wards/{id}).
    /// </summary>
    /// <param name="id">Ward ID.</param>
    /// <param name="allow">Allow the warded tool call to proceed.</param>
    /// <param name="deny">Deny the warded tool call.</param>
    /// <param name="reason">Optional reason recorded with the resolution.</param>
    public async Task<int> Resolve(
        string id,
        bool allow,
        bool deny,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {

        if (allow == deny)
        {
            CliErrorOutput.WriteMarkupLine(
                themePalette.ErrorMarkup(Markup.Escape("Exactly one of --allow or --deny is required.")));

            return 1;
        }

        Result<WardResolutionDto> result = await apiClient
            .ResolveWardAsync(id, allow, reason, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            if (string.Equals(result.Error.Code, "Ward.NotFound", StringComparison.Ordinal))
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("Ward not found.")));
            }
            else if (string.Equals(result.Error.Code, "Ward.AlreadyResolved", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape("Ward already resolved.")));
            }
            else
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));
            }

            return 1;

        }

        WardResolutionDto resolution = result.Value;

        string verb = resolution.Allowed ? "allowed" : "denied";

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Ward resolved:"), Markup.Escape($"{resolution.WardId} ({verb})")));

        return 0;

    }

}
