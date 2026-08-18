using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps the top-level <c>/api/prompts</c> route group (read paths for the alpha — The Scriptorium's
/// full create/update/render/test/clone flow is a later phase). Campaign-scoped prompt listing lives
/// on <see cref="CampaignService.GetPromptsAsync"/>.
/// </summary>
public sealed class PromptService
{

    private readonly ArcanumApiClient _apiClient;

    public PromptService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<ListPageResult<PromptSummaryDto>>?> ListAsync(
        Guid? campaignId,
        string? query,
        string? tag,
        int? limit,
        int? offset,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            "/api/prompts",
            ("campaignId", campaignId?.ToString()),
            ("q", query),
            ("tag", tag),
            ("limit", limit?.ToString()),
            ("offset", offset?.ToString()));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseListPageResultPromptSummaryDto, cancellationToken);

    }

    public Task<ApiResponse<PromptDetailDto>?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.GetAsync($"/api/prompts/{id}", TheForgeJsonContext.Default.ApiResponsePromptDetailDto, cancellationToken);

    public Task<ApiResponse<PromptVersionDto[]>?> ListVersionsAsync(string name, Guid? campaignId, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            $"/api/prompts/by-name/{Uri.EscapeDataString(name)}/versions",
            ("campaignId", campaignId?.ToString()));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponsePromptVersionDtoArray, cancellationToken);

    }

    /// <summary><c>POST /api/prompts</c> — creates a prompt; <c>CampaignId</c> may be null for a global prompt.</summary>
    public Task<ApiResponse<PromptDetailDto>?> CreateAsync(CreatePromptRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/prompts",
            request,
            TheForgeJsonContext.Default.CreatePromptRequest,
            TheForgeJsonContext.Default.ApiResponsePromptDetailDto,
            cancellationToken);

    /// <summary><c>PUT /api/prompts/{id}</c> — partial update; a field is preserved when the request sends <c>null</c> for it.</summary>
    public Task<ApiResponse<PromptDetailDto>?> UpdateAsync(Guid id, UpdatePromptRequest request, CancellationToken cancellationToken) =>
        _apiClient.PutAsync(
            $"/api/prompts/{id}",
            request,
            TheForgeJsonContext.Default.UpdatePromptRequest,
            TheForgeJsonContext.Default.ApiResponsePromptDetailDto,
            cancellationToken);

    /// <summary><c>POST /api/prompts/{id}/render</c> — renders the template with the supplied parameters (no LLM cost).</summary>
    public Task<ApiResponse<PromptRenderResultDto>?> RenderAsync(Guid id, PromptRenderRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/prompts/{id}/render",
            request,
            TheForgeJsonContext.Default.PromptRenderRequest,
            TheForgeJsonContext.Default.ApiResponsePromptRenderResultDto,
            cancellationToken);

    /// <summary><c>POST /api/prompts/{id}/test</c> — assembles the system prompt with default parameters (no LLM cost).</summary>
    public Task<ApiResponse<PromptTestResultDto>?> TestAsync(Guid id, TestPromptRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/prompts/{id}/test",
            request,
            TheForgeJsonContext.Default.TestPromptRequest,
            TheForgeJsonContext.Default.ApiResponsePromptTestResultDto,
            cancellationToken);

    /// <summary><c>POST /api/prompts/{id}/execute-stream</c> — live prompt execution, NDJSON <see cref="IntelligenceEvent"/> stream.</summary>
    public IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(Guid id, PromptExecuteRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostNdjsonStreamAsync(
            $"/api/prompts/{id}/execute-stream",
            request,
            TheForgeJsonContext.Default.PromptExecuteRequest,
            TheForgeJsonContext.Default.IntelligenceEvent,
            cancellationToken);

    /// <summary><c>DELETE /api/prompts/{id}</c> — success is <c>204 No Content</c>.</summary>
    public Task<DeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.DeleteNoContentAsync($"/api/prompts/{id}", cancellationToken);

    /// <summary><c>POST /api/prompts/{id}/clone</c> — copies the prompt under a new name/version.</summary>
    public Task<ApiResponse<PromptDetailDto>?> CloneAsync(Guid id, ClonePromptRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/prompts/{id}/clone",
            request,
            TheForgeJsonContext.Default.ClonePromptRequest,
            TheForgeJsonContext.Default.ApiResponsePromptDetailDto,
            cancellationToken);

    /// <summary><c>POST /api/prompts/{id}/export</c> — portable JSON export payload.</summary>
    public Task<ApiResponse<PromptExportDto>?> ExportAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/prompts/{id}/export",
            TheForgeJsonContext.Default.ApiResponsePromptExportDto,
            cancellationToken);

    /// <summary><c>POST /api/prompts/import</c> — imports a portable JSON export.</summary>
    public Task<ApiResponse<PromptSummaryDto>?> ImportAsync(PromptImportRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/prompts/import",
            request,
            TheForgeJsonContext.Default.PromptImportRequest,
            TheForgeJsonContext.Default.ApiResponsePromptSummaryDto,
            cancellationToken);

}
