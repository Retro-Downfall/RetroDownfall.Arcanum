using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Cli.Services;

public interface ICliResourceCatalog
{
    Task<ResourceSelectionResult<CampaignDto>> SelectCampaignAsync(string? identifier, CancellationToken cancellationToken);

    Task<ResourceSelectionResult<SessionSummaryDto>> SelectSessionAsync(string? identifier, CancellationToken cancellationToken);

    Task<ResourceSelectionResult<EntryDto>> SelectSessionEntryAsync(
        Guid sessionId,
        string? identifier,
        CancellationToken cancellationToken);

    Task<ResourceSelectionResult<WorkspaceInfo>> SelectWorkspaceAsync(string? identifier, CancellationToken cancellationToken);

    Task<ResourceSelectionResult<PromptSummaryDto>> SelectPromptAsync(string? identifier, CancellationToken cancellationToken);

    Task<ResourceSelectionResult<SpellSummary>> SelectSpellAsync(string? identifier, string? workspace, CancellationToken cancellationToken);

    Task<ResourceSelectionResult<ApprenticeSummaryDto>> SelectApprenticeAsync(string? identifier, CancellationToken cancellationToken);

    Task<ResourceSelectionResult<ModelInfoDto>> SelectModelAsync(string? identifier, CancellationToken cancellationToken);

    Task<ResourceSelectionResult<ProviderInfoDto>> SelectProviderAsync(string? identifier, CancellationToken cancellationToken);

    Task<ResourceSelectionResult<McpServerInfo>> SelectMcpServerAsync(string? identifier, CancellationToken cancellationToken);
}

