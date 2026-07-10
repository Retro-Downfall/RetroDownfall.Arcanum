using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Chronicle;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.WarTable;

/// <summary>API-backed War Table data source.</summary>
public sealed class WarTableDataSource : IWarTableDataSource
{

    private readonly ApprenticeService _apprenticeService;

    private readonly CampaignService _campaignService;

    private readonly WorkspaceService _workspaceService;

    public WarTableDataSource(
        ApprenticeService apprenticeService,
        CampaignService campaignService,
        WorkspaceService workspaceService)
    {

        _apprenticeService = apprenticeService;

        _campaignService = campaignService;

        _workspaceService = workspaceService;

    }

    public async Task<IReadOnlyList<ApprenticeSummaryDto>> ListApprenticesAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ListPageResult<ApprenticeSummaryDto>>? response = await _apprenticeService
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } page } ? page.Items : [];

    }

    public async Task<ApprenticeDetailDto?> GetApprenticeAsync(Guid id, CancellationToken cancellationToken)
    {

        ApiResponse<ApprenticeDetailDto>? response = await _apprenticeService.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<ApprenticeDetailDto?> CreateApprenticeAsync(CreateApprenticeRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<ApprenticeDetailDto>? response = await _apprenticeService.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<bool> StartAsync(Guid id, CancellationToken cancellationToken)
    {

        ApiResponse<string>? response = await _apprenticeService.StartAsync(id, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true };

    }

    public async Task<bool> PauseAsync(Guid id, CancellationToken cancellationToken)
    {

        ApiResponse<string>? response = await _apprenticeService.PauseAsync(id, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true };

    }

    public async Task<bool> ResumeAsync(Guid id, CancellationToken cancellationToken)
    {

        ApiResponse<string>? response = await _apprenticeService.ResumeAsync(id, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true };

    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {

        ApiResponse<string>? response = await _apprenticeService.CancelAsync(id, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true };

    }

    public async Task<ApprenticeDetailDto?> ReweaveAsync(Guid id, ReweaveApprenticeRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<ApprenticeDetailDto>? response = await _apprenticeService.ReweaveAsync(id, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<bool> InterveneAsync(Guid id, InterveneApprenticeRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<string>? response = await _apprenticeService.InterveneAsync(id, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true };

    }

    public Task<IReadOnlyList<ApprenticeDetailDto>> GetLineageAsync(Guid id, CancellationToken cancellationToken) =>
        _apprenticeService.GetLineageAsync(id, cancellationToken);

    public IAsyncEnumerable<ChronicleFrame> StreamChronicleAsync(Guid id, CancellationToken cancellationToken) =>
        _apprenticeService.StreamChronicleAsync(id, cancellationToken);

    public async Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ListPageResult<CampaignDto>>? response = await _campaignService
            .ListAsync(null, null, null, cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } page } ? page.Items : [];

    }

    public async Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken cancellationToken)
    {

        ApiResponse<WorkspaceInfo[]>? response = await _workspaceService.ListAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } workspaces } ? workspaces : [];

    }

}
