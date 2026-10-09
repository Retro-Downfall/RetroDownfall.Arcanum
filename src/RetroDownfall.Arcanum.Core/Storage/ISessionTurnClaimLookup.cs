using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>Finds the content-free identity of a previously accepted Session turn.</summary>
public interface ISessionTurnClaimLookup
{
    /// <summary>Checks local durable ownership of an opaque accepted key without requiring a live Covenant origin.</summary>
    ValueTask<Result<bool>> HasClaimAsync(
        Guid clientTurnId,
        CancellationToken cancellationToken);

    ValueTask<Result<SessionTurnClaim?>> FindAsync(
        Guid originInstallationId,
        Guid clientTurnId,
        CancellationToken cancellationToken);

}
