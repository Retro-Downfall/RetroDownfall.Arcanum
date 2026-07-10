using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Chronicle;

namespace RetroDownfall.TheForge.Ux.ViewModels.WarTable;

/// <summary>Data-source seam for The War Table.</summary>
public interface IWarTableDataSource
{

    Task<IReadOnlyList<ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken);

    Task<ApprenticeDetailDto?> GetApprenticeAsync(Guid id, CancellationToken cancellationToken);

    Task<ApprenticeDetailDto?> CreateApprenticeAsync(CreateApprenticeRequest request, CancellationToken cancellationToken);

    Task<bool> StartAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> PauseAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ResumeAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken);

    Task<ApprenticeDetailDto?> ReweaveAsync(Guid id, ReweaveApprenticeRequest request, CancellationToken cancellationToken);

    Task<bool> InterveneAsync(Guid id, InterveneApprenticeRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApprenticeDetailDto>> GetLineageAsync(Guid id, CancellationToken cancellationToken);

    IAsyncEnumerable<ChronicleFrame> StreamChronicleAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken cancellationToken);

}
