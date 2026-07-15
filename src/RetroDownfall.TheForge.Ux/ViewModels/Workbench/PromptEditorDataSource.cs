using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>API-backed data source for The Scriptorium. Returns <c>Data</c> only on <c>IsSuccess</c>; <c>null</c> otherwise.</summary>
public sealed class PromptEditorDataSource : IPromptEditorDataSource
{

    private readonly PromptService _promptService;

    public PromptEditorDataSource(PromptService promptService)
    {

        _promptService = promptService;

    }

    public async Task<PromptDetailDto?> LoadPromptAsync(Guid id, CancellationToken cancellationToken)
    {

        ApiResponse<PromptDetailDto>? response = await _promptService.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<PromptDetailDto?> SaveAsync(Guid id, UpdatePromptRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<PromptDetailDto>? response = await _promptService.UpdateAsync(id, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<PromptRenderResultDto?> RenderAsync(Guid id, PromptRenderRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<PromptRenderResultDto>? response = await _promptService.RenderAsync(id, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<PromptTestResultDto?> TestAsync(Guid id, TestPromptRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<PromptTestResultDto>? response = await _promptService.TestAsync(id, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(Guid id, PromptExecuteRequest request, CancellationToken cancellationToken) =>
        _promptService.ExecuteStreamAsync(id, request, cancellationToken);

    public async Task<IReadOnlyList<PromptVersionDto>> ListVersionsAsync(string name, Guid? campaignId, CancellationToken cancellationToken)
    {

        ApiResponse<PromptVersionDto[]>? response = await _promptService
            .ListVersionsAsync(name, campaignId, cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true, Data: not null } ? response.Data : [];

    }

    public async Task<PromptDetailDto?> CloneAsync(Guid id, ClonePromptRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<PromptDetailDto>? response = await _promptService.CloneAsync(id, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<PromptExportDto?> ExportAsync(Guid id, CancellationToken cancellationToken)
    {

        ApiResponse<PromptExportDto>? response = await _promptService.ExportAsync(id, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<DataSourceResult<PromptSummaryDto>> ImportAsync(PromptImportRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<PromptSummaryDto>? response = await _promptService.ImportAsync(request, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<PromptSummaryDto>.FromResponse(response);

    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        _promptService.DeleteAsync(id, cancellationToken);

}
