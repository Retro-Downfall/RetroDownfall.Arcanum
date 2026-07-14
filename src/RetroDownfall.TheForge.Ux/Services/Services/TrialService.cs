using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>POST /api/proving-grounds/trials/run</c> for The Proving Grounds.</summary>
public sealed class TrialService
{

    private readonly ArcanumApiClient _apiClient;

    public TrialService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<TrialResult>?> RunAsync(Trial trial, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/proving-grounds/trials/run",
            trial,
            TheForgeJsonContext.Default.Trial,
            TheForgeJsonContext.Default.ApiResponseTrialResult,
            cancellationToken);

}
