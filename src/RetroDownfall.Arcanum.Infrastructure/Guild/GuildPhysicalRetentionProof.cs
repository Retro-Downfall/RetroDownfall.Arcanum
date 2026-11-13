using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Proves local physical effects for a bounded page of normalized Run identities.</summary>
internal static class GuildPhysicalRetentionProof
{
    internal static async Task<Dictionary<string, bool>> ReadPageAsync(SqliteConnection connection,
        SqliteTransaction? transaction, IReadOnlyList<string> runKeys, CancellationToken ct)
    {
        if (runKeys.Count > 100) throw new ArgumentOutOfRangeException(nameof(runKeys));

        Dictionary<string, bool> clear = runKeys.ToDictionary(key => key, _ => true, StringComparer.Ordinal);

        if (clear.Count == 0) return clear;

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "CREATE TEMP TABLE IF NOT EXISTS guild_retention_run_page(RunKey TEXT PRIMARY KEY); DELETE FROM guild_retention_run_page";

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        foreach (string key in clear.Keys)
        {
            command.CommandText = "INSERT INTO guild_retention_run_page(RunKey) VALUES($key)";

            command.Parameters.Clear();

            command.Parameters.AddWithValue("$key", key);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        command.Parameters.Clear();

        command.CommandText = """
            WITH RECURSIVE targets(RunKey,AccountId) AS (
              SELECT DISTINCT p.RunKey,e.AccountId FROM guild_retention_run_page p
              CROSS JOIN guild_budget_effects e INDEXED BY idx_guild_budget_effects_accounting
              WHERE lower(replace(e.AccountingAssociationId,'-',''))=+p.RunKey AND e.KindCode IS NOT 1),
            lineage(Origin,Id) AS (
              SELECT AccountId,AccountId FROM targets
              UNION SELECT l.Origin,a.ParentAccountId FROM lineage l
                JOIN guild_budget_accounts a ON a.AccountId=l.Id WHERE a.ParentAccountId IS NOT NULL)
            SELECT t.RunKey,l.Origin,a.AccountId,a.RootAccountId,a.ParentAccountId,a.OwnerKindCode,a.OwnerId,a.UsdLimit,a.TokenLimit,
                p.AccountId,p.RootAccountId,p.UsdLimit,p.TokenLimit,root.OwnerId,root.ParentAccountId,root.RootAccountId,root.OwnerKindCode,
                CASE a.OwnerKindCode WHEN 0 THEN EXISTS(SELECT 1 FROM guild_quests q JOIN guild_quest_budget b ON b.QuestId=q.Id
                    AND b.AccountId=a.AccountId WHERE q.Id=a.OwnerId AND q.AllocationAccountId=a.AccountId AND q.RootQuestId=root.OwnerId)
                  WHEN 1 THEN EXISTS(SELECT 1 FROM guild_cohorts h WHERE h.Id=a.OwnerId AND h.AllocationAccountId=a.AccountId
                    AND h.RootQuestId=root.OwnerId) ELSE 0 END,
                typeof(a.OwnerKindCode)='integer' AND (a.UsdLimit IS NULL OR typeof(a.UsdLimit)='text' AND length(CAST(a.UsdLimit AS BLOB))<=64)
                  AND (a.TokenLimit IS NULL OR typeof(a.TokenLimit)='integer')
                  AND (p.UsdLimit IS NULL OR typeof(p.UsdLimit)='text' AND length(CAST(p.UsdLimit AS BLOB))<=64)
                  AND (p.TokenLimit IS NULL OR typeof(p.TokenLimit)='integer')
            FROM lineage l JOIN targets t ON t.AccountId=l.Origin
            LEFT JOIN guild_budget_accounts a ON a.AccountId=l.Id
            LEFT JOIN guild_budget_accounts p ON p.AccountId=a.ParentAccountId
            LEFT JOIN guild_budget_accounts root ON root.AccountId=a.RootAccountId
            ORDER BY t.RunKey,l.Origin,l.Id
            """;

        string? priorRun = null, priorOrigin = null;

        bool rootSeen = false;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string run = reader.GetString(0), origin = reader.GetString(1);

                if (priorRun is not null && (run != priorRun || origin != priorOrigin) && !rootSeen) clear[priorRun] = false;

                if (run != priorRun || origin != priorOrigin) rootSeen = false;

                priorRun = run;

                priorOrigin = origin;

                try
                {
                    if (reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(5) || reader.IsDBNull(6)
                        || reader.IsDBNull(13) || reader.IsDBNull(15) || reader.IsDBNull(16)
                        || reader.GetInt64(17) != 1 || reader.GetInt64(18) != 1
                        || reader.GetInt32(16) != 0 || !reader.IsDBNull(14)
                        || reader.GetString(3) != reader.GetString(15)
                        || !Guid.TryParseExact(reader.GetString(2), "D", out Guid accountId) || accountId == Guid.Empty
                        || !Guid.TryParseExact(reader.GetString(3), "D", out Guid rootId) || rootId == Guid.Empty
                        || !Guid.TryParseExact(reader.GetString(6), "D", out Guid ownerId) || ownerId == Guid.Empty)
                    { clear[run] = false; continue; }

                    decimal? ownUsd = reader.IsDBNull(7) ? null : ExactUsdText.Read(reader, 7);

                    long? ownTokens = reader.IsDBNull(8) ? null : reader.GetInt64(8);

                    decimal? parentUsd = reader.IsDBNull(11) ? null : ExactUsdText.Read(reader, 11);

                    long? parentTokens = reader.IsDBNull(12) ? null : reader.GetInt64(12);

                    if (ownUsd < 0 || ownTokens < 0 || parentUsd < 0 || parentTokens < 0
                        || parentUsd is { } usd && (ownUsd is null || ownUsd > usd)
                        || parentTokens is { } tokens && (ownTokens is null || ownTokens > tokens)) clear[run] = false;

                    if (reader.IsDBNull(4))
                    {
                        if (reader.GetString(2) != reader.GetString(3) || reader.GetInt32(5) != 0) clear[run] = false;

                        rootSeen = true;
                    }
                    else if (reader.IsDBNull(9) || reader.IsDBNull(10) || reader.GetString(4) != reader.GetString(9)
                        || reader.GetString(3) != reader.GetString(10)) clear[run] = false;
                }
                catch (Exception error) when (error is FormatException or InvalidCastException or ArgumentException or OverflowException)
                {
                    clear[run] = false;
                }
            }
        }

