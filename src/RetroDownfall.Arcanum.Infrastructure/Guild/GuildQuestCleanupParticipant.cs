using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Seals the ordinary Quest lineage; uncertainty retains its existing family work.</summary>
internal sealed class GuildQuestCleanupParticipant(IGrimoireOrdinaryConnectionFactory connections, TimeProvider clock) : IGuildOwnerCleanupParticipant
{
    public async ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(owner, connection, transaction);

        command.CommandText = Affected(owner) + $"""
            UPDATE guild_quests AS c SET CancellationRequested=1,
                Retired=CASE WHEN {Owned(owner)} THEN 1 ELSE Retired END,Revision=Revision+1,UpdatedAtUtc=$now
            WHERE Id IN(SELECT Id FROM affected) AND (CancellationRequested=0 OR ({Owned(owner)} AND Retired=0));
            """;

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    public async ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken)
    {
        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, null, cancellationToken).ConfigureAwait(false);

        if (available.IsFailure)
        {
            return available;
        }

        await using SqliteTransaction transaction = lease.Connection.BeginTransaction(deferred: false);

        // A parent receipt may precede closure of a child's account. Refresh at most 100
        // stale Pending projections after re-proving each funding subtree in this writer
        // transaction. Unknown or missing evidence never becomes settled by inference.
        await using (SqliteCommand refresh = Command(owner, lease.Connection, transaction))
        {
            refresh.CommandText = Affected(owner) + """
                UPDATE guild_quests AS c SET AccountingStateCode=0,Revision=Revision+1,UpdatedAtUtc=$now
                WHERE c.Id IN (SELECT Id FROM affected) AND c.AccountingStateCode=1 AND c.Revision<9223372036854775807
                  AND c.Id IN (
                    SELECT candidate.Id FROM guild_quests candidate
                    JOIN guild_quest_budget binding ON binding.QuestId=candidate.Id AND binding.AccountId=candidate.AllocationAccountId
                    JOIN guild_budget_accounts own ON own.AccountId=binding.AccountId
                    WHERE candidate.Id IN (SELECT Id FROM affected) AND candidate.AccountingStateCode=1
                      AND own.OwnerKindCode=0 AND own.OwnerId=candidate.Id
                      AND EXISTS (SELECT 1 FROM guild_budget_accounts root
                        JOIN guild_quests rootQuest ON rootQuest.Id=candidate.RootQuestId
                        WHERE root.AccountId=own.RootAccountId AND root.RootAccountId=root.AccountId
                          AND root.ParentAccountId IS NULL AND root.OwnerKindCode=0 AND root.OwnerId=rootQuest.Id
                          AND rootQuest.AllocationAccountId=root.AccountId)
                      AND EXISTS (
                        WITH RECURSIVE ancestors(Id,ParentId) AS (
                          SELECT own.AccountId,own.ParentAccountId UNION
                          SELECT parent.AccountId,parent.ParentAccountId FROM guild_budget_accounts parent JOIN ancestors child ON parent.AccountId=child.ParentId)
                        SELECT 1 FROM ancestors WHERE Id=own.RootAccountId AND ParentId IS NULL)
                      AND NOT EXISTS (
                        WITH RECURSIVE subtree(Id) AS (
                          SELECT own.AccountId UNION SELECT child.AccountId FROM guild_budget_accounts child JOIN subtree p ON child.ParentAccountId=p.Id),
                        quests(Id) AS (
                          SELECT candidate.Id UNION SELECT child.Id FROM guild_quests child JOIN quests p ON child.ParentQuestId=p.Id)
                        SELECT 1 FROM guild_quests q JOIN quests ON quests.Id=q.Id
                        WHERE q.RootQuestId<>candidate.RootQuestId OR NOT EXISTS (
                          SELECT 1 FROM guild_quest_budget qb JOIN guild_budget_accounts qa ON qa.AccountId=qb.AccountId
                          WHERE qb.QuestId=q.Id AND qb.AccountId=q.AllocationAccountId AND qa.RootAccountId=own.RootAccountId
                            AND qa.OwnerKindCode=0 AND qa.OwnerId=q.Id AND qa.AccountId IN(SELECT Id FROM subtree)))
                      AND NOT EXISTS (
                        WITH RECURSIVE subtree(Id) AS (
                          SELECT own.AccountId UNION SELECT child.AccountId FROM guild_budget_accounts child JOIN subtree p ON child.ParentAccountId=p.Id)
                        SELECT 1 FROM guild_budget_accounts a JOIN subtree ON subtree.Id=a.AccountId WHERE (
                          a.RootAccountId<>own.RootAccountId OR a.Closed IS NOT 1 OR a.SettlementReceiptId IS NULL OR
                          (a.OwnerKindCode=0 AND NOT EXISTS (
                            SELECT 1 FROM guild_quests q JOIN guild_quest_budget qb ON qb.QuestId=q.Id AND qb.AccountId=a.AccountId
                              JOIN guild_result_receipts r ON r.QuestId=q.Id
                            WHERE q.Id=a.OwnerId AND q.RootQuestId=candidate.RootQuestId AND q.AllocationAccountId=a.AccountId
                              AND r.ResultId=a.SettlementReceiptId AND (r.NoDispatchProven=1 OR r.WorkStoppedProven=1))) OR
                          (a.OwnerKindCode=1 AND NOT EXISTS (
                            SELECT 1 FROM guild_cohorts h WHERE h.Id=a.OwnerId AND h.RootQuestId=candidate.RootQuestId
                              AND h.AllocationAccountId=a.AccountId AND h.Id=a.SettlementReceiptId AND h.StateCode IN(4,5,6))) OR
                          a.OwnerKindCode NOT IN(0,1)))
                      AND NOT EXISTS (
                        WITH RECURSIVE subtree(Id) AS (
                          SELECT own.AccountId UNION SELECT child.AccountId FROM guild_budget_accounts child JOIN subtree p ON child.ParentAccountId=p.Id)
                        SELECT 1 FROM guild_budget_accounts a JOIN subtree ON subtree.Id=a.AccountId
                        JOIN guild_budget_effects e ON e.AccountId=a.AccountId
                        WHERE a.OwnerKindCode<>0 OR e.QuestId<>a.OwnerId OR e.StateCode NOT IN(2,3)
                          OR e.UnknownUsd IS NOT 0 OR e.UnknownTokens IS NOT 0
                          OR NOT EXISTS (SELECT 1 FROM guild_budget_evidence evidence
                            WHERE evidence.EffectId=e.EffectId AND evidence.AppliedRevision=e.Revision))
                    ORDER BY candidate.Id LIMIT 100);
                """;

            refresh.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

            _ = await refresh.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Result settled = await SettledAsync(owner, lease.Connection, transaction, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return settled;
    }

    public async ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        Result settled = await SettledAsync(owner, connection, transaction, cancellationToken).ConfigureAwait(false);

        if (settled.IsFailure)
        {
            return settled;
        }

        await using SqliteCommand command = Command(owner, connection, transaction);

        command.CommandText = Affected(owner) + """
            UPDATE guild_quests SET CancellationConverged=1,Revision=Revision+1,UpdatedAtUtc=$now
            WHERE Id IN(SELECT Id FROM affected) AND CancellationConverged=0;
            """;

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Immutable issue/dispatch/result/answer/application evidence and authority-denial lineage
        // remain after owner deletion. Only a qualified full reset removes that namespace.
        return Result.Success();
    }

    private static async Task<Result> SettledAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(owner, connection, transaction);

        command.CommandText = Affected(owner)
            + $"SELECT COUNT(*) FROM guild_quests c WHERE c.Id IN(SELECT Id FROM affected) AND {GuildQuestRetentionPins.Unresolved};";

        return Equals(0L, await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            ? Result.Success()
            : new Error(ErrorCodes.Guild.ReconciliationRequired, "Guild owner cleanup retains unresolved Quest execution, delivery or accounting evidence.");
    }

    private static string Affected(GuildCleanupOwner owner) => $"""
        WITH RECURSIVE affected(Id) AS (
            SELECT c.Id FROM guild_quests c WHERE {Owned(owner)}
            UNION
            SELECT child.Id FROM guild_quests child JOIN affected parent ON child.ParentQuestId=parent.Id
        )
        """;

    private static string Owned(GuildCleanupOwner owner) => owner.Kind == 1
        ? "(c.CampaignId=$owner COLLATE NOCASE OR c.WorkspaceId=$owner COLLATE NOCASE)" : "c.SessionId=$owner COLLATE NOCASE";

    private static SqliteCommand Command(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction? transaction)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.Parameters.AddWithValue("$owner", owner.Id.ToString("D").ToUpperInvariant());

        return command;
    }
}
