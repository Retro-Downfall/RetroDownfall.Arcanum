using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Reads own physical evidence once across the exact subtree, retaining unknown exposure and separate allocation authority.</summary>
public interface IGuildTreeSpendLedger
{
    Task<Result<GuildSpendDto>> ReadAsync(GuildCallerContext caller, Guid questId, CancellationToken cancellationToken);
}