        if (priorRun is not null && !rootSeen) clear[priorRun] = false;

        command.CommandText = """
            SELECT p.RunKey,e.EffectId,e.QuestId,e.UsdBound,e.TokenBound,e.KnownUsd,e.KnownTokens,e.UnknownUsd,e.UnknownTokens,e.StateCode,
              e.ReservationId,a.UsdLimit,a.TokenLimit,v.ReceiptId,v.KnownUsd,v.KnownTokens,v.UnknownUsd,v.UnknownTokens,v.Complete,v.NoDispatchProven,
              EXISTS(SELECT 1 FROM guild_dispatches d WHERE d.DispatchId=e.DispatchId AND d.QuestId=e.QuestId AND d.AccountingAssociationId=e.AccountingAssociationId),
              a.OwnerKindCode=0 AND a.OwnerId=e.QuestId,
              typeof(e.KindCode)='integer' AND e.KindCode=0 AND typeof(e.KnownUsd)='text'
                AND length(CAST(e.KnownUsd AS BLOB))<=64 AND typeof(e.KnownTokens)='integer'
                AND (e.UsdBound IS NULL OR typeof(e.UsdBound)='text' AND length(CAST(e.UsdBound AS BLOB))<=64)
                AND (e.TokenBound IS NULL OR typeof(e.TokenBound)='integer') AND typeof(e.StateCode)='integer'
                AND typeof(e.UnknownUsd)='integer' AND e.UnknownUsd IN(0,1) AND typeof(e.UnknownTokens)='integer' AND e.UnknownTokens IN(0,1)
                AND typeof(v.Digest)='blob' AND length(v.Digest)=32 AND typeof(v.KnownUsd)='text' AND length(CAST(v.KnownUsd AS BLOB))<=64
                AND typeof(v.KnownTokens)='integer' AND typeof(v.UnknownUsd)='integer' AND v.UnknownUsd IN(0,1)
                AND typeof(v.UnknownTokens)='integer' AND v.UnknownTokens IN(0,1) AND typeof(v.Complete)='integer' AND v.Complete IN(0,1)
                AND typeof(v.NoDispatchProven)='integer' AND v.NoDispatchProven IN(0,1)
            FROM guild_retention_run_page p CROSS JOIN guild_budget_effects e INDEXED BY idx_guild_budget_effects_accounting
            LEFT JOIN guild_budget_accounts a ON a.AccountId=e.AccountId
            LEFT JOIN guild_budget_evidence v ON v.EffectId=e.EffectId AND v.AppliedRevision=e.Revision
            WHERE lower(replace(e.AccountingAssociationId,'-',''))=+p.RunKey AND e.KindCode IS NOT 1
            ORDER BY p.RunKey,e.EffectId
            """;

