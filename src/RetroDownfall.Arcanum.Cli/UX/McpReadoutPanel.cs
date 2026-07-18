using RetroDownfall.Arcanum.Core.Mcp;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Compact readout of configured MCP servers and their lifecycle state, for the
/// chat sidebar and any standalone "mcp status" style views.
/// </summary>
internal static class McpReadoutPanel
{

    private const int MaxServerRows = 10;

    internal static Panel Render(IReadOnlyList<McpServerInfo> servers, IThemePalette theme)
    {

        ArgumentNullException.ThrowIfNull(servers);

        ArgumentNullException.ThrowIfNull(theme);

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddColumn(new TableColumn(string.Empty).RightAligned());

        if (servers.Count == 0)
        {
            table.AddRow(
                theme.MutedMarkup(Markup.Escape("·")),
                theme.MutedMarkup(Markup.Escape("no servers configured")),
                theme.MutedMarkup(string.Empty));
        }

        int shown = Math.Min(servers.Count, MaxServerRows);

        for (int i = 0; i < shown; i++)
        {
            McpServerInfo server = servers[i];

            table.AddRow(
                StateGlyph(server.State, theme),
                theme.TextMarkup(Markup.Escape(server.Name)),
                theme.MutedMarkup(Markup.Escape($"{server.Tools.Length} tools")));
        }

        int remaining = servers.Count - shown;

        if (remaining > 0)
        {
            table.AddRow(
                theme.MutedMarkup(Markup.Escape("·")),
                theme.MutedMarkup(Markup.Escape($"+{remaining} more")),
                theme.MutedMarkup(string.Empty));
        }

        return new Panel(table)
        {
            Header = new PanelHeader(theme.HeadingBoldMarkup(Markup.Escape("MCP"))),
            Border = BoxBorder.Rounded,
            BorderStyle = theme.MutedStyle(),
            Padding = new Padding(1, 0, 1, 0),
        };

    }

    private static string StateGlyph(McpServerState state, IThemePalette theme) => state switch
    {
        McpServerState.Running => "[green]●[/]",
        McpServerState.Starting or McpServerState.Restarting => "[yellow]○[/]",
        McpServerState.Error => theme.ErrorMarkup("✗"),
        McpServerState.Stopped => theme.MutedMarkup("·"),
        _ => theme.MutedMarkup("?"),
    };

}
