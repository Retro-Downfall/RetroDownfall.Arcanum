using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<bool>> LocalChildrenStoppedAsync(GuildQuestDriver driver, CancellationToken ct) =>
        authority.InQuestTransactionAsync<bool>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                await using SqliteCommand frontier = Command(connection, transaction, """
                    WITH RECURSIVE tree(Id,OperationId) AS (
                        SELECT Id,OperationId FROM guild_quests WHERE Id=$quest
                        UNION SELECT c.Id,c.OperationId FROM guild_quests c JOIN tree p ON c.ParentQuestId=p.Id)
                    SELECT CASE WHEN EXISTS(SELECT 1 FROM tree c JOIN LongRunningOperations o ON o.Id=c.OperationId
                            WHERE c.Id<>$quest AND o.State=7 AND o.TerminalErrorCode=$interrupted)
                        THEN -1
                        ELSE NOT EXISTS(SELECT 1 FROM tree c JOIN LongRunningOperations o ON o.Id=c.OperationId
                                WHERE c.Id<>$quest AND o.State NOT IN(4,5,6))
                            AND NOT EXISTS(SELECT 1 FROM tree c JOIN guild_dispatches d ON d.QuestId=c.Id WHERE d.EvidenceCode=2)
                        END
                    """);

                Add(frontier, "$quest", Id(driver.Execution.QuestId));

                Add(frontier, "$interrupted", ErrorCodes.Guild.LocalExecutionInterrupted);

                object? state = await frontier.ExecuteScalarAsync(token).ConfigureAwait(false);

                // An explicit runtime interruption cannot be replayed. Input waits and generic
                // crash parks keep their existing resumable-frontier checks and remain pending here.
                if (Equals(-1L, state)) return UncertainDispatch;

                return Equals(1L, state);
            }, ct);
}