        bool links = false;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string run = reader.GetString(0);

                try
                {
                    if (reader.IsDBNull(13) || reader.IsDBNull(20) || reader.IsDBNull(21) || reader.GetInt64(20) != 1
                        || reader.GetInt64(21) != 1 || reader.GetInt64(22) != 1
                        || !Guid.TryParseExact(reader.GetString(1), "D", out Guid effectId) || effectId == Guid.Empty
                        || !Guid.TryParseExact(reader.GetString(2), "D", out Guid questId) || questId == Guid.Empty) { clear[run] = false; continue; }

                    decimal? boundUsd = reader.IsDBNull(3) ? null : ExactUsdText.Read(reader, 3);

                    long? boundTokens = reader.IsDBNull(4) ? null : reader.GetInt64(4);

                    decimal knownUsd = ExactUsdText.Read(reader, 5);

                    long knownTokens = reader.GetInt64(6);

                    bool unknownUsd = reader.GetBoolean(7), unknownTokens = reader.GetBoolean(8);

                    GuildBudgetEffectState state = (GuildBudgetEffectState)reader.GetInt32(9);

                    bool linked = !reader.IsDBNull(10);

                    links |= linked;

                    if (!Enum.IsDefined(state) || boundUsd < 0 || boundTokens < 0 || knownUsd < 0 || knownTokens < 0
                        || boundUsd is { } usd && knownUsd > usd || boundTokens is { } tokens && knownTokens > tokens
                        || !Guid.TryParse(reader.GetString(13), out Guid receipt) || receipt == Guid.Empty
                        || ExactUsdText.Read(reader, 14) != knownUsd || reader.GetInt64(15) != knownTokens
                        || reader.GetBoolean(16) != unknownUsd || reader.GetBoolean(17) != unknownTokens
                        || reader.GetBoolean(18) != (state is GuildBudgetEffectState.Complete or GuildBudgetEffectState.ProvenNoDispatch)
                        || reader.GetBoolean(19) != (state == GuildBudgetEffectState.ProvenNoDispatch)
                        || state == GuildBudgetEffectState.ProvenNoDispatch && (knownUsd != 0m || knownTokens != 0 || unknownUsd || unknownTokens))
                    { clear[run] = false; continue; }

                    var obligations = GuildBudgetObligations.Classify(state, !reader.IsDBNull(11), !reader.IsDBNull(12),
                        linked, unknownUsd, unknownTokens, boundUsd, boundTokens, knownUsd, knownTokens);

                    if (obligations.PhysicalPending || obligations.UsdBlocked || obligations.TokensBlocked) clear[run] = false;
                }
                catch (Exception error) when (error is FormatException or InvalidCastException or ArgumentException or OverflowException)
                {
                    clear[run] = false;
                }
            }
        }

        if (!links) return clear;

        command.CommandText = """
            SELECT p.RunKey,e.EffectId,e.ReservationId,e.AccountingAssociationId,e.StateCode,e.UsdBound,e.KnownUsd,e.UnknownUsd,
                r.Id,r.RunId,r.Status,r.ReservedUsd,r.ReconciledUsd,
                typeof(r.Status)='integer' AND typeof(r.ReservedUsd)='text' AND length(CAST(r.ReservedUsd AS BLOB))<=64
                  AND typeof(r.ReconciledUsd)='text' AND length(CAST(r.ReconciledUsd AS BLOB))<=64
            FROM guild_retention_run_page p CROSS JOIN guild_budget_effects e INDEXED BY idx_guild_budget_effects_accounting
            LEFT JOIN BudgetReservations r INDEXED BY IX_BudgetReservations_Id_Norm
              ON lower(replace(r.Id,'-',''))=lower(replace(e.ReservationId,'-',''))
            WHERE lower(replace(e.AccountingAssociationId,'-',''))=+p.RunKey AND e.KindCode IS NOT 1 AND e.ReservationId IS NOT NULL
            ORDER BY p.RunKey,e.EffectId
            """;

        string? previousEffect = null, previousRun = null;

        await using SqliteDataReader reservations = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reservations.ReadAsync(ct).ConfigureAwait(false))
        {
            string run = reservations.GetString(0), effect = reservations.GetString(1);

            if (run == previousRun && effect == previousEffect) { clear[run] = false; continue; }

            previousRun = run;

            previousEffect = effect;

            try
            {
                if (!GuildBudgetObligations.ValidReservationRow(reservations, 1)) clear[run] = false;
            }
            catch (Exception error) when (error is FormatException or InvalidCastException or ArgumentException or OverflowException)
            {
                clear[run] = false;
            }
        }

        return clear;
    }
}
