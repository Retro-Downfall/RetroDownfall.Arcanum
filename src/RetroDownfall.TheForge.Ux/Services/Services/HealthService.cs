using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>GET /api/health</c>, <c>GET /api/meta</c>, <c>GET /api/grimoire/stats</c>.</summary>
public sealed class HealthService
{

    private readonly ArcanumApiClient _apiClient;

    private readonly ILogger<HealthService> _logger;

    public HealthService(ArcanumApiClient apiClient, ILogger<HealthService> logger)
    {

        _apiClient = apiClient;

        _logger = logger;

    }

    public Task<ApiResponse<HealthReportDto>?> GetHealthAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/health", ForgeJsonContext.Default.ApiResponseHealthReportDto, cancellationToken);

    public Task<ApiResponse<InstanceMetadataDto>?> GetMetaAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/meta", ForgeJsonContext.Default.ApiResponseInstanceMetadataDto, cancellationToken);

    public Task<ApiResponse<GrimoireStatsDto>?> GetGrimoireStatsAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/grimoire/stats", ForgeJsonContext.Default.ApiResponseGrimoireStatsDto, cancellationToken);

}
