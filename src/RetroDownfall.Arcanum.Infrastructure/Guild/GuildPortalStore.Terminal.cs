using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    internal Task<Result<GuildTerminalEventDto?>> ReadTreeTerminalAsync(GuildCallerContext caller, Guid root,
        Guid subtree, string cursor, CancellationToken ct)
    {
        if (caller is null || root == Guid.Empty || subtree == Guid.Empty || string.IsNullOrEmpty(cursor))
            return Task.FromResult(Result<GuildTerminalEventDto?>.Failure(CursorInvalid));

        return authority.InQuestTransactionAsync<GuildTerminalEventDto?>(root, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token, operatorMetadataOnly: true).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            Result<PortalCursor> saved = await ReadCursorWithinAsync(connection, transaction, caller, binding.Value,
                root, ScopedReplayKey(subtree, null), cursor, token).ConfigureAwait(false);

            if (saved.IsFailure) return saved.Error;

            Result<PortalReadScope> scope = await RequireWatchScopeWithinAsync(connection, transaction, signets,
                caller, root, subtree, null, binding.Value, true, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, token).ConfigureAwait(false);

            if (head.IsFailure) return head.Error;

            if (saved.Value.After != head.Value.Last) return Result<GuildTerminalEventDto?>.Success(null);

            Result<GuildQuestStored> current = await GuildQuestStore.ReadWithinAsync(connection, transaction, subtree, token).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            Result<bool> drained = await quests.IsWatchTreeDrainedWithinAsync(connection, transaction, current.Value.Quest, token).ConfigureAwait(false);

            if (drained.IsFailure) return drained.Error;

            if (!drained.Value) return Result<GuildTerminalEventDto?>.Success(null);

            GuildQuestDto quest = current.Value.Quest;

            await using SqliteCommand accounting = Command(connection, transaction, """
                WITH RECURSIVE members(Id) AS (
                    SELECT Id FROM guild_quests WHERE Id=$subtree AND RootQuestId=$root
                    UNION SELECT q.Id FROM guild_quests q INDEXED BY idx_guild_quests_parent JOIN members m ON q.ParentQuestId=m.Id
                        WHERE q.RootQuestId=$root)
                SELECT MAX(c.AccountingStateCode) FROM guild_quests c JOIN members m ON m.Id=c.Id
                """);

            Add(accounting, "$subtree", Id(subtree));

            Add(accounting, "$root", Id(root));

            if (await accounting.ExecuteScalarAsync(token).ConfigureAwait(false) is not long value || value is < 0 or > 2)
                return Unavailable;

            return Result<GuildTerminalEventDto?>.Success(new(quest.Id, quest.Revision, quest.State, (GuildAccountingState)value, head.Value.Last));
        }, ct);
    }

    internal Task<Result<GuildTerminalEventDto?>> ReadCohortTerminalAsync(GuildCallerContext caller, Guid root,
        Guid cohortId, string cursor, CancellationToken ct)
    {
        if (caller is null || root == Guid.Empty || cohortId == Guid.Empty || string.IsNullOrEmpty(cursor))
            return Task.FromResult(Result<GuildTerminalEventDto?>.Failure(CursorInvalid));

        return authority.InQuestTransactionAsync<GuildTerminalEventDto?>(root, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token, operatorMetadataOnly: true).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            Result<PortalCursor> saved = await ReadCursorWithinAsync(connection, transaction, caller, binding.Value,
                root, ScopedReplayKey(null, cohortId), cursor, token).ConfigureAwait(false);

            if (saved.IsFailure) return saved.Error;

            Result<PortalReadScope> scope = await RequireWatchScopeWithinAsync(connection, transaction, signets,
                caller, root, null, cohortId, binding.Value, true, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, token).ConfigureAwait(false);

            if (head.IsFailure) return head.Error;

            if (saved.Value.After != head.Value.Last) return Result<GuildTerminalEventDto?>.Success(null);

            Result<GuildCohortStored> found = await GuildCohortStore.ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

            if (found.IsFailure) return found.Error;

            CohortDto cohort = found.Value.Cohort;

            if (cohort.State is not (GuildCohortState.Completed or GuildCohortState.Failed or GuildCohortState.Canceled))
                return Result<GuildTerminalEventDto?>.Success(null);

            // Terminal Cohort state is committed with a closed allocation only after the durable
            // settlement walk proves its member, descendant and effect obligations converged.
            await using SqliteCommand check = Command(connection, transaction, $"""
                WITH RECURSIVE members(Id) AS (
                    SELECT a.QuestId FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id
                        WHERE m.CohortId=$cohort
                    UNION SELECT q.Id FROM guild_quests q INDEXED BY idx_guild_quests_parent JOIN members p ON q.ParentQuestId=p.Id
                        WHERE q.RootQuestId=$root),
                accounts(Id) AS (
                    SELECT AccountId FROM guild_budget_accounts WHERE AccountId=$account AND OwnerId=$cohort AND Closed=1 AND SettlementReceiptId=$cohort
                    UNION SELECT a.AccountId FROM guild_budget_accounts a JOIN accounts p ON a.ParentAccountId=p.Id)
                SELECT EXISTS(SELECT 1 FROM accounts WHERE Id=$account)
                    AND NOT EXISTS(SELECT 1 FROM guild_budget_accounts a JOIN accounts p ON p.Id=a.AccountId WHERE a.Closed<>1)
                    AND NOT EXISTS(SELECT 1 FROM guild_budget_effects e JOIN accounts p ON p.Id=e.AccountId WHERE e.StateCode IN(0,1))
                    AND NOT EXISTS(SELECT 1 FROM guild_quests c JOIN members m ON m.Id=c.Id WHERE {GuildQuestRetentionPins.StoppedExecutionUnresolved})
                    AND NOT EXISTS(SELECT 1 FROM guild_result_outbox o JOIN members m ON m.Id=o.QuestId WHERE o.DeliveryStateCode NOT IN(2,3))
                    AND NOT EXISTS(SELECT 1 FROM guild_portal_outbox o JOIN members m ON m.Id=o.QuestId WHERE o.DeliveryStateCode NOT IN(2,3))
                    AND NOT EXISTS(SELECT 1 FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId
                        WHERE i.RecipientQuestId IS NOT NULL
                        AND (i.RecipientQuestId IN(SELECT Id FROM members) OR e.QuestId IN(SELECT Id FROM members))
                        AND {GuildQuestRetentionPins.UnresolvedPortalInbox})
                """);

            Add(check, "$cohort", Id(cohortId));

            Add(check, "$root", Id(root));

            Add(check, "$account", Id(cohort.AllocationAccountId));

            if (!Equals(1L, await check.ExecuteScalarAsync(token).ConfigureAwait(false)))
                return Result<GuildTerminalEventDto?>.Success(null);

            QuestState state = cohort.State == GuildCohortState.Completed ? QuestState.Completed
                : cohort.State == GuildCohortState.Failed ? QuestState.Failed : QuestState.Canceled;

            return Result<GuildTerminalEventDto?>.Success(new(cohortId, cohort.Revision, state, GuildAccountingState.Settled, head.Value.Last));
        }, ct);
    }

}
