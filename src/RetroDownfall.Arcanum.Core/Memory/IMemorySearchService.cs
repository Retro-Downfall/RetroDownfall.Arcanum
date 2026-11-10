using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>Shared bounded persisted-memory search, with separate operator inspection and model disclosure authority.</summary>
public interface IMemorySearchService
{
    Task<Result<LexiconInspectionResult<MemorySearchResponse>>> SearchAsync(
        MemorySearchRequest? request, ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default);

    Task<Result<MemorySearchResponse>> SearchForInvocationAsync(
        MemorySearchRequest request, CancellationToken cancellationToken = default);

    Task<Result<MemorySearchResponse>> SearchForInvocationAsync(
        MemorySearchRequest request, long allocationBytes, CancellationToken cancellationToken = default) =>
        SearchForInvocationAsync(request, cancellationToken);
}
