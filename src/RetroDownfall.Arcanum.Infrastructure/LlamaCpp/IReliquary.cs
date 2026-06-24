using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

/// <summary>
/// Local GGUF model cache under <see cref="Core.Storage.ArcanumPaths.ModelCacheDirectory"/>.
/// </summary>
public interface IGgufModelCache
{

    bool IsCached(string cacheKey);

    string? GetModelPath(string cacheKey);

    Task<Result<string>> EnsureModelAsync(
        string cacheKey,
        string sourceUrl,
        string? expectedSha256,
        IProgress<LlamaPullProgress>? progress,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CachedModelInfo>> ListAsync(CancellationToken cancellationToken);

    Task<Result> DeleteAsync(string cacheKey, CancellationToken cancellationToken);

}
