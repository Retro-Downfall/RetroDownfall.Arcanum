using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Built-in runtime controls; callers must hold physical runtime admission before retaining a model.</summary>
public interface ILocalRuntimeAdapter
{
    string AdapterId { get; }

    Task<Result<LocalRuntimeInspection>> InspectAsync(LocalRuntimeSettings runtime, CancellationToken cancellationToken);

    /// <summary>Requests residency for a positive duration or Timeout.InfiniteTimeSpan. This may load the model; it is neither a hard pin nor a KV snapshot.</summary>
    Task<Result> RetainModelAsync(LocalRuntimeSettings runtime, string model, TimeSpan retention, CancellationToken cancellationToken);
}

public sealed record LocalRuntimeCapabilities(bool ModelResidencyRetention, bool KvSnapshotRestore, bool BeforeUnloadNotification);

public sealed record LocalRuntimeLoadedModel(string Name, string? Digest, DateTimeOffset? ExpiresAt, long? SizeBytes);

public sealed record LocalRuntimeInspection(string RuntimeVersion, LocalRuntimeCapabilities Capabilities, IReadOnlyList<LocalRuntimeLoadedModel> LoadedModels);
