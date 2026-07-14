using RetroDownfall.Arcanum.Core.LlamaCpp;

namespace RetroDownfall.TheForge.Ux.ViewModels.Reliquary;

/// <summary>
/// Testable seam for The Reliquary (local LlamaCpp management). Implementations forward to
/// <see cref="RetroDownfall.TheForge.Ux.Services.Services.LlamaService"/> and map
/// <see cref="ApiResponse{T}"/> failures to null/false without throwing.
/// <see cref="PullModelAsync"/> forwards the NDJSON progress stream directly (no envelope unwrap).
/// </summary>
public interface IReliquaryDataSource
{

    Task<IReadOnlyList<CachedModelInfo>> ListCachedModelsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LlamaServerInfo>> ListServersAsync(CancellationToken cancellationToken);

    Task<LlamaServerInfo?> StartServerAsync(string cacheKey, CancellationToken cancellationToken);

    Task<bool> StopServerAsync(string cacheKey, CancellationToken cancellationToken);

    Task<WarmupResultDto?> WarmupServerAsync(string cacheKey, CancellationToken cancellationToken);

    IAsyncEnumerable<LlamaPullProgress> PullModelAsync(PullModelRequestDto request, CancellationToken cancellationToken);

}
