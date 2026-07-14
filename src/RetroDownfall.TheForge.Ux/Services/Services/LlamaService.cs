using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps the <c>/api/llama</c> route group for The Reliquary (LlamaCpp model management).</summary>
public sealed class LlamaService
{

    private readonly ArcanumApiClient _apiClient;

    public LlamaService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    /// <summary><c>POST /api/llama/models/pull</c> — NDJSON <see cref="LlamaPullProgress"/> download-progress stream.</summary>
    public IAsyncEnumerable<LlamaPullProgress> PullModelAsync(PullModelRequestDto request, CancellationToken cancellationToken) =>
        _apiClient.PostNdjsonStreamAsync(
            "/api/llama/models/pull",
            request,
            TheForgeJsonContext.Default.PullModelRequestDto,
            TheForgeJsonContext.Default.LlamaPullProgress,
            cancellationToken);

    public Task<ApiResponse<CachedModelInfo[]>?> ListModelsAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/llama/models", TheForgeJsonContext.Default.ApiResponseCachedModelInfoArray, cancellationToken);

    public Task<ApiResponse<LlamaServerInfo[]>?> ListServersAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/llama/servers", TheForgeJsonContext.Default.ApiResponseLlamaServerInfoArray, cancellationToken);

    public Task<ApiResponse<LlamaServerInfo>?> StartServerAsync(
        string cacheKey,
        StartLlamaServerRequestDto? request,
        CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/llama/servers/{Uri.EscapeDataString(cacheKey)}/start",
            request ?? new StartLlamaServerRequestDto(),
            TheForgeJsonContext.Default.StartLlamaServerRequestDto,
            TheForgeJsonContext.Default.ApiResponseLlamaServerInfo,
            cancellationToken);

    public Task<ApiResponse<bool>?> StopServerAsync(string cacheKey, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/llama/servers/{Uri.EscapeDataString(cacheKey)}/stop",
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    public Task<ApiResponse<bool>?> StopAllServersAsync(CancellationToken cancellationToken) =>
        _apiClient.PostAsync("/api/llama/servers/stop", TheForgeJsonContext.Default.ApiResponseBoolean, cancellationToken);

    public Task<ApiResponse<WarmupResultDto>?> WarmupAsync(string cacheKey, WarmupRequestDto request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/llama/servers/{Uri.EscapeDataString(cacheKey)}/warmup",
            request,
            TheForgeJsonContext.Default.WarmupRequestDto,
            TheForgeJsonContext.Default.ApiResponseWarmupResultDto,
            cancellationToken);

}
