using System.Globalization;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Mcp;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Configuration;

public sealed class McpCommands(
    ArcanumApiClient apiClient,
    ICliEnvironment environment,
    IResourcePicker picker,
    IRecentResourceStore recentStore,
    IThemePalette themePalette)
{

    public async Task<int> List(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        Result<IReadOnlyList<McpServerInfo>> result = await apiClient
            .GetMcpServersAsync(cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        IEnumerable<McpServerInfo> servers = result.Value;

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {

            string normalized = NormalizeWorkspace(workingDirectory);

            servers = servers.Where(
                server => string.Equals(
                    NormalizeWorkspace(server.WorkingDirectory),
                    normalized,
                    StringComparison.Ordinal));

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Server"));

        table.AddColumn(themePalette.HeadingTableColumn("Field"));

        table.AddColumn(themePalette.HeadingTableColumn("Value"));

        foreach (McpServerInfo server in servers)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(server.Name))),
                new Markup(themePalette.HighlightMarkup("Scope")),
                new Markup(themePalette.MutedMarkup(Scope(server))));

            table.AddRow(
                new Markup(string.Empty),
                new Markup(themePalette.HighlightMarkup("Transport")),
                new Markup(themePalette.MutedMarkup(Markup.Escape(server.Transport.ToString()))));

            table.AddRow(
                new Markup(string.Empty),
                new Markup(themePalette.HighlightMarkup("Trust")),
                new Markup(themePalette.MutedMarkup(Trust(server))));

            table.AddRow(
                new Markup(string.Empty),
                new Markup(themePalette.HighlightMarkup("Lifecycle")),
                new Markup(themePalette.TextMarkup(Markup.Escape(server.State.ToString()))));

            table.AddRow(
                new Markup(string.Empty),
                new Markup(themePalette.HighlightMarkup("Tools")),
                new Markup(themePalette.MutedMarkup(server.Tools.Length.ToString(CultureInfo.InvariantCulture))));

            table.AddRow(
                new Markup(string.Empty),
                new Markup(themePalette.HighlightMarkup("Last error")),
                new Markup(themePalette.MutedMarkup(Markup.Escape(server.ErrorMessage ?? "-"))));

        }

        AnsiConsole.Write(table);

        return 0;

    }

    public async Task<int> Show(
        string? identifier,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        ResourceSelectionResult<McpServerInfo> selection = await SelectServerAsync(
            identifier,
            workingDirectory,
            cancellationToken).ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return 0;

        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {

            return WriteError(selection.Error!);

        }

        McpServerInfo selected = selection.Value!;

        Result<McpServerInfo> result = await apiClient
            .GetMcpServerAsync(
                selected.Name,
                selected.WorkingDirectory,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WriteServer(result.Value);

        return 0;

    }

    public Task<int> Start(
        string? identifier,
        string? workingDirectory,
        CancellationToken cancellationToken) =>
        ChangeLifecycle(
            identifier,
            workingDirectory,
            "started",
            apiClient.StartMcpServerAsync,
            cancellationToken);

    public Task<int> Stop(
        string? identifier,
        string? workingDirectory,
        CancellationToken cancellationToken) =>
        ChangeLifecycle(
            identifier,
            workingDirectory,
            "stopped",
            apiClient.StopMcpServerAsync,
            cancellationToken);

    public Task<int> Restart(
        string? identifier,
        string? workingDirectory,
        CancellationToken cancellationToken) =>
        ChangeLifecycle(
            identifier,
            workingDirectory,
            "restarted",
            apiClient.RestartMcpServerAsync,
            cancellationToken);

    public async Task<int> Reload(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        Result<string> result = await apiClient
            .ReloadMcpAsync(
                new OptionalWorkspaceRequest(workingDirectory),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                "MCP reload:",
                Markup.Escape(result.Value)));

        return 0;

    }

    public async Task<int> Trust(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        string workspace = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory.Trim();

        Result<bool> result = await apiClient
            .TrustMcpWorkspaceAsync(
                new OptionalWorkspaceRequest(workspace),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                "Workspace MCP trusted:",
                Markup.Escape(workspace)));

        AnsiConsole.MarkupLine(
            themePalette.MutedMarkup(
                "Trust is bound to the current mcp.json bytes; changed configuration must be trusted again."));

        return 0;

    }

    public async Task<int> Tools(
        string? identifier,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        ResourceSelectionResult<McpServerInfo> selection = await SelectServerAsync(
            identifier,
            workingDirectory,
            cancellationToken).ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return 0;

        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {

            return WriteError(selection.Error!);

        }

        McpServerInfo server = selection.Value!;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Server"));

        table.AddColumn(themePalette.HeadingTableColumn("Tool"));

        table.AddColumn(themePalette.HeadingTableColumn("Scope"));

        foreach (string tool in server.Tools.Order(StringComparer.Ordinal))
        {

            table.AddRow(
                Markup.Escape(server.Name),
                Markup.Escape(tool),
                Scope(server));

        }

        AnsiConsole.Write(table);

        return 0;

    }

    public async Task<int> Invoke(
        string toolIdentifier,
        string? argumentSource,
        string? serverIdentifier,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        if (string.Equals(
                serverIdentifier?.Trim(),
                DiagnosticMcpInvocationService.InternalServerName,
                StringComparison.OrdinalIgnoreCase))
        {

            return WriteError(
                "The internal arcanum-internal server is not a diagnostic MCP target. Diagnostic MCP invocation is external-only; use 'arcanum tool invoke' for eligible built-in tools, while Forbidden Arts continue through the Master tool execution pipeline.");

        }

        if (!ToolArgumentReader.TryRead(
                argumentSource,
                out JsonElement arguments,
                out string? argumentError))
        {

            return WriteError(argumentError!);

        }

        if (DiagnosticMcpInvocationService.BlockedToolNames.Contains(
                toolIdentifier.Trim()))
        {

            Result<McpToolInvokeResponse> blocked = await apiClient
                .InvokeDiagnosticMcpToolAsync(
                    new McpToolInvokeRequest
                    {

                        ToolName = toolIdentifier.Trim(),

                        Arguments = arguments,

                        ServerName = serverIdentifier,

                        WorkingDirectory = workingDirectory,

                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return blocked.IsFailure
                ? WriteError(blocked.Error)
                : WriteError(
                    "The server unexpectedly allowed a Forbidden Art diagnostic invocation.");

        }

        Result<WorkspaceArsenalDto> arsenal = await apiClient
            .GetWorkspaceArsenalAsync(
                new OptionalWorkspaceRequest(workingDirectory),
                cancellationToken)
            .ConfigureAwait(false);

        if (arsenal.IsFailure)
        {

            return WriteError(arsenal.Error);

        }

        McpServerStatusDto[] externalServers = arsenal.Value.McpServers
            .Where(
                static server => !string.Equals(
                    server.ServerName,
                    DiagnosticMcpInvocationService.InternalServerName,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(serverIdentifier))
        {

            ResourceSelectionResult<McpServerStatusDto> serverSelection =
                await SelectDiagnosticServerAsync(
                    externalServers,
                    serverIdentifier,
                    cancellationToken).ConfigureAwait(false);

            if (serverSelection.Status == ResourceSelectionStatus.Cancelled)
            {

                return 0;

            }

            if (serverSelection.Status == ResourceSelectionStatus.Error)
            {

                return WriteError(serverSelection.Error!);

            }

            externalServers = [serverSelection.Value!];

        }

        DiagnosticTool[] tools = externalServers
            .Where(
                static server => string.Equals(
                    server.Status,
                    "running",
                    StringComparison.OrdinalIgnoreCase))
            .SelectMany(
                static server => server.ProvidedTools.Select(
                    tool => new DiagnosticTool(tool, server.ServerName)))
            .ToArray();

        ResourceSelectionResult<DiagnosticTool> toolSelection =
            await SelectDiagnosticToolAsync(
                tools,
                toolIdentifier,
                cancellationToken).ConfigureAwait(false);

        if (toolSelection.Status == ResourceSelectionStatus.Cancelled)
        {

            return 0;

        }

        if (toolSelection.Status == ResourceSelectionStatus.Error)
        {

            return WriteError(toolSelection.Error!);

        }

        DiagnosticTool selected = toolSelection.Value!;

        Result<McpToolInvokeResponse> result = await apiClient
            .InvokeDiagnosticMcpToolAsync(
                new McpToolInvokeRequest
                {

                    ToolName = selected.Name,

                    Arguments = arguments,

                    ServerName = selected.ServerName,

                    WorkingDirectory = workingDirectory,

                },
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        McpToolInvokeResponse response = result.Value;

        // Raw stdout: Spectre would render the document as a Text renderable and hard-wrap it at the
        // profile width, putting literal newlines inside JSON string literals.
        Console.Out.WriteLine(response.Result.GetRawText());

        AnsiConsole.MarkupLine(
            themePalette.MutedMarkup(
                $"Diagnostic MCP: {Markup.Escape(response.ToolName)} on {Markup.Escape(response.ServerName)}; {response.DurationMs.ToString(CultureInfo.InvariantCulture)}ms; truncated: {(response.Truncated ? "yes" : "no")}."));

        return 0;

    }

    private async Task<int> ChangeLifecycle(
        string? identifier,
        string? workingDirectory,
        string successVerb,
        Func<string, string?, CancellationToken, Task<Result<bool>>> action,
        CancellationToken cancellationToken)
    {

        ResourceSelectionResult<McpServerInfo> selection = await SelectServerAsync(
            identifier,
            workingDirectory,
            cancellationToken).ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return 0;

        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {

            return WriteError(selection.Error!);

        }

        McpServerInfo server = selection.Value!;

        Result<bool> result = await action(
            server.Name,
            server.WorkingDirectory,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                $"MCP server {successVerb}:",
                Markup.Escape(server.Name)));

        return 0;

    }

    private Task<ResourceSelectionResult<McpServerInfo>> SelectServerAsync(
        string? identifier,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        ResourceDescriptor<McpServerInfo> descriptor = new(
            "MCP server",
            ["Name", "Scope", "State", "Transport", "Tools"],
            static server => $"{server.Name}@{ScopeId(server)}",
            static server => server.Name,
            static server => $"scope {Scope(server)}, state {server.State}, transport {server.Transport}, {server.Tools.Length} tools",
            static server =>
            [

                server.Name,

                Scope(server),

                server.State.ToString(),

                server.Transport.ToString(),

                server.Tools.Length.ToString(CultureInfo.InvariantCulture),

            ]);

        return new ResourceSelector<McpServerInfo>(picker, recentStore)
            .SelectAsync(
                new ResourceSelectionRequest<McpServerInfo>(
                    "mcp-server",
                    identifier,
                    environment.IsInteractive,
                    descriptor,
                    async (_, ct) =>
                    {

                        Result<IReadOnlyList<McpServerInfo>> result = await apiClient
                            .GetMcpServersAsync(ct)
                            .ConfigureAwait(false);

                        if (result.IsFailure)
                        {

                            return Result<ResourcePage<McpServerInfo>>.Failure(result.Error);

                        }

                        IEnumerable<McpServerInfo> servers = result.Value;

                        if (!string.IsNullOrWhiteSpace(workingDirectory))
                        {

                            string normalized = NormalizeWorkspace(workingDirectory);

                            servers = servers.Where(
                                server => string.Equals(
                                    NormalizeWorkspace(server.WorkingDirectory),
                                    normalized,
                                    StringComparison.Ordinal));

                        }

                        return Result<ResourcePage<McpServerInfo>>.Success(
                            new ResourcePage<McpServerInfo>(servers.ToArray(), null));

                    },
                    PickAmbiguousIdentifiers: true),
                cancellationToken);

    }

    private Task<ResourceSelectionResult<McpServerStatusDto>> SelectDiagnosticServerAsync(
        IReadOnlyList<McpServerStatusDto> servers,
        string identifier,
        CancellationToken cancellationToken) =>
        new ResourceSelector<McpServerStatusDto>(picker, recentStore)
            .SelectAsync(
                new ResourceSelectionRequest<McpServerStatusDto>(
                    "diagnostic-mcp-server",
                    identifier,
                    environment.IsInteractive,
                    new ResourceDescriptor<McpServerStatusDto>(
                        "external MCP server",
                        ["Name", "State", "Tools"],
                        static server => server.ServerName,
                        static server => server.ServerName,
                        static server => $"state {server.Status}, {server.ToolCount} tools",
                        static server =>
                        [

                            server.ServerName,

                            server.Status,

                            server.ToolCount.ToString(CultureInfo.InvariantCulture),

                        ]),
                    (_, _) => Task.FromResult(
                        Result<ResourcePage<McpServerStatusDto>>.Success(
                            new ResourcePage<McpServerStatusDto>(servers, null))),
                    PickAmbiguousIdentifiers: true),
                cancellationToken);

    private Task<ResourceSelectionResult<DiagnosticTool>> SelectDiagnosticToolAsync(
        IReadOnlyList<DiagnosticTool> tools,
        string identifier,
        CancellationToken cancellationToken) =>
        new ResourceSelector<DiagnosticTool>(picker, recentStore)
            .SelectAsync(
                new ResourceSelectionRequest<DiagnosticTool>(
                    "diagnostic-mcp-tool",
                    identifier,
                    environment.IsInteractive,
                    new ResourceDescriptor<DiagnosticTool>(
                        "external MCP tool",
                        ["Tool", "Server"],
                        static tool => $"{tool.ServerName}/{tool.Name}",
                        static tool => tool.Name,
                        static tool => $"server {tool.ServerName}",
                        static tool => [tool.Name, tool.ServerName]),
                    (_, _) => Task.FromResult(
                        Result<ResourcePage<DiagnosticTool>>.Success(
                            new ResourcePage<DiagnosticTool>(tools, null))),
                    PickAmbiguousIdentifiers: true),
                cancellationToken);

    private void WriteServer(McpServerInfo server)
    {

        Table table = new Table().Border(TableBorder.None).HideHeaders();

        table.AddColumn(string.Empty);

        table.AddColumn(string.Empty);

        table.AddRow("Name:", Markup.Escape(server.Name));

        table.AddRow("Scope:", Scope(server));

        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {

            table.AddRow("Workspace:", Markup.Escape(server.WorkingDirectory));

        }

        table.AddRow("Transport:", Markup.Escape(server.Transport.ToString()));

        table.AddRow("Trust:", Trust(server));

        table.AddRow("Lifecycle:", Markup.Escape(server.State.ToString()));

        table.AddRow("Always on:", server.AlwaysOn ? "yes" : "no");

        table.AddRow("Tool count:", server.Tools.Length.ToString(CultureInfo.InvariantCulture));

        table.AddRow(
            "Tools:",
            Markup.Escape(
                server.Tools.Length == 0
                    ? "(none)"
                    : string.Join(", ", server.Tools)));

        table.AddRow("Last error:", Markup.Escape(server.ErrorMessage ?? "-"));

        AnsiConsole.Write(table);

    }

    private static string Scope(McpServerInfo server) =>
        string.IsNullOrWhiteSpace(server.WorkingDirectory)
            ? "global"
            : "workspace";

    private static string ScopeId(McpServerInfo server) =>
        string.IsNullOrWhiteSpace(server.WorkingDirectory)
            ? "global"
            : NormalizeWorkspace(server.WorkingDirectory);

    private static string Trust(McpServerInfo server) =>
        string.IsNullOrWhiteSpace(server.WorkingDirectory)
            ? "not required"
            : "trusted";

    private static string NormalizeWorkspace(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim();

    private int WriteError(Error error) =>
        WriteError($"{error.Code}: {error.Message}");

    private int WriteError(string error)
    {

        AnsiConsole.MarkupLine(
            themePalette.ErrorMarkup(Markup.Escape(error)));

        return 1;

    }

    private sealed record DiagnosticTool(
        string Name,
        string ServerName);

}
