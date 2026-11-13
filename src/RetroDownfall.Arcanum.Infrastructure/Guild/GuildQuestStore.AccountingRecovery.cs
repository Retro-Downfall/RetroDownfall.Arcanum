using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal static async Task<bool> ValidateAccountingLineageOwnershipAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid root, Guid account, CancellationToken ct)
    {
        await using SqliteCommand ownership = Command(connection, transaction, """
            WITH RECURSIVE ancestors(Id) AS (
                SELECT AccountId FROM guild_budget_accounts WHERE AccountId=$account
                UNION SELECT a.ParentAccountId FROM guild_budget_accounts a JOIN ancestors x ON a.AccountId=x.Id WHERE a.ParentAccountId IS NOT NULL)
            SELECT NOT EXISTS(SELECT 1 FROM guild_budget_accounts a JOIN ancestors x ON x.Id=a.AccountId
                LEFT JOIN guild_budget_accounts root ON root.AccountId=a.RootAccountId
                WHERE root.AccountId IS NULL OR root.RootAccountId<>root.AccountId OR root.ParentAccountId IS NOT NULL OR root.OwnerKindCode<>0 OR root.OwnerId<>$root
                   OR (a.OwnerKindCode=0 AND NOT EXISTS(SELECT 1 FROM guild_quests q JOIN guild_quest_budget b ON b.QuestId=q.Id AND b.AccountId=a.AccountId
                       WHERE q.Id=a.OwnerId AND q.AllocationAccountId=a.AccountId AND q.RootQuestId=$root))
                   OR (a.OwnerKindCode=1 AND NOT EXISTS(SELECT 1 FROM guild_cohorts h WHERE h.Id=a.OwnerId AND h.AllocationAccountId=a.AccountId AND h.RootQuestId=$root))
                   OR a.OwnerKindCode NOT IN(0,1))
            """);

        Add(ownership, "$account", Id(account));

        Add(ownership, "$root", Id(root));

        return Equals(1L, await ownership.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    internal Task<Result<bool>> RefreshAccountingProjectionAsync(Guid root, Guid quest, CancellationToken ct) =>
        authority.InQuestTransactionAsync(root, (connection, transaction, _, token) =>
            RefreshAccountingProjectionWithinAsync(connection, transaction, root, quest, time.GetUtcNow(), token), ct);

    internal static async Task<Result<bool>> RefreshAccountingProjectionWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid root, Guid quest, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            Guid result;

            long revision;

            await using (SqliteCommand candidate = Command(connection, transaction, """
                SELECT q.AllocationAccountId,q.Revision,r.ResultId,a.RootAccountId FROM guild_quests q
                JOIN guild_result_receipts r ON r.QuestId=q.Id
                JOIN guild_budget_accounts a ON a.AccountId=q.AllocationAccountId
                WHERE q.Id=$quest AND q.RootQuestId=$root AND q.AccountingStateCode IN(1,2)
                  AND typeof(q.Revision)='integer' AND q.Revision BETWEEN 1 AND 9223372036854775806
                """))
            {
                Add(candidate, "$quest", Id(quest));

                Add(candidate, "$root", Id(root));

                await using SqliteDataReader reader = await candidate.ExecuteReaderAsync(ct).ConfigureAwait(false);

                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return false;

                revision = reader.GetInt64(1);

                result = Guid.Parse(reader.GetString(2));
            }

            if (!await AccountingClearWithinAsync(connection, transaction, root, quest, ct).ConfigureAwait(false)) return false;

            bool hasSource;

            await using (SqliteCommand existing = Command(connection, transaction,
                "SELECT QuestId FROM guild_portal_outbox WHERE SourceKindCode=$kind AND ReceiptId=$receipt"))
            {
                Add(existing, "$kind", (int)GuildReceiptSourceKind.Settlement);

                Add(existing, "$receipt", Id(result));

                object? stored = await existing.ExecuteScalarAsync(ct).ConfigureAwait(false);

                if (stored is not null && stored is not string) return false;

                if (stored is string sourceQuest && sourceQuest != Id(quest)) return false;

                hasSource = stored is not null;
            }

            await using (SqliteCommand update = Command(connection, transaction, """
                UPDATE guild_quests SET AccountingStateCode=0,Revision=Revision+1,UpdatedAtUtc=$now
                WHERE Id=$quest AND RootQuestId=$root AND AccountingStateCode IN(1,2) AND Revision=$revision
                """))
            {
                Add(update, "$quest", Id(quest));

                Add(update, "$root", Id(root));

                Add(update, "$revision", revision);

                Add(update, "$now", UtcInstantText.Format(now));

                if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return false;
            }

            if (!hasSource)
            {
                Result<long> recorded = await GuildPortalEvents.RecordSourceWithinAsync(connection, transaction,
                    root, quest, GuildReceiptSourceKind.Settlement, result, revision + 1, now, ct).ConfigureAwait(false);

                if (recorded.IsFailure) return recorded.Error;
            }

            await using (SqliteCommand withhold = Command(connection, transaction, """
                UPDATE guild_portal_outbox SET DeliveryStateCode=3 WHERE QuestId=$quest AND DeliveryStateCode=1
                AND EXISTS(SELECT 1 FROM guild_quests source JOIN guild_quests root ON root.Id=source.RootQuestId
                    LEFT JOIN guild_quests recipient ON recipient.Id=source.ParentQuestId
                    WHERE source.Id=$quest AND (source.Retired=1 OR root.Retired=1 OR recipient.Retired=1))
                """))
            {
                Add(withhold, "$quest", Id(quest));

                await withhold.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    internal static async Task<bool> AccountingClearWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid root, Guid quest, CancellationToken ct)
    {
        try
        {
            Guid account, rootAccount;

            await using (SqliteCommand binding = Command(connection, transaction, """
                SELECT q.AllocationAccountId,a.RootAccountId FROM guild_quests q
                JOIN guild_result_receipts r ON r.QuestId=q.Id
                JOIN guild_budget_accounts a ON a.AccountId=q.AllocationAccountId
                WHERE q.Id=$quest AND q.RootQuestId=$root
                """))
            {
                Add(binding, "$quest", Id(quest));

                Add(binding, "$root", Id(root));

                await using SqliteDataReader reader = await binding.ExecuteReaderAsync(ct).ConfigureAwait(false);

                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return false;

                account = Guid.Parse(reader.GetString(0));

                rootAccount = Guid.Parse(reader.GetString(1));
            }

            var lineage = await GuildBudgetLedger.ReadFiniteLineageAsync(connection, transaction, account, false, ct).ConfigureAwait(false);

            if (lineage.IsFailure) return false;

            if (!await ValidateAccountingLineageOwnershipAsync(connection, transaction, root, account, ct).ConfigureAwait(false)) return false;

            if (!await ProveAccountingSubtreeWithinAsync(connection, transaction, root, quest, account, rootAccount,
                lineage.Value.Usd, lineage.Value.Tokens, ct).ConfigureAwait(false)) return false;

            if (!await GuildBudgetObligations.ReservationsClearWithinAsync(connection, transaction, account, ct).ConfigureAwait(false)) return false;

            return true;
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private const string AccountingSubtree = """
        WITH RECURSIVE funding(Id,FiniteUsd,FiniteTokens) AS (
          SELECT AccountId,$usd,$tokens FROM guild_budget_accounts WHERE AccountId=$account
          UNION
          SELECT a.AccountId,(f.FiniteUsd OR a.UsdLimit IS NOT NULL),(f.FiniteTokens OR a.TokenLimit IS NOT NULL)
          FROM guild_budget_accounts a JOIN funding f ON a.ParentAccountId=f.Id),
        quests(Id) AS (
          SELECT Id FROM guild_quests WHERE Id=$quest
          UNION SELECT child.Id FROM guild_quests child JOIN quests parent ON child.ParentQuestId=parent.Id)
        """;

    private static async Task<bool> ProveAccountingSubtreeWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid root, Guid quest, Guid account, Guid rootAccount, bool finiteUsd, bool finiteTokens, CancellationToken ct)
    {
        await using (SqliteCommand descendants = Command(connection, transaction, AccountingSubtree + """
            SELECT NOT EXISTS(SELECT 1 FROM guild_quests c JOIN quests q ON q.Id=c.Id
                WHERE c.RootQuestId<>$root OR c.AllocationAccountId NOT IN(SELECT Id FROM funding) OR
            """ + GuildQuestRetentionPins.StoppedExecutionUnresolved + ")"))
        {
            BindAccounting(descendants, root, quest, account, finiteUsd, finiteTokens);

            if (!Equals(1L, await descendants.ExecuteScalarAsync(ct).ConfigureAwait(false))) return false;
        }

        await using SqliteCommand command = Command(connection, transaction, AccountingSubtree + """
            SELECT a.AccountId,a.OwnerKindCode,a.OwnerId,a.RootAccountId,a.ParentAccountId,a.UsdLimit,a.TokenLimit,a.Closed,a.SettlementReceiptId,
                p.UsdLimit,p.TokenLimit,f.FiniteUsd,f.FiniteTokens,
                CASE a.OwnerKindCode WHEN 0 THEN EXISTS(SELECT 1 FROM guild_quests q JOIN guild_quest_budget b ON b.QuestId=q.Id AND b.AccountId=a.AccountId
                    JOIN guild_result_receipts r ON r.QuestId=q.Id AND r.ResultId=a.SettlementReceiptId
                    WHERE q.Id=a.OwnerId AND q.Id IN(SELECT Id FROM quests) AND q.RootQuestId=$root
                      AND q.AllocationAccountId=a.AccountId AND (r.NoDispatchProven=1 OR r.WorkStoppedProven=1))
                  WHEN 1 THEN EXISTS(SELECT 1 FROM guild_cohorts h WHERE h.Id=a.OwnerId AND h.RootQuestId=$root AND h.AllocationAccountId=a.AccountId
                    AND h.IssuerQuestId IN(SELECT Id FROM quests) AND h.Id=a.SettlementReceiptId AND h.StateCode IN(4,5,6)) ELSE 0 END,
                e.EffectId,e.QuestId,e.UsdBound,e.TokenBound,e.KnownUsd,e.KnownTokens,e.UnknownUsd,e.UnknownTokens,e.StateCode,e.ReservationId,
                v.ReceiptId,v.KnownUsd,v.KnownTokens,v.UnknownUsd,v.UnknownTokens,v.Complete,v.NoDispatchProven,
                EXISTS(SELECT 1 FROM guild_dispatches d WHERE d.DispatchId=e.DispatchId AND d.QuestId=e.QuestId AND d.AccountingAssociationId=e.AccountingAssociationId),
                typeof(v.Digest)='blob' AND length(v.Digest)=32,
                typeof(a.Closed)='integer' AND a.Closed IN(0,1) AND typeof(a.OwnerKindCode)='integer'
                  AND (a.UsdLimit IS NULL OR typeof(a.UsdLimit)='text') AND (a.TokenLimit IS NULL OR typeof(a.TokenLimit)='integer')
                  AND (p.UsdLimit IS NULL OR typeof(p.UsdLimit)='text') AND (p.TokenLimit IS NULL OR typeof(p.TokenLimit)='integer'),
                e.EffectId IS NULL OR (typeof(e.KindCode)='integer' AND e.KindCode IN(0,1)
                  AND typeof(e.StateCode)='integer' AND typeof(e.KnownUsd)='text' AND typeof(e.KnownTokens)='integer'
                  AND (e.UsdBound IS NULL OR typeof(e.UsdBound)='text') AND (e.TokenBound IS NULL OR typeof(e.TokenBound)='integer')
                  AND typeof(e.UnknownUsd)='integer' AND e.UnknownUsd IN(0,1) AND typeof(e.UnknownTokens)='integer' AND e.UnknownTokens IN(0,1)
                  AND typeof(v.KnownUsd)='text' AND typeof(v.KnownTokens)='integer'
                  AND typeof(v.UnknownUsd)='integer' AND v.UnknownUsd IN(0,1) AND typeof(v.UnknownTokens)='integer' AND v.UnknownTokens IN(0,1)
                  AND typeof(v.Complete)='integer' AND v.Complete IN(0,1) AND typeof(v.NoDispatchProven)='integer' AND v.NoDispatchProven IN(0,1))
            FROM funding f JOIN guild_budget_accounts a ON a.AccountId=f.Id
            LEFT JOIN guild_budget_accounts p ON p.AccountId=a.ParentAccountId
            LEFT JOIN guild_budget_effects e ON e.AccountId=a.AccountId
            LEFT JOIN guild_budget_evidence v ON v.EffectId=e.EffectId AND v.AppliedRevision=e.Revision
            ORDER BY a.AccountId,e.EffectId
            """);

        BindAccounting(command, root, quest, account, finiteUsd, finiteTokens);

        bool found = false;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found = true;

            GuildBudgetOwnerKind kind = (GuildBudgetOwnerKind)reader.GetInt32(1);

            if (!Enum.IsDefined(kind) || Guid.Parse(reader.GetString(3)) != rootAccount
                || reader.GetBoolean(7) != true || reader.IsDBNull(8) || reader.GetInt64(13) != 1 || reader.GetInt64(33) != 1) return false;

            decimal? ownUsd = reader.IsDBNull(5) ? null : ExactUsdText.Read(reader, 5);

            long? ownTokens = reader.IsDBNull(6) ? null : reader.GetInt64(6);

            decimal? parentUsd = reader.IsDBNull(9) ? null : ExactUsdText.Read(reader, 9);

            long? parentTokens = reader.IsDBNull(10) ? null : reader.GetInt64(10);

            if (ownUsd < 0 || ownTokens < 0 || parentUsd < 0 || parentTokens < 0
                || parentUsd is { } usd && (ownUsd is null || ownUsd > usd)
                || parentTokens is { } tokens && (ownTokens is null || ownTokens > tokens)) return false;

            if (reader.IsDBNull(14)) continue;

            if (kind != GuildBudgetOwnerKind.Quest || reader.GetString(2) != reader.GetString(15)
                || reader.IsDBNull(24) || reader.GetInt64(31) != 1 || reader.GetInt64(32) != 1 || reader.GetInt64(34) != 1
                || Guid.Parse(reader.GetString(24)) == Guid.Empty) return false;

            decimal? boundUsd = reader.IsDBNull(16) ? null : ExactUsdText.Read(reader, 16);

            long? boundTokens = reader.IsDBNull(17) ? null : reader.GetInt64(17);

            decimal knownUsd = ExactUsdText.Read(reader, 18);

            long knownTokens = reader.GetInt64(19);

            bool unknownUsd = reader.GetBoolean(20), unknownTokens = reader.GetBoolean(21);

            GuildBudgetEffectState state = (GuildBudgetEffectState)reader.GetInt32(22);

            if (!Enum.IsDefined(state) || boundUsd < 0 || boundTokens < 0 || knownUsd < 0 || knownTokens < 0
                || boundUsd is { } maxUsd && knownUsd > maxUsd || boundTokens is { } maxTokens && knownTokens > maxTokens
                || ExactUsdText.Read(reader, 25) != knownUsd || reader.GetInt64(26) != knownTokens
                || reader.GetBoolean(27) != unknownUsd || reader.GetBoolean(28) != unknownTokens
                || reader.GetBoolean(29) != (state is GuildBudgetEffectState.Complete or GuildBudgetEffectState.ProvenNoDispatch)
                || reader.GetBoolean(30) != (state == GuildBudgetEffectState.ProvenNoDispatch)
                || state == GuildBudgetEffectState.ProvenNoDispatch && (knownUsd != 0m || knownTokens != 0 || unknownUsd || unknownTokens)) return false;

            var obligations = GuildBudgetObligations.Classify(state, reader.GetBoolean(11), reader.GetBoolean(12),
                !reader.IsDBNull(23), unknownUsd, unknownTokens, boundUsd, boundTokens, knownUsd, knownTokens);

            if (obligations.PhysicalPending || obligations.UsdBlocked || obligations.TokensBlocked) return false;
        }

        return found;
    }

    private static void BindAccounting(SqliteCommand command, Guid root, Guid quest, Guid account, bool finiteUsd, bool finiteTokens)
    {
        Add(command, "$root", Id(root));

        Add(command, "$quest", Id(quest));

        Add(command, "$account", Id(account));

        Add(command, "$usd", finiteUsd ? 1 : 0);

        Add(command, "$tokens", finiteTokens ? 1 : 0);
    }
}
