using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Composition owned by the actual provider turn. The dispatcher supplies only host-verified bindings.</summary>
internal interface IGuildWorkspaceMutationRuntime
{
    Task<ResourceLimits> GetResourceLimitsAsync(string root, CancellationToken cancellationToken);

    Task<Result<ApplyPatchToolExecutionResponse>> ExecutePatchAsync(GuildChildToolAdmission call, Guid session, CanonicalCampaignContext campaign,
        Func<ApplyPatchInvocationContext, CancellationToken, Task<ApplyPatchToolExecutionResponse>> apply,
        CancellationToken cancellationToken);
}
