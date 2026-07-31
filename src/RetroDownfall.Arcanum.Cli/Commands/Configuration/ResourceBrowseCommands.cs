using System.Globalization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Configuration;

public sealed class WorkspaceCommands(
    ArcanumApiClient apiClient,
    ICliResourceCatalog resourceCatalog,
    IThemePalette themePalette)
{
    public async Task<int> List(CancellationToken cancellationToken)
    {
        Result<WorkspaceInfo[]> result = await apiClient.GetWorkspacesAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            return 1;
        }

        Table table = new();
        table.AddColumn(themePalette.HeadingTableColumn("Name"));
        table.AddColumn(themePalette.HeadingTableColumn("Path"));
        table.AddColumn(themePalette.HeadingTableColumn("Type"));
        foreach (WorkspaceInfo workspace in result.Value)
        {
            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(workspace.Name))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(workspace.Path))),
                new Markup(themePalette.TextMarkup(Markup.Escape(workspace.Type.ToString()))));
        }
        AnsiConsole.Write(table);
        return 0;
    }

    public async Task<int> Get(string? identifier, CancellationToken cancellationToken)
    {
        ResourceSelectionResult<WorkspaceInfo> selection = await resourceCatalog
            .SelectWorkspaceAsync(identifier, cancellationToken)
            .ConfigureAwait(false);
        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {
            return 0;
        }
        if (selection.Status == ResourceSelectionStatus.Error)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(selection.Error!)));
            return 1;
        }

        WorkspaceInfo workspace = selection.Value!;
        Table table = DetailTable();
        table.AddRow("Id:", Markup.Escape(workspace.Id));
        table.AddRow("Name:", Markup.Escape(workspace.Name));
        table.AddRow("Path:", Markup.Escape(workspace.Path));
        table.AddRow("Type:", Markup.Escape(workspace.Type.ToString()));
        table.AddRow("Registered:", Markup.Escape(workspace.RegisteredAt.ToString("u", CultureInfo.InvariantCulture)));
        AnsiConsole.Write(table);
        return 0;
    }

    private static Table DetailTable()
    {
        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(string.Empty);
        table.AddColumn(string.Empty);
        return table;
    }
}

public sealed class McpCommands(
    ArcanumApiClient apiClient,
    ICliResourceCatalog resourceCatalog,
    IThemePalette themePalette)
{
    public async Task<int> List(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<McpServerInfo>> result = await apiClient.GetMcpServersAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            return 1;
        }

        Table table = new();
        table.AddColumn(themePalette.HeadingTableColumn("Name"));
        table.AddColumn(themePalette.HeadingTableColumn("State"));
        table.AddColumn(themePalette.HeadingTableColumn("Transport"));
        table.AddColumn(themePalette.HeadingTableColumn("Tools"));
        foreach (McpServerInfo server in result.Value)
        {
            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(server.Name))),
                new Markup(themePalette.TextMarkup(Markup.Escape(server.State.ToString()))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(server.Transport.ToString()))),
                new Markup(themePalette.MutedMarkup(server.Tools.Length.ToString(CultureInfo.InvariantCulture))));
        }
        AnsiConsole.Write(table);
        return 0;
    }

    public async Task<int> Get(string? identifier, CancellationToken cancellationToken)
    {
        ResourceSelectionResult<McpServerInfo> selection = await resourceCatalog
            .SelectMcpServerAsync(identifier, cancellationToken)
            .ConfigureAwait(false);
        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {
            return 0;
        }
        if (selection.Status == ResourceSelectionStatus.Error)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(selection.Error!)));
            return 1;
        }

        McpServerInfo server = selection.Value!;
        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(string.Empty);
        table.AddColumn(string.Empty);
        table.AddRow("Name:", Markup.Escape(server.Name));
        table.AddRow("State:", Markup.Escape(server.State.ToString()));
        table.AddRow("Transport:", Markup.Escape(server.Transport.ToString()));
        table.AddRow("Always on:", server.AlwaysOn ? "yes" : "no");
        table.AddRow("Tools:", Markup.Escape(server.Tools.Length == 0 ? "(none)" : string.Join(", ", server.Tools)));
        if (!string.IsNullOrWhiteSpace(server.ErrorMessage))
        {
            table.AddRow("Error:", Markup.Escape(server.ErrorMessage));
        }
        AnsiConsole.Write(table);
        return 0;
    }
}
