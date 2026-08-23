using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps the <c>/api/campaigns</c> route group for The Atelier's campaign roots: list/get/create/
/// update/delete, campaign-scoped prompts and sessions (campaign-scoped spells live on
/// <see cref="SpellService.GetCampaignSpellsAsync"/> instead, per the API integration notes), and
/// the campaign CODEX.md editor.
/// </summary>
public sealed class CampaignService
{

    private readonly ArcanumApiClient _apiClient;

    public CampaignService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<ListPageResult<CampaignDto>>?> ListAsync(
        WorkspaceType? type,
        int? limit,
        int? offset,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            "/api/campaigns",
            ("type", type?.ToString()),
            ("limit", limit?.ToString()),
            ("offset", offset?.ToString()));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseListPageResultCampaignDto, cancellationToken);

    }

    public Task<ApiResponse<CampaignDto>?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.GetAsync($"/api/campaigns/{id}", TheForgeJsonContext.Default.ApiResponseCampaignDto, cancellationToken);

    public Task<ApiResponse<CampaignDto>?> CreateAsync(RegisterCampaignRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/campaigns",
            request,
            TheForgeJsonContext.Default.RegisterCampaignRequest,
            TheForgeJsonContext.Default.ApiResponseCampaignDto,
            cancellationToken);

    public Task<ApiResponse<CampaignDto>?> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken) =>
        _apiClient.PutAsync(
            $"/api/campaigns/{id}",
            request,
            TheForgeJsonContext.Default.UpdateCampaignRequest,
            TheForgeJsonContext.Default.ApiResponseCampaignDto,
            cancellationToken);

    public Task<DeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.DeleteNoContentAsync($"/api/campaigns/{id}", cancellationToken);

    public Task<ApiResponse<ListPageResult<PromptSummaryDto>>?> GetPromptsAsync(
        Guid id,
        string? query,
        string? tag,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build($"/api/campaigns/{id}/prompts", ("q", query), ("tag", tag));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseListPageResultPromptSummaryDto, cancellationToken);

    }

    public Task<ApiResponse<SessionQueryResult>?> GetSessionsAsync(
        Guid id,
        string? status,
        string? search,
        int? limit,
        DateTimeOffset? beforeUpdatedAt,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            $"/api/campaigns/{id}/sessions",
            ("status", status),
            ("search", search),
            ("limit", limit?.ToString()),
            ("beforeUpdatedAt", beforeUpdatedAt?.ToString("O")));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseSessionQueryResult, cancellationToken);

    }

    public Task<ApiResponse<CodexContentDto>?> GetCodexAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.GetAsync($"/api/campaigns/{id}/codex", TheForgeJsonContext.Default.ApiResponseCodexContentDto, cancellationToken);

    public Task<ApiResponse<CodexContentDto>?> PutCodexAsync(Guid id, string content, CancellationToken cancellationToken) =>
        _apiClient.PutAsync(
            $"/api/campaigns/{id}/codex",
            new CodexPutRequest(content),
            TheForgeJsonContext.Default.CodexPutRequest,
            TheForgeJsonContext.Default.ApiResponseCodexContentDto,
            cancellationToken);

    /// <summary><c>DELETE /api/campaigns/{id}/codex</c> — 204.</summary>
    public Task<ApiResponse<bool>?> DeleteCodexAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.DeleteAsync(
            $"/api/campaigns/{id}/codex",
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    /// <summary><c>GET /api/codex</c> — the Grimoire-global CODEX.md.</summary>
    public Task<ApiResponse<CodexContentDto>?> GetGlobalCodexAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/codex", TheForgeJsonContext.Default.ApiResponseCodexContentDto, cancellationToken);

    /// <summary><c>PUT /api/codex</c> — the Grimoire-global CODEX.md.</summary>
    public Task<ApiResponse<CodexContentDto>?> PutGlobalCodexAsync(string content, CancellationToken cancellationToken) =>
        _apiClient.PutAsync(
            "/api/codex",
            new CodexPutRequest(content),
            TheForgeJsonContext.Default.CodexPutRequest,
            TheForgeJsonContext.Default.ApiResponseCodexContentDto,
            cancellationToken);

    /// <summary><c>DELETE /api/codex</c> — 204.</summary>
    public Task<ApiResponse<bool>?> DeleteGlobalCodexAsync(CancellationToken cancellationToken) =>
        _apiClient.DeleteAsync(
            "/api/codex",
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

}
