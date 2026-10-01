using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>
/// Lifts erasure fingerprints, so content, a name, or a key an operator erased may be written again by
/// extraction or an agent in that exact scope.
/// </summary>
/// <remarks>
/// <para>One store-neutral port, separate from every erase port, because release is the unsafe
/// direction: it re-enables automatic authorship. It is operator-only, takes no mutation id, and is
/// naturally idempotent. It recomputes the fingerprint from the supplied scope and identity and deletes
/// that row; Saga content is tried both exactly and trimmed.</para>
///
/// <para>A release never creates the erasure key. A store with no fingerprints answers
/// <see cref="MemoryErasureReleaseOutcome.NotFingerprinted"/> without asking for it, and fingerprints
/// the key cannot verify are refused as lost rather than reported as absent.</para>
/// </remarks>
public interface IMemoryErasureRelease
{
    Task<Result<MemoryErasureReleaseResultDto>> ReleaseSagaAsync(
        SagaErasureReleaseRequest request,
        CancellationToken cancellationToken);

    Task<Result<MemoryErasureReleaseResultDto>> ReleaseLexiconAsync(
        LexiconErasureReleaseRequest request,
        CancellationToken cancellationToken);

    Task<Result<MemoryErasureReleaseResultDto>> ReleaseCovenantAsync(
        CovenantErasureReleaseRequest request,
        CancellationToken cancellationToken);
}
