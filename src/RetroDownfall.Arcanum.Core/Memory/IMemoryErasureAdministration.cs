using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>
/// The installation-wide erasure surfaces that belong to no one store: the content-free status, the
/// write-ahead-log scrub retry, and the recovery of a lost or replaced erasure key.
/// </summary>
/// <remarks>
/// <para>A port of its own, beside the erase ports and the release port, so no store implementation can
/// grow a second path that discards evidence. Status is a read; the scrub only ever clears the one
/// upgradable reason on receipts it read as pending; and a key reset is prepared, then applied with the
/// preview's token, and discards only evidence the current key cannot verify.</para>
///
/// <para>Nothing here returns content, a fingerprint, a key, or a key identifier: every answer is a key
/// state and counts.</para>
/// </remarks>
public interface IMemoryErasureAdministration
{
    Task<Result<MemoryErasureStatusDto>> GetStatusAsync(CancellationToken cancellationToken);

    Task<Result<MemoryErasureScrubResultDto>> ScrubAsync(CancellationToken cancellationToken);

    Task<Result<MemoryErasureKeyResetPreflightDto>> PrepareKeyResetAsync(CancellationToken cancellationToken);

    Task<Result<MemoryErasureKeyResetResultDto>> ResetKeyAsync(
        MemoryErasureKeyResetRequest request,
        CancellationToken cancellationToken);
}