/// <summary>
/// Resource-specific presentation and paging over existing HTTP list APIs. Descriptors deliberately
/// omit provider endpoints, credential names, and MCP command/URL/argument details.
/// </summary>
public sealed class CliResourceCatalog(
    ArcanumApiClient apiClient,
    ICliEnvironment environment,
    IResourcePicker picker,
    IRecentResourceStore recentStore) : ICliResourceCatalog
{
    private const int PageSize = 100;

    public async Task<ResourceSelectionResult<CampaignDto>> SelectCampaignAsync(
        string? identifier,
        CancellationToken cancellationToken)
    {

        ResourceSelectionResult<CampaignDto> result = await SelectAsync(
            new ResourceSelectionRequest<CampaignDto>(
                "campaign",
                identifier,
                environment.IsInteractive,
                new ResourceDescriptor<CampaignDto>(
                    "campaign",
                    ["Name", "Path"],
                    static value => value.Id.ToString("D"),
                    static value => value.Name,
                    static value => $"{value.Path} ({value.Type})",
                    static value => [value.Name, value.Path]),
                async (token, ct) =>
                {
                    int offset = ParseOffset(token);
                    Result<ListPageResult<CampaignDto>> result = await apiClient
                        .GetCampaignsPageAsync(null, PageSize, offset, ct)
                        .ConfigureAwait(false);
                    return ToPage(result, static page => page.NextOffset?.ToString(CultureInfo.InvariantCulture));
                }),
            cancellationToken).ConfigureAwait(false);

        if (result.Status != ResourceSelectionStatus.Error
            || !string.IsNullOrWhiteSpace(identifier))
        {

            return result;

        }

        string? advice = await GetCampaignRegistrationAdviceAsync(cancellationToken)
            .ConfigureAwait(false);

        return advice is null
            ? result
            : ResourceSelectionResult<CampaignDto>.Failure(
                $"{result.Error} {advice}");

    }

    private async Task<string?> GetCampaignRegistrationAdviceAsync(
        CancellationToken cancellationToken)
    {

        string currentDirectory = Path.GetFullPath(Environment.CurrentDirectory);

        Result<WorkspaceInfo[]> workspaces = await apiClient
            .GetWorkspacesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (workspaces.IsFailure)
        {

            return null;

        }

        WorkspaceInfo? workspace = workspaces.Value
            .Where(item => CliContextService.IsWithin(
                currentDirectory,
                item.Path))
            .OrderByDescending(item => item.Path.Length)
            .FirstOrDefault();

        if (workspace is null)
        {

            return null;

        }

        int offset = 0;

        for (int page = 0; page < 100; page++)
        {

            Result<ListPageResult<CampaignDto>> campaigns = await apiClient
                .GetCampaignsPageAsync(
                    null,
                    PageSize,
                    offset,
                    cancellationToken)
                .ConfigureAwait(false);

            if (campaigns.IsFailure)
            {

                return null;

            }

            if (campaigns.Value.Items.Any(
                    item => CliContextService.IsWithin(
                        currentDirectory,
                        item.Path)))
            {

                return null;

            }

            if (!campaigns.Value.HasMore
                || campaigns.Value.NextOffset is not { } nextOffset)
            {

                return $"This server path is registered as Workspace '{workspace.Name}' but no Campaign contains it. Register a Campaign with: arcanum campaign create --name <name> --path <server path>.";

            }

            offset = nextOffset;

        }

        return null;

    }

    public Task<ResourceSelectionResult<SessionSummaryDto>> SelectSessionAsync(
        string? identifier,
        CancellationToken cancellationToken) =>
        SelectAsync(
            new ResourceSelectionRequest<SessionSummaryDto>(
                "session",
                identifier,
                environment.IsInteractive,
                new ResourceDescriptor<SessionSummaryDto>(
                    "session",
                    ["Title", "Campaign", "Updated"],
                    static value => value.Id.ToString("D"),
                    static value => string.IsNullOrWhiteSpace(value.Title) ? "(untitled)" : value.Title,
                    static value => $"campaign {value.CampaignId?.ToString("D") ?? "-"}, updated {value.UpdatedAt:u}",
                    static value =>
                    [
                        string.IsNullOrWhiteSpace(value.Title) ? "(untitled)" : value.Title,
                        value.CampaignId?.ToString("D") ?? "-",
                        value.UpdatedAt.ToString("u", CultureInfo.InvariantCulture),
                    ]),
                async (token, ct) =>
                {
                    DateTimeOffset? before = ParseTimestamp(token);

                    Result<SessionQueryResult> result = await apiClient
                        .QuerySessionsAsync(
                            new SessionQueryRequest(
                                Status: "all",
                                Limit: PageSize,
                                BeforeUpdatedAt: before),
                            ct)
                        .ConfigureAwait(false);
                    if (result.IsFailure)
                    {
                        return Result<ResourcePage<SessionSummaryDto>>.Failure(result.Error);
                    }

                    SessionQueryResult page = result.Value;
                    string? next = page.HasMore ? page.NextBeforeUpdatedAt?.ToString("O") : null;
                    return Result<ResourcePage<SessionSummaryDto>>.Success(new(page.Summaries, next));
                }),
            cancellationToken);

    public Task<ResourceSelectionResult<EntryDto>> SelectSessionEntryAsync(
        Guid sessionId,
        string? identifier,
        CancellationToken cancellationToken) =>
        SelectAsync(
            new ResourceSelectionRequest<EntryDto>(
                "session-entry",
                identifier,
                environment.IsInteractive,
                new ResourceDescriptor<EntryDto>(
                    "session entry",
                    ["Role", "Created", "Content"],
                    static value => value.Id.ToString("D"),
                    static value => EntryLabel(value),
                    static value => $"{value.Role}, created {value.CreatedAt:u}",
                    static value =>
                    [
                        value.Role,
                        value.CreatedAt.ToString("u", CultureInfo.InvariantCulture),
                        EntryLabel(value),
                    ]),
                async (token, ct) =>
                {
                    int offset = ParseOffset(token);

                    Result<EntryDto[]> result = await apiClient
                        .GetSessionEntriesAsync(sessionId, offset, PageSize, ct)
                        .ConfigureAwait(false);

                    if (result.IsFailure)
                    {
                        return Result<ResourcePage<EntryDto>>.Failure(result.Error);
                    }

                    EntryDto[] entries = result.Value;

                    string? next = entries.Length == PageSize
                        ? (offset + PageSize).ToString(CultureInfo.InvariantCulture)
                        : null;

                    return Result<ResourcePage<EntryDto>>.Success(new(entries, next));
                }),
            cancellationToken);

    public Task<ResourceSelectionResult<WorkspaceInfo>> SelectWorkspaceAsync(
        string? identifier,
        CancellationToken cancellationToken) =>
        SelectSinglePageAsync(
            "workspace",
            identifier,
            new ResourceDescriptor<WorkspaceInfo>(
                "workspace",
                ["Name", "Path"],
                static value => value.Id,
                static value => value.Name,
                static value => $"{value.Path} ({value.Type})",
                static value => [value.Name, value.Path]),
            apiClient.GetWorkspacesAsync,
            cancellationToken);

    private static string EntryLabel(EntryDto entry)
    {
        string content = entry.Content.ReplaceLineEndings(" ").Trim();

        return content.Length switch
        {
            0 => "(empty entry)",
            > 80 => content[..77] + "...",
            _ => content,
        };
    }

    public Task<ResourceSelectionResult<PromptSummaryDto>> SelectPromptAsync(
        string? identifier,
        CancellationToken cancellationToken) =>
        SelectAsync(
            new ResourceSelectionRequest<PromptSummaryDto>(
                "prompt",
                identifier,
                environment.IsInteractive,
                new ResourceDescriptor<PromptSummaryDto>(
                    "prompt",
                    ["Name", "Version"],
                    static value => value.Id.ToString("D"),
                    static value => value.Name,
                    static value => $"version {value.Version}, updated {value.UpdatedAt:u}",
                    static value => [value.Name, value.Version]),
                async (token, ct) =>
                {
                    int offset = ParseOffset(token);
                    Result<ListPageResult<PromptSummaryDto>> result = await apiClient
                        .GetPromptsAsync(limit: PageSize, offset: offset, cancellationToken: ct)
                        .ConfigureAwait(false);
                    return ToPage(result, static page => page.NextOffset?.ToString(CultureInfo.InvariantCulture));
                }),
            cancellationToken);

    public Task<ResourceSelectionResult<SpellSummary>> SelectSpellAsync(
        string? identifier,
        string? workspace,
        CancellationToken cancellationToken) =>
        SelectSinglePageAsync(
            "spell",
            identifier,
            new ResourceDescriptor<SpellSummary>(
                "spell",
                ["Name", "Source"],
                static value => value.Name,
                static value => value.Name,
                static value => $"source {value.Source}, version {value.Version ?? "-"}",
                static value => [value.Name, value.Source.ToString()]),
            ct => apiClient.GetSpellsAsync(workspace, ct),
            cancellationToken);

    public Task<ResourceSelectionResult<ApprenticeSummaryDto>> SelectApprenticeAsync(
        string? identifier,
        CancellationToken cancellationToken) =>
        SelectAsync(
            new ResourceSelectionRequest<ApprenticeSummaryDto>(
                "apprentice",
                identifier,
                environment.IsInteractive,
                new ResourceDescriptor<ApprenticeSummaryDto>(
                    "apprentice",
                    ["Name", "Status"],
                    static value => value.Id.ToString("D"),
                    static value => value.Name,
                    static value => $"status {value.Status}, updated {value.UpdatedAt:u}",
                    static value => [value.Name, value.Status]),
                async (token, ct) =>
                {
                    DateTimeOffset? before = ParseTimestamp(token);
                    Result<ListPageResult<ApprenticeSummaryDto>> result = await apiClient
                        .GetApprenticesAsync(limit: PageSize, beforeUpdatedAt: before, cancellationToken: ct)
                        .ConfigureAwait(false);
                    return ToPage(result, static page => page.NextBeforeUpdatedAt?.ToString("O"));
                }),
            cancellationToken);

    public Task<ResourceSelectionResult<ModelInfoDto>> SelectModelAsync(
        string? identifier,
        CancellationToken cancellationToken) =>
        SelectSinglePageAsync(
            "model",
            identifier,
            new ResourceDescriptor<ModelInfoDto>(
                "model",
                ["Model", "Provider"],
                static value => $"{value.ProviderName}/{value.Model}",
                static value => value.Model,
                static value => $"provider {value.ProviderName}, type {value.ProviderType}",
                static value => [value.Model, value.ProviderName]),
            apiClient.GetModelsAsync,
            cancellationToken);

    public Task<ResourceSelectionResult<ProviderInfoDto>> SelectProviderAsync(
        string? identifier,
        CancellationToken cancellationToken) =>
        SelectSinglePageAsync(
            "provider",
            identifier,
            new ResourceDescriptor<ProviderInfoDto>(
                "provider",
                ["Name", "Type"],
                static value => value.Name,
                static value => value.Name,
                static value => $"type {value.Type}, {value.Models.Length} models",
                static value => [value.Name, value.Type]),
            apiClient.GetProvidersAsync,
            cancellationToken);

    public Task<ResourceSelectionResult<McpServerInfo>> SelectMcpServerAsync(
        string? identifier,
        CancellationToken cancellationToken) =>
        SelectSinglePageAsync(
            "mcp-server",
            identifier,
            new ResourceDescriptor<McpServerInfo>(
                "MCP server",
                ["Name", "Scope", "State", "Transport", "Tools"],
                McpSelectionId,
                static value => value.Name,
                static value => $"scope {McpScopeLabel(value)}, state {value.State}, transport {value.Transport}, {value.Tools.Length} tools",
                static value => [value.Name, McpScopeLabel(value), value.State.ToString(), value.Transport.ToString(), value.Tools.Length.ToString(CultureInfo.InvariantCulture)]),
            async ct =>
            {
                Result<IReadOnlyList<McpServerInfo>> result = await apiClient.GetMcpServersAsync(ct).ConfigureAwait(false);
                return result.IsSuccess
                    ? Result<McpServerInfo[]>.Success(result.Value.ToArray())
                    : Result<McpServerInfo[]>.Failure(result.Error);
            },
            cancellationToken,
            pickAmbiguousIdentifiers: true);

    private Task<ResourceSelectionResult<T>> SelectSinglePageAsync<T>(
        string resourceKind,
        string? identifier,
        ResourceDescriptor<T> descriptor,
        Func<CancellationToken, Task<Result<T[]>>> fetch,
        CancellationToken cancellationToken,
        bool pickAmbiguousIdentifiers = false)
        where T : class =>
        SelectAsync(
            new ResourceSelectionRequest<T>(
                resourceKind,
                identifier,
                environment.IsInteractive,
                descriptor,
                async (_, ct) =>
                {
                    Result<T[]> result = await fetch(ct).ConfigureAwait(false);
                    return result.IsSuccess
                        ? Result<ResourcePage<T>>.Success(new(result.Value, null))
                        : Result<ResourcePage<T>>.Failure(result.Error);
                },
                PickAmbiguousIdentifiers: pickAmbiguousIdentifiers),
            cancellationToken);

    private Task<ResourceSelectionResult<T>> SelectAsync<T>(
        ResourceSelectionRequest<T> request,
        CancellationToken cancellationToken)
        where T : class =>
        new ResourceSelector<T>(picker, recentStore).SelectAsync(request, cancellationToken);

    private static Result<ResourcePage<T>> ToPage<T>(
        Result<ListPageResult<T>> result,
        Func<ListPageResult<T>, string?> getNextToken)
        where T : class
    {
        if (result.IsFailure)
        {
            return Result<ResourcePage<T>>.Failure(result.Error);
        }

        ListPageResult<T> page = result.Value;
        return Result<ResourcePage<T>>.Success(
            new ResourcePage<T>(page.Items, page.HasMore ? getNextToken(page) : null));
    }

    private static int ParseOffset(string? token) =>
        int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int offset) ? offset : 0;

    private static DateTimeOffset? ParseTimestamp(string? token) =>
        DateTimeOffset.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : null;

    private static string McpSelectionId(McpServerInfo server) =>
        $"{server.Name}@{McpScopeLabel(server)}";

    private static string McpScopeLabel(McpServerInfo server)
    {
        if (string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            return "global";
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(server.WorkingDirectory));
        return "workspace-" + Convert.ToHexString(digest.AsSpan(0, 4)).ToLowerInvariant();
    }
}
