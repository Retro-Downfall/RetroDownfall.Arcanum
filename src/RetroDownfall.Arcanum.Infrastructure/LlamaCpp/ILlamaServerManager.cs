using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

/// <summary>
/// Manages local <c>llama-server</c> child processes and inference concurrency slots.
/// </summary>
public interface ILlamaServerManager
{

    Task<Result<LlamaServerInfo>> EnsureServerAsync(
        string cacheKey,
        string? sourceUrl,
        int? gpuLayersOverride,
        int? portOverride,
        CancellationToken cancellationToken);

    Task<IDisposable> AcquireSlotAsync(string cacheKey, CancellationToken cancellationToken);

    bool IsModelInUse(string cacheKey);

    bool IsLlamaServerAvailable();

    LlamaServerInfo? TryGetRunningServer(string cacheKey);

    Task<Result> StopAsync(string cacheKey, CancellationToken cancellationToken);

    Task StopAllAsync(CancellationToken cancellationToken);

    IReadOnlyList<LlamaServerInfo> ListServers();

}
