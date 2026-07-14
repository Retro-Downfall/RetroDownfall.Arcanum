using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Chronicle;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps the <c>/api/apprentices</c> route group for The War Table. There is no dedicated lineage
/// endpoint — callers walk <see cref="ApprenticeDetailDto.ParentApprenticeId"/> client-side via
/// repeated <see cref="GetAsync"/> calls until it is <see langword="null"/>. Live Chronicle
/// observation is exposed via <see cref="ArcanumSseClient.StreamChronicleAsync"/>.
/// </summary>
public sealed class ApprenticeService
{

    private readonly ArcanumApiClient _apiClient;

    private readonly ArcanumSseClient _sseClient;

    public ApprenticeService(ArcanumApiClient apiClient, ArcanumSseClient sseClient)
    {

        _apiClient = apiClient;

        _sseClient = sseClient;

    }

    public Task<ApiResponse<ListPageResult<ApprenticeSummaryDto>>?> ListAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/apprentices", TheForgeJsonContext.Default.ApiResponseListPageResultApprenticeSummaryDto, cancellationToken);

    public Task<ApiResponse<ApprenticeDetailDto>?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.GetAsync($"/api/apprentices/{id}", TheForgeJsonContext.Default.ApiResponseApprenticeDetailDto, cancellationToken);

    public Task<ApiResponse<ApprenticeDetailDto>?> CreateAsync(CreateApprenticeRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/apprentices",
            request,
            TheForgeJsonContext.Default.CreateApprenticeRequest,
            TheForgeJsonContext.Default.ApiResponseApprenticeDetailDto,
            cancellationToken);

    public Task<ApiResponse<string>?> StartAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.PostAsync($"/api/apprentices/{id}/start", TheForgeJsonContext.Default.ApiResponseString, cancellationToken);

    public Task<ApiResponse<string>?> PauseAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.PostAsync($"/api/apprentices/{id}/pause", TheForgeJsonContext.Default.ApiResponseString, cancellationToken);

    public Task<ApiResponse<string>?> ResumeAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.PostAsync($"/api/apprentices/{id}/resume", TheForgeJsonContext.Default.ApiResponseString, cancellationToken);

    public Task<ApiResponse<string>?> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        _apiClient.PostAsync($"/api/apprentices/{id}/cancel", TheForgeJsonContext.Default.ApiResponseString, cancellationToken);

    public Task<ApiResponse<ApprenticeDetailDto>?> ReweaveAsync(Guid id, ReweaveApprenticeRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/apprentices/{id}/reweave",
            request,
            TheForgeJsonContext.Default.ReweaveApprenticeRequest,
            TheForgeJsonContext.Default.ApiResponseApprenticeDetailDto,
            cancellationToken);

    public Task<ApiResponse<string>?> InterveneAsync(Guid id, InterveneApprenticeRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/apprentices/{id}/intervene",
            request,
            TheForgeJsonContext.Default.InterveneApprenticeRequest,
            TheForgeJsonContext.Default.ApiResponseString,
            cancellationToken);

    /// <summary>
    /// Walks the <see cref="ApprenticeDetailDto.ParentApprenticeId"/> chain from <paramref name="id"/>
    /// up to the root, since no dedicated lineage endpoint exists. The returned list is ordered
    /// child-first (index 0 is <paramref name="id"/>'s own detail, the last entry is the root).
    /// </summary>
    public async Task<IReadOnlyList<ApprenticeDetailDto>> GetLineageAsync(Guid id, CancellationToken cancellationToken)
    {

        List<ApprenticeDetailDto> chain = [];

        Guid? current = id;

        HashSet<Guid> visited = [];

        while (current is { } currentId && visited.Add(currentId))
        {

            ApiResponse<ApprenticeDetailDto>? response = await GetAsync(currentId, cancellationToken).ConfigureAwait(false);

            if (response is not { IsSuccess: true, Data: { } detail })
            {

                break;

            }

            chain.Add(detail);

            current = detail.ParentApprenticeId;

        }

        return chain;

    }

    /// <summary><c>GET /api/apprentices/{id}/chronicle</c> — see <see cref="ChronicleFrame"/> for why frames are not <c>ApprenticeEvent</c>.</summary>
    public IAsyncEnumerable<ChronicleFrame> StreamChronicleAsync(Guid id, CancellationToken cancellationToken) =>
        _sseClient.StreamChronicleAsync(id, cancellationToken);

}
