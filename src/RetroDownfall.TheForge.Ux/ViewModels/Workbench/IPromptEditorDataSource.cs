using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Data-source seam for The Scriptorium, implemented by the API-backed adapter in production and fakes in tests.</summary>
public interface IPromptEditorDataSource
{

    Task<PromptDetailDto?> LoadPromptAsync(Guid id, CancellationToken cancellationToken);

    Task<PromptDetailDto?> SaveAsync(Guid id, UpdatePromptRequest request, CancellationToken cancellationToken);

    Task<PromptRenderResultDto?> RenderAsync(Guid id, PromptRenderRequest request, CancellationToken cancellationToken);

    Task<PromptTestResultDto?> TestAsync(Guid id, TestPromptRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(Guid id, PromptExecuteRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PromptVersionDto>> ListVersionsAsync(string name, Guid? campaignId, CancellationToken cancellationToken);

    Task<PromptDetailDto?> CloneAsync(Guid id, ClonePromptRequest request, CancellationToken cancellationToken);

    Task<PromptExportDto?> ExportAsync(Guid id, CancellationToken cancellationToken);

    Task<DataSourceResult<PromptSummaryDto>> ImportAsync(PromptImportRequest request, CancellationToken cancellationToken);

    Task<DeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken);

}
