using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Local cycle identity is the stable agent, independently of its Card revision or Run.</summary>
public static class GuildDelegationPolicy
{
    public static Result ValidateAncestorPath(Guid targetAgentId, ImmutableArray<Guid> ancestorAgentIds)
    {
        if (targetAgentId == Guid.Empty || ancestorAgentIds.IsDefault || ancestorAgentIds.Contains(Guid.Empty))
        {
            return new Error(ErrorCodes.Guild.InvalidRequest, "A complete stable agent path is required.");
        }

        if (ancestorAgentIds.Contains(targetAgentId))
        {
            return new Error(ErrorCodes.Guild.DelegationCycle, "The target agent already occurs in the ancestor path.");
        }

        return Result.Success();
    }
}
