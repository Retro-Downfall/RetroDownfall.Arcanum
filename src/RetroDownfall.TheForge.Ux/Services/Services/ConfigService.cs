using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>GET/PUT/POST /api/config[/validate]</c> — the Arcanum-side settings surface (distinct from The Forge's own <c>forge.json</c>).</summary>
public sealed class ConfigService
{

    private readonly ArcanumApiClient _apiClient;

    public ConfigService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<ArcanumSettings>?> GetAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/config", TheForgeJsonContext.Default.ApiResponseArcanumSettings, cancellationToken);

    public Task<ApiResponse<bool>?> UpdateAsync(ArcanumSettings settings, CancellationToken cancellationToken) =>
        _apiClient.PutAsync(
            "/api/config",
            settings,
            TheForgeJsonContext.Default.ArcanumSettings,
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    public Task<ApiResponse<bool>?> ValidateAsync(ArcanumSettings settings, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/config/validate",
            settings,
            TheForgeJsonContext.Default.ArcanumSettings,
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

}
