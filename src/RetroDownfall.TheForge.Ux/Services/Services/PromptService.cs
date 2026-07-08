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

        return _apiClient.GetAsync(path, ForgeJsonContext.Default.ApiResponseListPageResultPromptSummaryDto, cancellationToken);

    }

    public Task<ApiResponse<PromptDetailDto>?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.GetAsync($"/api/prompts/{id}", ForgeJsonContext.Default.ApiResponsePromptDetailDto, cancellationToken);

    public Task<ApiResponse<PromptVersionDto[]>?> ListVersionsAsync(string name, Guid? campaignId, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            $"/api/prompts/by-name/{Uri.EscapeDataString(name)}/versions",
            ("campaignId", campaignId?.ToString()));

        return _apiClient.GetAsync(path, ForgeJsonContext.Default.ApiResponsePromptVersionDtoArray, cancellationToken);

    }

}
