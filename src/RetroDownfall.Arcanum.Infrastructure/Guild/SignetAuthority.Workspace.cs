using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class SignetAuthority
{
    internal Task<Result<T>> AdmitWorkspaceAsync<T>(GuildExecutionHandle execution, Guid workspaceId, GuildAuthorityAction action,
        Func<GuildSignetRecord, GuildVerifiedAuthority, CancellationToken, Task<Result<T>>> effect, CancellationToken cancellationToken)
    {
        if (execution is null || workspaceId == Guid.Empty || effect is null)
            return Task.FromResult(Result<T>.Failure(Mismatch));

        return UnderRootAsync(execution.RootQuestId, true, async ct =>
        {
            Result<Verified> live = await ReadLiveAsync(execution, ct).ConfigureAwait(false);

            if (live.IsFailure) return Result<T>.Failure(live.Error);

            GuildSignetRecord row = live.Value.Lineage[0];

            if (row.Owners.WorkspaceId != workspaceId) return Result<T>.Failure(Mismatch);

            GuildVerifiedAuthority proof = new(execution, action, workspaceId, row.Revision, live.Value.Effective);

            return await effect(row, proof, ct).ConfigureAwait(false);
        }, cancellationToken);
    }
}
