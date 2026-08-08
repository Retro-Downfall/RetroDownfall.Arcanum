using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class ToolCommands(
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

        Result<WorkspaceArsenalDto> result = await GetArsenalAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Tool"));

        table.AddColumn(themePalette.HeadingTableColumn("Type"));

        table.AddColumn(themePalette.HeadingTableColumn("Diagnostic policy"));

        foreach (string tool in result.Value.NativeTools.Order(StringComparer.Ordinal))
        {

            table.AddRow(
                Markup.Escape(tool),
                "built-in",
                "direct API invocation");

        }

        AnsiConsole.Write(table);

        return 0;

    }

    public async Task<int> Show(
        string toolIdentifier,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        ResourceSelectionResult<BuiltInTool> selection = await SelectToolAsync(
            toolIdentifier,
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

        Table table = new Table().Border(TableBorder.None).HideHeaders();

        table.AddColumn(string.Empty);

        table.AddColumn(string.Empty);

        table.AddRow("Name:", Markup.Escape(selection.Value!.Name));

        table.AddRow("Type:", "built-in");

        table.AddRow("Scope:", string.IsNullOrWhiteSpace(workingDirectory) ? "global" : "workspace");

        table.AddRow("Invocation:", "authenticated built-in diagnostic API");

        table.AddRow("MCP policy:", "not an external MCP diagnostic invocation");

        AnsiConsole.Write(table);

        return 0;

    }

    public async Task<int> Invoke(
        string toolIdentifier,
        string? argumentSource,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        if (!ToolArgumentReader.TryRead(
                argumentSource,
                out JsonElement arguments,
                out string? argumentError))
        {

            return WriteError(argumentError!);

        }

        ResourceSelectionResult<BuiltInTool> selection = await SelectToolAsync(
            toolIdentifier,
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

        Result<RetroDownfall.Arcanum.Api.Models.ToolInvokeResponse> result =
            await apiClient.InvokeToolAsync(
                selection.Value!.Name,
                arguments,
                cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        // Raw stdout: Spectre would render the document as a Text renderable and hard-wrap it at the
        // profile width, putting literal newlines inside JSON string literals.
        Console.Out.WriteLine(result.Value.Result.GetRawText());

        return 0;

    }

    private async Task<ResourceSelectionResult<BuiltInTool>> SelectToolAsync(
        string identifier,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        Result<WorkspaceArsenalDto> arsenal = await GetArsenalAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);

        if (arsenal.IsFailure)
        {

            return ResourceSelectionResult<BuiltInTool>.Failure(
                $"{arsenal.Error.Code}: {arsenal.Error.Message}");

        }

        BuiltInTool[] tools = arsenal.Value.NativeTools
            .Select(static name => new BuiltInTool(name))
            .ToArray();

        return await new ResourceSelector<BuiltInTool>(picker, recentStore)
            .SelectAsync(
                new ResourceSelectionRequest<BuiltInTool>(
                    "built-in-tool",
                    identifier,
                    environment.IsInteractive,
                    new ResourceDescriptor<BuiltInTool>(
                        "built-in tool",
                        ["Tool", "Type"],
                        static tool => tool.Name,
                        static tool => tool.Name,
                        static _ => "built-in diagnostic tool",
                        static tool => [tool.Name, "built-in"]),
                    (_, _) => Task.FromResult(
                        Result<ResourcePage<BuiltInTool>>.Success(
                            new ResourcePage<BuiltInTool>(tools, null))),
                    PickAmbiguousIdentifiers: true),
                cancellationToken)
            .ConfigureAwait(false);

    }

    private Task<Result<WorkspaceArsenalDto>> GetArsenalAsync(
        string? workingDirectory,
        CancellationToken cancellationToken) =>
        apiClient.GetWorkspaceArsenalAsync(
            new OptionalWorkspaceRequest(workingDirectory),
            cancellationToken);

    private int WriteError(Error error) =>
        WriteError($"{error.Code}: {error.Message}");

    private int WriteError(string error)
    {

        AnsiConsole.MarkupLine(
            themePalette.ErrorMarkup(Markup.Escape(error)));

        return 1;

    }

    private sealed record BuiltInTool(string Name);

}
