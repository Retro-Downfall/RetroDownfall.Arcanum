using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>Reads a committed claimed reply only after proving it clean in the same SQL snapshot.</summary>
public interface ISessionTurnClaimReplayStore
{

    ValueTask<Result<GrimoireEntryDto>> ReadCleanCommittedReplyAsync(
        SessionTurnClaimLease lease,
        CancellationToken cancellationToken);

}
