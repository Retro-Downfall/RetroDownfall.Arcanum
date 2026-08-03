using System.Globalization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Workspaces;

using Spectre.Console;


namespace RetroDownfall.Arcanum.Cli.Commands.Configuration;


public sealed class WorkspaceCommands(
    ArcanumApiClient apiClient,
    ICliResourceCatalog resourceCatalog,
    ICliContextStore contextStore,
    IThemePalette themePalette)
{

    public async Task<int> List(CancellationToken cancellationToken)
    {

        Result<WorkspaceInfo[]> result = await apiClient
            .GetWorkspacesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Id"));

        table.AddColumn(themePalette.HeadingTableColumn("Name"));

        table.AddColumn(themePalette.HeadingTableColumn("Server path"));

        table.AddColumn(themePalette.HeadingTableColumn("Type"));

        foreach (WorkspaceInfo workspace in result.Value)
        {

            table.AddRow(
                new Markup(themePalette.MutedMarkup(Markup.Escape(workspace.Id))),
                new Markup(themePalette.TextMarkup(Markup.Escape(workspace.Name))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(workspace.Path))),
                new Markup(themePalette.TextMarkup(Markup.Escape(workspace.Type.ToString()))));

        }

        AnsiConsole.Write(table);

        return 0;

    }


    public async Task<int> Current(CancellationToken cancellationToken)
    {

        Result<WorkspaceInfo[]> workspaces = await apiClient
            .GetWorkspacesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (workspaces.IsFailure)
        {

            return WriteError(workspaces.Error);

        }

        Result<CampaignDto[]> campaigns = await GetAllCampaignsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (campaigns.IsFailure)
        {

            return WriteError(campaigns.Error);

        }

        string currentDirectory = Path.GetFullPath(Environment.CurrentDirectory);

        WorkspaceInfo? workspace = FindContaining(
            currentDirectory,
            workspaces.Value,
            static value => value.Path);

        CampaignDto? campaign = FindContaining(
            currentDirectory,
            campaigns.Value,
            static value => value.Path);

        Table table = DetailTable();

        table.AddRow("Client path:", Markup.Escape(currentDirectory));

        table.AddRow(
            "Workspace:",
            workspace is null
                ? "(not registered)"
                : Markup.Escape($"{workspace.Name} [{workspace.Id}] -> {workspace.Path}"));

        table.AddRow(
            "Campaign:",
            campaign is null
                ? "(not registered)"
                : Markup.Escape($"{campaign.Name} [{campaign.Id:D}] -> {campaign.Path}"));

        table.AddRow(
            "Path semantics:",
            "Paths are resolved and allowlisted on the server host.");

        AnsiConsole.Write(table);

        if (workspace is null)
        {

            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(
                    "Register this directory with [bold]arcanum workspace register[/]."));

        }

        if (campaign is null)
        {

            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(
                    "Campaign operations need a persistent project container. Register one with [bold]arcanum campaign create --name <name> --path <server-path>[/]."));

        }

        return 0;

    }


    public async Task<int> Register(
        string? path,
        string? name,
        string? type,
        CancellationToken cancellationToken)
    {

        string serverPath = string.IsNullOrWhiteSpace(path)
            ? Environment.CurrentDirectory
            : path.Trim();

        if (!TryParseWorkspaceType(type, out WorkspaceType workspaceType))
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(
                    "--type must be one of: spell, campaign, data, custom."));

            return 1;

        }

        string workspaceName = string.IsNullOrWhiteSpace(name)
            ? InferName(serverPath)
            : name.Trim();

        Result<WorkspaceInfo> result = await apiClient
            .RegisterWorkspaceAsync(
                new CreateWorkspaceRequest(
                    workspaceName,
                    serverPath,
                    workspaceType),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                "Workspace registered:",
                Markup.Escape($"{result.Value.Name} [{result.Value.Id}] -> {result.Value.Path}")));

        return 0;

    }


    public async Task<int> Show(
        string? identifier,
        CancellationToken cancellationToken)
    {

        WorkspaceResolution resolution = await ResolveWorkspaceAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.IsSuccess)
        {

            return resolution.ExitCode;

        }

        Result<WorkspaceInfo> result = await apiClient
            .GetWorkspaceAsync(
                resolution.Workspace!.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WriteWorkspace(result.Value);

        return 0;

    }


    public async Task<int> Tree(
        string? identifier,
        string? relativePath,
        CancellationToken cancellationToken)
    {

        WorkspaceResolution resolution = await ResolveWorkspaceAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.IsSuccess)
        {

            return resolution.ExitCode;

        }

        string? cursor = null;

        do
        {

            Result<FileListResult> result = await apiClient
                .ListWorkspaceFilesAsync(
                    resolution.Workspace!.Id,
                    relativePath,
                    recursive: true,
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {

                return WriteError(result.Error);

            }

            Table table = new();

            table.AddColumn(themePalette.HeadingTableColumn("Path"));

            table.AddColumn(themePalette.HeadingTableColumn("Type"));

            table.AddColumn(themePalette.HeadingTableColumn("Bytes"));

            foreach (FileEntry entry in result.Value.Entries)
            {

                table.AddRow(
                    new Markup(themePalette.TextMarkup(Markup.Escape(entry.RelativePath))),
                    new Markup(themePalette.MutedMarkup(Markup.Escape(entry.Type.ToString()))),
                    new Markup(themePalette.MutedMarkup(entry.Size.ToString(CultureInfo.InvariantCulture))));

            }

            AnsiConsole.Write(table);

            cursor = result.Value.NextCursor;

        } while (cursor is not null);

        return 0;

    }


    public async Task<int> Info(
        string relativePath,
        string? identifier,
        CancellationToken cancellationToken)
    {

        WorkspaceResolution resolution = await ResolveWorkspaceAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.IsSuccess)
        {

            return resolution.ExitCode;

        }

        Result<FileEntry> result = await apiClient
            .GetWorkspaceFileInfoAsync(
                resolution.Workspace!.Id,
                relativePath,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        FileEntry entry = result.Value;

        Table table = DetailTable();

        table.AddRow("Name:", Markup.Escape(entry.Name));

        table.AddRow("Relative path:", Markup.Escape(entry.RelativePath));

        table.AddRow("Server path:", Markup.Escape(entry.FullPath));

        table.AddRow("Type:", Markup.Escape(entry.Type.ToString()));

        table.AddRow("Bytes:", entry.Size.ToString(CultureInfo.InvariantCulture));

        table.AddRow(
            "Modified:",
            Markup.Escape(
                entry.LastModified.ToString(
                    "u",
                    CultureInfo.InvariantCulture)));

        AnsiConsole.Write(table);

        return 0;

    }


    public async Task<int> Read(
        string relativePath,
        string? identifier,
        CancellationToken cancellationToken)
    {

        WorkspaceResolution resolution = await ResolveWorkspaceAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.IsSuccess)
        {

            return resolution.ExitCode;

        }

        Result<FileReadResult> result = await apiClient
            .ReadWorkspaceFileAsync(
                resolution.Workspace!.Id,
                relativePath,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        AnsiConsole.Write(new Text(result.Value.Content));

        return 0;

    }


    public async Task<int> Search(
        string query,
        string? identifier,
        int? limit,
        CancellationToken cancellationToken)
    {

        WorkspaceResolution resolution = await ResolveWorkspaceAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.IsSuccess)
        {

            return resolution.ExitCode;

        }

        Result<WorkspaceSearchResult[]> result = await apiClient
            .SearchWorkspaceAsync(
                resolution.Workspace!.Id,
                new WorkspaceSemanticSearchRequest(query, limit),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Path"));

        table.AddColumn(themePalette.HeadingTableColumn("Chunk"));

        table.AddColumn(themePalette.HeadingTableColumn("Similarity"));

        table.AddColumn(themePalette.HeadingTableColumn("Preview"));

        foreach (WorkspaceSearchResult match in result.Value)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(match.RelativePath))),
                new Markup(themePalette.MutedMarkup($"{match.ChunkIndex + 1}/{match.TotalChunks}")),
                new Markup(themePalette.MutedMarkup(match.Similarity.ToString("0.000", CultureInfo.InvariantCulture))),
                new Markup(themePalette.TextMarkup(Markup.Escape(match.ContentPreview))));

        }

        AnsiConsole.Write(table);

        return 0;

    }


    public async Task<int> Index(
        string? identifier,
        CancellationToken cancellationToken)
    {

        WorkspaceResolution resolution = await ResolveWorkspaceAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.IsSuccess)
        {

            return resolution.ExitCode;

        }

        Result<bool> result = await apiClient
            .IndexWorkspaceAsync(
                resolution.Workspace!.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                "Index requested:",
                Markup.Escape(resolution.Workspace.Name)));

        return 0;

    }


    public async Task<int> IndexStatus(
        string? identifier,
        CancellationToken cancellationToken)
    {

        WorkspaceResolution resolution = await ResolveWorkspaceAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.IsSuccess)
        {

            return resolution.ExitCode;

        }

        Result<WorkspaceIndexStatusDto> result = await apiClient
            .GetWorkspaceIndexStatusAsync(
                resolution.Workspace!.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WorkspaceIndexStatusDto status = result.Value;

        Table table = DetailTable();

        table.AddRow("Workspace:", Markup.Escape(status.WorkspaceName));

        table.AddRow("Server path:", Markup.Escape(status.WorkspacePath));

        table.AddRow("Indexing enabled:", status.IndexingEnabled ? "yes" : "no");

        table.AddRow("Indexed files:", status.TotalIndexedFiles.ToString(CultureInfo.InvariantCulture));

        table.AddRow("Chunks:", status.TotalChunks.ToString(CultureInfo.InvariantCulture));

        table.AddRow("Vector mode:", Markup.Escape(status.VectorMode));

        table.AddRow("Watching:", status.Watching ? "yes" : "no");

        table.AddRow("Degraded:", status.Degraded ? "yes" : "no");

        table.AddRow("Reconciling:", status.Reconciling ? "yes" : "no");

        table.AddRow("Skipped files:", Markup.Escape(status.SkippedFilesNote));

        AnsiConsole.Write(table);

        return 0;

    }


    public async Task<int> Chunks(
        string? identifier,
        string? relativePath,
        int? limit,
        int? offset,
        CancellationToken cancellationToken)
    {

        WorkspaceResolution resolution = await ResolveWorkspaceAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.IsSuccess)
        {

            return resolution.ExitCode;

        }

        Result<WorkspaceFileChunkPage> result = await apiClient
            .GetWorkspaceChunksAsync(
                resolution.Workspace!.Id,
                relativePath,
                limit,
                offset,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn("Path"));

        table.AddColumn(themePalette.HeadingTableColumn("Chunk"));

        table.AddColumn(themePalette.HeadingTableColumn("Lines"));

        table.AddColumn(themePalette.HeadingTableColumn("Preview"));

        foreach (WorkspaceFileChunkDto chunk in result.Value.Chunks)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(chunk.RelativePath))),
                new Markup(themePalette.MutedMarkup($"{chunk.ChunkIndex + 1}/{chunk.TotalChunksForFile}")),
                new Markup(themePalette.MutedMarkup($"{chunk.StartLine}-{chunk.EndLine}")),
                new Markup(themePalette.TextMarkup(Markup.Escape(chunk.ContentPreview))));

        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            themePalette.MutedMarkup(
                Markup.Escape(
                    $"Showing {result.Value.Chunks.Length} of {result.Value.Total} chunks from offset {result.Value.Offset}.")));

        return 0;

    }


    public async Task<int> Unregister(
        string? identifier,
        CancellationToken cancellationToken)
    {

        WorkspaceResolution resolution = await ResolveWorkspaceAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.IsSuccess)
        {

            return resolution.ExitCode;

        }

        Result result = await apiClient
            .UnregisterWorkspaceAsync(
                resolution.Workspace!.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                "Workspace unregistered:",
                Markup.Escape(resolution.Workspace.Name)));

        return 0;

    }


    private async Task<WorkspaceResolution> ResolveWorkspaceAsync(
        string? identifier,
        CancellationToken cancellationToken)
    {

        string? effectiveIdentifier = identifier;

        if (string.IsNullOrWhiteSpace(effectiveIdentifier)
            && !CliInvocationContext.Current.NoContext)
        {

            effectiveIdentifier = contextStore.Load().WorkspaceId;

        }

        if (string.IsNullOrWhiteSpace(effectiveIdentifier))
        {

            Result<WorkspaceInfo[]> workspaces = await apiClient
                .GetWorkspacesAsync(cancellationToken)
                .ConfigureAwait(false);

            if (workspaces.IsFailure)
            {

                return WorkspaceResolution.Failure(
                    WriteError(workspaces.Error));

            }

            WorkspaceInfo? detected = FindContaining(
                Path.GetFullPath(Environment.CurrentDirectory),
                workspaces.Value,
                static value => value.Path);

            if (detected is not null)
            {

                return WorkspaceResolution.Success(detected);

            }

        }

        ResourceSelectionResult<WorkspaceInfo> selection = await resourceCatalog
            .SelectWorkspaceAsync(effectiveIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return WorkspaceResolution.Failure(0);

        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape(selection.Error!)));

            return WorkspaceResolution.Failure(1);

        }

        return WorkspaceResolution.Success(selection.Value!);

    }


    private async Task<Result<CampaignDto[]>> GetAllCampaignsAsync(
        CancellationToken cancellationToken)
    {

        List<CampaignDto> campaigns = [];

        int offset = 0;

        while (true)
        {

            Result<ListPageResult<CampaignDto>> result = await apiClient
                .GetCampaignsPageAsync(
                    null,
                    100,
                    offset,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {

                return Result<CampaignDto[]>.Failure(result.Error);

            }

            campaigns.AddRange(result.Value.Items);

            if (!result.Value.HasMore
                || result.Value.NextOffset is not { } nextOffset)
            {

                return Result<CampaignDto[]>.Success([.. campaigns]);

            }

            offset = nextOffset;

        }

    }


    private void WriteWorkspace(WorkspaceInfo workspace)
    {

        Table table = DetailTable();

        table.AddRow("Id:", Markup.Escape(workspace.Id));

        table.AddRow("Name:", Markup.Escape(workspace.Name));

        table.AddRow("Server path:", Markup.Escape(workspace.Path));

        table.AddRow("Type:", Markup.Escape(workspace.Type.ToString()));

        table.AddRow(
            "Registered:",
            Markup.Escape(
                workspace.RegisteredAt.ToString(
                    "u",
                    CultureInfo.InvariantCulture)));

        AnsiConsole.Write(table);

    }


    private int WriteError(Error error)
    {

        AnsiConsole.MarkupLine(themePalette.ErrorMarkup(error));

        return 1;

    }


    private static T? FindContaining<T>(
        string candidatePath,
        IEnumerable<T> values,
        Func<T, string> getPath)
        where T : class =>
        values
            .Where(value => CliContextService.IsWithin(
                candidatePath,
                getPath(value)))
            .OrderByDescending(value => getPath(value).Length)
            .FirstOrDefault();


    private static bool TryParseWorkspaceType(
        string? value,
        out WorkspaceType type)
    {

        if (string.IsNullOrWhiteSpace(value))
        {

            type = WorkspaceType.Custom;

            return true;

        }

        return Enum.TryParse(value, ignoreCase: true, out type)
            && Enum.IsDefined(type);

    }


    private static string InferName(string path)
    {

        string trimmed = path.TrimEnd('/', '\\');

        int separator = Math.Max(
            trimmed.LastIndexOf('/'),
            trimmed.LastIndexOf('\\'));

        string name = separator >= 0
            ? trimmed[(separator + 1)..]
            : trimmed;

        return string.IsNullOrWhiteSpace(name)
            ? "workspace"
            : name;

    }


    private static Table DetailTable()
    {

        Table table = new Table().Border(TableBorder.None).HideHeaders();

        table.AddColumn(string.Empty);

        table.AddColumn(string.Empty);

        return table;

    }


    private sealed record WorkspaceResolution(
        WorkspaceInfo? Workspace,
        int ExitCode)
    {

        public bool IsSuccess => Workspace is not null;

        public static WorkspaceResolution Success(WorkspaceInfo workspace) =>
            new(workspace, 0);

        public static WorkspaceResolution Failure(int exitCode) =>
            new(null, exitCode);

    }

}
