using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    // This path is reachable only before the local engine has been entered. A failed or
    // ambiguous proof retains the lease; a committed dispatch is never released here.
    internal Task<Result<bool>> ReleaseUnexposedLocalLeaseAsync(Guid root, Guid quest, Guid operation, Guid dispatch,
        string owner, long revision, CancellationToken ct)
    {
        if (root == Guid.Empty || quest == Guid.Empty || operation == Guid.Empty || dispatch == Guid.Empty
            || string.IsNullOrWhiteSpace(owner) || revision < 0 || revision == long.MaxValue)
            return Task.FromResult(Result<bool>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<bool>(root, async (connection, transaction, _, token) =>
        {
            await using var command = Command(connection, transaction, """
                UPDATE "LongRunningOperations"
                SET "LeaseOwner"=NULL,"LeaseExpiresAt"=NULL,"Revision"="Revision"+1
                WHERE "Id"=$operation AND "Kind"=$kind AND "State"=$running AND "LeaseOwner"=$owner AND "Revision"=$revision AND "LeaseExpiresAt">$now
                    AND EXISTS(SELECT 1 FROM guild_quests q WHERE q.Id=$quest AND q.RootQuestId=$root AND q.OperationId=$operation)
                    AND NOT EXISTS(SELECT 1 FROM guild_dispatches d WHERE d.DispatchId=$dispatch)
                    AND NOT EXISTS(SELECT 1 FROM guild_budget_effects e WHERE e.DispatchId=$dispatch)
                """);

            Add(command, "$operation", operation.ToString("N"));
            Add(command, "$kind", LongRunningOperationKinds.GuildQuest);
            Add(command, "$running", (int)LongRunningOperationState.Running);
            Add(command, "$owner", owner);
            Add(command, "$revision", revision);
            Add(command, "$now", UtcInstantText.Format(time.GetUtcNow()));
            Add(command, "$quest", Id(quest));
            Add(command, "$root", Id(root));
            Add(command, "$dispatch", Id(dispatch));

            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        }, ct);
    }
}
