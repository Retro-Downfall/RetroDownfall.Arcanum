using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    private static async Task<Result<GuildCohortSettlementNode>> NewSettlementNodeAsync(SqliteConnection connection,
        SqliteTransaction transaction, CohortDto cohort, Guid account, Guid? parent, CancellationToken ct)
    {
        GuildCohortSettlementNode node;

        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,UsdLimit,TokenLimit,Closed,Revision
            FROM guild_budget_accounts WHERE AccountId=$account
                AND (UsdLimit IS NULL OR typeof(UsdLimit)='text' AND length(CAST(UsdLimit AS BLOB))<=64)
            """))
        {
            Add(command, "$account", Id(account));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;

            if (parent is { } expected && (reader.IsDBNull(3) || Guid.Parse(reader.GetString(3)) != expected)) return Unavailable;

            node = new()
            {
                FormatVersion = GuildCohortSettlementNode.CurrentFormatVersion,
                AccountId = account, ParentAccountId = parent, RootAccountId = Guid.Parse(reader.GetString(2)),
                Kind = (GuildBudgetOwnerKind)reader.GetInt32(0), OwnerId = Guid.Parse(reader.GetString(1)),
                UsdLimit = reader.IsDBNull(4) ? null : ExactUsdText.Format(ExactUsdText.Read(reader, 4)),
                TokenLimit = reader.IsDBNull(5) ? null : reader.GetInt64(5), Closed = reader.GetBoolean(6),
                Revision = reader.GetInt64(7),
            };
        }

        if (!Enum.IsDefined(node.Kind) || node.Revision < 1 || node.TokenLimit < 0
            || node.UsdLimit is { } limit && Usd(limit) < 0) return Unavailable;

        node.PendingAccounts = parent is not null && !node.Closed ? 1 : 0;

        if (node.Kind == GuildBudgetOwnerKind.Quest)
        {
            await using SqliteCommand quest = Command(connection, transaction, """
                SELECT q.RootQuestId,q.AllocationAccountId,q.AccountingStateCode,q.CancellationRequested,q.CancellationConverged,
                    r.OutcomeCode,r.NoDispatchProven,r.WorkStoppedProven,
                    EXISTS(SELECT 1 FROM guild_cohort_members m WHERE m.CohortId=$cohort AND m.PrimaryVotingQuestId=q.Id)
                FROM guild_quests q JOIN guild_quest_budget b ON b.QuestId=q.Id AND b.AccountId=q.AllocationAccountId
                LEFT JOIN guild_result_receipts r ON r.QuestId=q.Id
                WHERE q.Id=$quest
                """);

            Add(quest, "$cohort", Id(cohort.Id));
            Add(quest, "$quest", Id(node.OwnerId));

            await using SqliteDataReader reader = await quest.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || Guid.Parse(reader.GetString(0)) != cohort.RootQuestId
                || Guid.Parse(reader.GetString(1)) != account) return Unavailable;

            bool cancel = reader.GetBoolean(3) && !reader.GetBoolean(4);

            if (!Enum.IsDefined((GuildAccountingState)reader.GetInt32(2))) return Unavailable;

            // The Quest's accounting projection can precede later physical billing evidence.
            // This traversal recomputes that evidence and every descendant before finalization.
            bool pending = !node.Closed || cancel
                || reader.IsDBNull(5) || reader.GetInt32(5) is not (3 or 4 or 5 or 6)
                || !(reader.GetBoolean(6) || reader.GetBoolean(7));

            node.IsPrimaryMember = reader.GetBoolean(8);

            node.PendingMembers = pending && node.IsPrimaryMember ? 1 : 0;
            node.PendingDescendants = pending && !node.IsPrimaryMember ? 1 : 0;
            node.CancelRequested = cancel ? 1 : 0;
        }
        else
        {
            await using SqliteCommand owner = Command(connection, transaction,
                "SELECT RootQuestId FROM guild_cohorts WHERE Id=$cohort AND AllocationAccountId=$account");

            Add(owner, "$cohort", Id(node.OwnerId));
            Add(owner, "$account", Id(account));

            if (await owner.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string root || Guid.Parse(root) != cohort.RootQuestId) return Unavailable;
        }

        return node;
    }

    private static async Task<Result<bool>> AccumulateSettlementEffectAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildCohortSettlementNode node, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT e.EffectId,e.QuestId,
                CASE WHEN e.UsdBound IS NULL OR length(CAST(e.UsdBound AS BLOB))<=64 THEN e.UsdBound END,e.TokenBound,
                CASE WHEN length(CAST(e.KnownUsd AS BLOB))<=64 THEN e.KnownUsd END,e.KnownTokens,e.UnknownUsd,e.UnknownTokens,e.StateCode,
                (e.UsdBound IS NULL OR typeof(e.UsdBound)='text' AND length(CAST(e.UsdBound AS BLOB))<=64)
                    AND typeof(e.KnownUsd)='text' AND length(CAST(e.KnownUsd AS BLOB))<=64
                    AND (e.TokenBound IS NULL OR typeof(e.TokenBound)='integer') AND typeof(e.KnownTokens)='integer'
                    AND typeof(e.UnknownUsd)='integer' AND e.UnknownUsd IN(0,1)
                    AND typeof(e.UnknownTokens)='integer' AND e.UnknownTokens IN(0,1)
                    AND typeof(e.StateCode)='integer' AND typeof(e.KindCode)='integer' AND e.KindCode IN(0,1),
                e.ReservationId,v.ReceiptId,v.KnownUsd,v.KnownTokens,v.UnknownUsd,v.UnknownTokens,v.Complete,v.NoDispatchProven,
                v.ReceiptId IS NULL OR (typeof(v.KnownUsd)='text' AND typeof(v.KnownTokens)='integer'
                    AND typeof(v.UnknownUsd)='integer' AND v.UnknownUsd IN(0,1)
                    AND typeof(v.UnknownTokens)='integer' AND v.UnknownTokens IN(0,1)
                    AND typeof(v.Complete)='integer' AND v.Complete IN(0,1)
                    AND typeof(v.NoDispatchProven)='integer' AND v.NoDispatchProven IN(0,1)
                    AND typeof(v.Digest)='blob' AND length(v.Digest)=32),
                EXISTS(SELECT 1 FROM guild_dispatches d WHERE d.DispatchId=e.DispatchId AND d.QuestId=e.QuestId
                    AND d.AccountingAssociationId=e.AccountingAssociationId)
            FROM guild_budget_effects e LEFT JOIN guild_budget_evidence v ON v.EffectId=e.EffectId AND v.AppliedRevision=e.Revision
            WHERE e.AccountId=$account AND e.EffectId>$after ORDER BY e.EffectId LIMIT 1
            """);

        Add(command, "$account", Id(node.AccountId));
        Add(command, "$after", node.LastEffect is { } effect ? Id(effect) : "");

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return false;

        if (reader.GetInt64(9) != 1 || node.Kind != GuildBudgetOwnerKind.Quest
            || Guid.Parse(reader.GetString(1)) != node.OwnerId) return Unavailable;

        decimal? boundUsd = reader.IsDBNull(2) ? null : ExactUsdText.Read(reader, 2);
        long? boundTokens = reader.IsDBNull(3) ? null : reader.GetInt64(3);
        decimal knownUsd = ExactUsdText.Read(reader, 4);
        long knownTokens = reader.GetInt64(5);
        bool unknownUsd = reader.GetBoolean(6), unknownTokens = reader.GetBoolean(7);
        GuildBudgetEffectState state = (GuildBudgetEffectState)reader.GetInt32(8);

        if (!Enum.IsDefined(state) || boundUsd < 0 || boundTokens < 0 || knownUsd < 0 || knownTokens < 0
            || reader.GetInt64(18) != 1 || reader.GetInt64(19) != 1) return Unavailable;

        bool hasReceipt = !reader.IsDBNull(11);

        if (state is GuildBudgetEffectState.Complete or GuildBudgetEffectState.ProvenNoDispatch && !hasReceipt) return Unavailable;

        if (hasReceipt && (Guid.Parse(reader.GetString(11)) == Guid.Empty || ExactUsdText.Read(reader, 12) != knownUsd
            || reader.GetInt64(13) != knownTokens || reader.GetBoolean(14) != unknownUsd || reader.GetBoolean(15) != unknownTokens
            || reader.GetBoolean(16) != (state is GuildBudgetEffectState.Complete or GuildBudgetEffectState.ProvenNoDispatch)
            || reader.GetBoolean(17) != (state == GuildBudgetEffectState.ProvenNoDispatch))) return Unavailable;

        if (state == GuildBudgetEffectState.ProvenNoDispatch && (knownUsd != 0m || knownTokens != 0 || unknownUsd || unknownTokens)) return Unavailable;

        node.KnownUsd = ExactUsdText.Format(ExactUsdText.CheckedAdd(Usd(node.KnownUsd), knownUsd));
        node.KnownTokens = checked(node.KnownTokens + knownTokens);
        node.UnknownUsd |= unknownUsd;
        node.UnknownTokens |= unknownTokens;
        node.CapabilityFault |= boundUsd is { } usd && knownUsd > usd || boundTokens is { } tokens && knownTokens > tokens;

        GuildBudgetObligations.Classification obligations = GuildBudgetObligations.Classify(state, node.FiniteUsd,
            node.FiniteTokens, !reader.IsDBNull(10), unknownUsd, unknownTokens, boundUsd, boundTokens, knownUsd, knownTokens);

        if (obligations.PhysicalPending) node.PendingEffects = checked(node.PendingEffects + 1);

        node.PhysicalBlocked |= obligations.PhysicalPending;
        node.UsdBlocked |= obligations.UsdBlocked;
        node.TokensBlocked |= obligations.TokensBlocked;

        node.HoldUsd = AddUsd(node.HoldUsd, obligations.HoldUsd is { } held ? ExactUsdText.Format(held) : null);
        node.HoldTokens = AddTokens(node.HoldTokens, obligations.HoldTokens);

        if (state == GuildBudgetEffectState.Complete)
        {
            if (unknownUsd) node.Unpriced = checked(node.Unpriced + 1);
            else node.Priced = checked(node.Priced + 1);
        }

        if (state == GuildBudgetEffectState.PossiblyDispatched || unknownUsd || unknownTokens)
            node.Uncertain = checked(node.Uncertain + 1);

        node.LastEffect = Guid.Parse(reader.GetString(0));

        return true;
    }

    private static async Task SaveSettlementStateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohort, long generation, long epoch, Guid? current, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_cohort_settlement_state(CohortId,Generation,Epoch,CurrentAccountId,Complete)
            VALUES($cohort,$generation,$epoch,$current,$complete)
            ON CONFLICT(CohortId) DO UPDATE SET Generation=excluded.Generation,Epoch=excluded.Epoch,
                CurrentAccountId=excluded.CurrentAccountId,Complete=excluded.Complete
            """);

        Add(command, "$cohort", Id(cohort));
        Add(command, "$generation", generation);
        Add(command, "$epoch", epoch);
        Add(command, "$current", current is { } id ? Id(id) : null);
        Add(command, "$complete", current is null ? 1 : 0);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task SaveSettlementNodeAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohort, long generation, GuildCohortSettlementNode node, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(node, GuildCohortSettlementJsonContext.Default.GuildCohortSettlementNode);

        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_cohort_settlement_nodes(CohortId,Generation,AccountId,NodeJson)
            VALUES($cohort,$generation,$account,$json)
            ON CONFLICT(CohortId,Generation,AccountId) DO UPDATE SET NodeJson=excluded.NodeJson
            """);

        Add(command, "$cohort", Id(cohort));
        Add(command, "$generation", generation);
        Add(command, "$account", Id(node.AccountId));
        Add(command, "$json", json);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Result<GuildCohortSettlementNode>> LoadSettlementNodeAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid cohort, long generation, Guid account, CancellationToken ct, bool allowOld = false)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT CASE WHEN typeof(n.NodeJson)='text' AND length(CAST(n.NodeJson AS BLOB))<=65536 THEN n.NodeJson END,
                a.RootAccountId,a.ParentAccountId,a.UsdLimit,a.TokenLimit,a.Closed,a.Revision,a.OwnerKindCode,a.OwnerId,h.AllocationAccountId
            FROM guild_cohort_settlement_nodes n JOIN guild_budget_accounts a ON a.AccountId=n.AccountId
              JOIN guild_cohorts h ON h.Id=n.CohortId
            WHERE n.CohortId=$cohort AND n.Generation=$generation AND n.AccountId=$account
            """);

        Add(command, "$cohort", Id(cohort));
        Add(command, "$generation", generation);
        Add(command, "$account", Id(account));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0)) return Unavailable;

        GuildCohortSettlementNode? node = JsonSerializer.Deserialize(reader.GetString(0), GuildCohortSettlementJsonContext.Default.GuildCohortSettlementNode);

        Guid actualRoot = Guid.Parse(reader.GetString(1));

        Guid? actualParent = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));

        decimal? actualUsd = reader.IsDBNull(3) ? null : ExactUsdText.Read(reader, 3);

        long? actualTokens = reader.IsDBNull(4) ? null : reader.GetInt64(4);

        bool actualClosed = reader.GetBoolean(5);

        long actualRevision = reader.GetInt64(6);

        GuildBudgetOwnerKind actualKind = (GuildBudgetOwnerKind)reader.GetInt32(7);

        Guid actualOwner = Guid.Parse(reader.GetString(8));

        Guid traversalRoot = Guid.Parse(reader.GetString(9));

        if (node is null || node.FormatVersion < 0 || node.FormatVersion > GuildCohortSettlementNode.CurrentFormatVersion
            || !allowOld && node.FormatVersion != GuildCohortSettlementNode.CurrentFormatVersion
            || node.AccountId != account || node.RootAccountId != actualRoot || node.OwnerId != actualOwner || node.Kind != actualKind
            || node.ParentAccountId != (account == traversalRoot ? null : actualParent)
            || (node.UsdLimit is null ? (decimal?)null : Usd(node.UsdLimit)) != actualUsd || node.TokenLimit != actualTokens
            || node.Closed != actualClosed || node.Revision != actualRevision
            || node.FormatVersion == GuildCohortSettlementNode.CurrentFormatVersion
                && (node.FiniteUsd != (actualUsd is not null) || node.FiniteTokens != (actualTokens is not null))
            || node.Revision < 1 || node.Phase is < 0 or > 2 || !Enum.IsDefined(node.Kind)
            || node.KnownTokens < 0 || node.HoldTokens < 0 || node.ActiveTokens < 0 || node.LiabilityTokens < 0
            || node.Priced < 0 || node.Unpriced < 0 || node.Uncertain < 0 || node.PendingMembers < 0
            || node.PendingDescendants < 0 || node.CancelRequested < 0 || node.PendingAccounts < 0 || node.PendingEffects < 0
            || Usd(node.KnownUsd) < 0 || node.HoldUsd is { } hold && Usd(hold) < 0
            || node.ActiveUsd is { } active && Usd(active) < 0 || node.LiabilityUsd is { } liability && Usd(liability) < 0)
            return Unavailable;

        return node;
    }
}
