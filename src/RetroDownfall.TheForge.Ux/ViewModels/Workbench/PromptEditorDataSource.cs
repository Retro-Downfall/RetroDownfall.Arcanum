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

}
