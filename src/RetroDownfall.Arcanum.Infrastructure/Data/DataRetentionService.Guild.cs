using System.Data.Common;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class DataRetentionService
{
    private async Task AddGuildStatusesAsync(
        List<DataRetentionStatusItem> items,
        RetentionSettings retention,
        CancellationToken cancellationToken)
    {
        await AddDatabaseStatusAsync(items, RetentionDataClass.GuildContent, "guild_content", null,
            "Immutable Guild payload revisions. Active and unsettled references prevent ordinary retention.",
            retention, cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(items, RetentionDataClass.GuildRecords,
            ["guild_agent_cards", "guild_agent_card_revisions", "guild_agent_card_skills"],
            "Guild card cache headers, immutable revision links and skill projections. Operator files remain outside managed retention.",
            retention, cancellationToken).ConfigureAwait(false);

        await AddCompositeDatabaseStatusAsync(items, RetentionDataClass.GuildEvidence,
            ["guild_distribution_notifications", "guild_cohort_award_authority_scan", "guild_work_mutations", "guild_work_awards", "guild_work_bids", "guild_work_offer_recipients", "guild_work_offers", "guild_cohort_ledger", "guild_portal_cohort_audience", "guild_cohort_settlement_nodes", "guild_cohort_settlement_state", "guild_cohort_settlement_clock", "guild_cohort_selections", "guild_cohort_eval_candidates", "guild_cohort_eval_buckets", "guild_cohort_eval_state", "guild_cohort_stop_frontiers", "guild_cohort_result_observations", "guild_cohort_contributions", "guild_cohort_member_quests", "guild_cohort_mutations", "guild_cohort_members", "guild_cohorts", "guild_workspace_lease_mutations", "guild_workspace_leases", "guild_workspace_authority_state", "guild_budget_evidence", "guild_budget_effects", "guild_quest_budget", "guild_budget_accounts", "guild_memory_attribution", "guild_portal_inputs", "guild_portal_receipts", "guild_inbox", "guild_portal_outbox", "guild_events", "guild_portal_cursors", "guild_envelopes", "guild_portal_streams", "guild_event_streams", "guild_remote_questions", "guild_remote_callbacks", "guild_remote_effects", "guild_remote_executions", "guild_inbound_tasks", "guild_inbound_peer_state", "guild_continuations", "guild_result_inputs", "guild_result_applications", "guild_result_outbox", "guild_result_receipts", "guild_dispatches", "guild_quest_cancellations", "guild_local_wait_checkpoints", "guild_local_executions", "guild_quest_start_intents", "guild_quests", "guild_signets", "guild_signet_revocations", "guild_signet_authority_state", "guild_content_erasure_receipts", "guild_owner_cleanup", "guild_roster_refreshes", "guild_roster_cursors", "guild_declared_source_claims", "guild_declared_roots"],
            "Content-free Guild budget, physical spend, erasure and owner cleanup evidence; never aged out on a timer.",
            retention, cancellationToken).ConfigureAwait(false);
    }

    internal const string GuildPhysicalRetentionRunPageSql = """
                SELECT DISTINCT lower(replace(effect.AccountingAssociationId,'-','')) AS RunKey
                FROM guild_budget_effects effect INDEXED BY idx_guild_budget_effects_accounting WHERE effect.KindCode IS NOT 1
                  AND lower(replace(effect.AccountingAssociationId,'-',''))>$after
                  AND EXISTS(SELECT 1 FROM InferenceRuns run
                    WHERE lower(replace(run.Id,'-',''))=lower(replace(effect.AccountingAssociationId,'-',''))
                      AND ($cutoff IS NULL OR julianday(COALESCE(run.CompletedAt,run.StartedAt))<=julianday($cutoff)))
                ORDER BY RunKey LIMIT 100
                """;

    private static async Task<string> GuildPhysicalAccountingPinAsync(string runIdentity, SqliteConnection connection,
        SqliteTransaction transaction, bool hasGuildEffects, DateTimeOffset? cutoff, CancellationToken cancellationToken)
    {
        if (!hasGuildEffects) return "0";

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "CREATE TEMP TABLE IF NOT EXISTS guild_retention_blocked_runs(RunKey TEXT PRIMARY KEY); DELETE FROM guild_retention_blocked_runs";

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        string after = "";

        while (true)
        {
            command.CommandText = GuildPhysicalRetentionRunPageSql;

            command.Parameters.Clear();

            command.Parameters.AddWithValue("$after", after);

            command.Parameters.AddWithValue("$cutoff", cutoff is { } instant ? FormatTimestamp(instant) : DBNull.Value);

            List<string> page = [];

            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) page.Add(reader.GetString(0));
            }

            if (page.Count == 0) break;

            Dictionary<string, bool> proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, transaction, page, cancellationToken).ConfigureAwait(false);

            foreach ((string key, bool clear) in proof)
            {
                if (clear) continue;

                command.CommandText = "INSERT OR IGNORE INTO guild_retention_blocked_runs(RunKey) VALUES($key)";

                command.Parameters.Clear();

                command.Parameters.AddWithValue("$key", key);

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            after = page[^1];
        }

        return $"EXISTS(SELECT 1 FROM guild_retention_blocked_runs blocked WHERE blocked.RunKey=lower(replace({runIdentity},'-','')))";
    }

    private async Task<string> GuildReservationUnlinkedAsync(string reservationAlias, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync("guild_budget_effects", cancellationToken).ConfigureAwait(false)) return "1";

        string normalized = $"lower(replace({reservationAlias}.Id,'-',''))";

        string canonical = $"upper(substr({normalized},1,8)||'-'||substr({normalized},9,4)||'-'||substr({normalized},13,4)||'-'||substr({normalized},17,4)||'-'||substr({normalized},21,12))";

        return $"NOT EXISTS(SELECT 1 FROM guild_budget_effects effect WHERE effect.ReservationId={canonical})";
    }

    private async Task<string> GuildOperationLinkPinAsync(CancellationToken cancellationToken) =>
        await TableExistsAsync("guild_quests", cancellationToken).ConfigureAwait(false)
            ? "AND NOT EXISTS(SELECT 1 FROM guild_quests c WHERE c.OperationId=LongRunningOperations.Id)"
            : string.Empty;

    private async Task<DataRetentionConflict[]> ReadGuildResetConflictsAsync(DbConnection connection, DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        bool quests = await TableExistsAsync("guild_quests", cancellationToken).ConfigureAwait(false);

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = quests
            ? $"SELECT c.Id,0 FROM guild_quests c WHERE {GuildQuestRetentionPins.Unresolved}"
                + " UNION SELECT operation.Id,0 FROM LongRunningOperations operation WHERE operation.Kind='guild-quest'"
                + " AND NOT EXISTS(SELECT 1 FROM guild_quests c WHERE c.OperationId=operation.Id)"
            : "SELECT Id,0 FROM LongRunningOperations WHERE Kind='guild-quest'";

        if (await TableExistsAsync("guild_local_executions", cancellationToken).ConfigureAwait(false))
        {
            command.CommandText += quests
                ? $" UNION SELECT b.QuestId,0 FROM guild_local_executions b WHERE NOT ({GuildLocalExecutionStore.BindingProof})"
                : " UNION SELECT QuestId,0 FROM guild_local_executions";
        }

        if (await TableExistsAsync("guild_local_wait_checkpoints", cancellationToken).ConfigureAwait(false))
        {
            command.CommandText += quests
                ? $" UNION SELECT w.QuestId,0 FROM guild_local_wait_checkpoints w WHERE NOT ({GuildQuestStore.WaitCheckpointProof})"
                : " UNION SELECT QuestId,0 FROM guild_local_wait_checkpoints";
        }

        if (await TableExistsAsync("guild_inbox", cancellationToken).ConfigureAwait(false))
        {
            command.CommandText += " UNION SELECT i.Id,0 FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId WHERE "
                + GuildQuestRetentionPins.UnresolvedPortalInbox
                + " UNION SELECT ReceiptId,0 FROM guild_portal_outbox WHERE DeliveryStateCode=1";
        }

        if (await TableExistsAsync("guild_cohorts", cancellationToken).ConfigureAwait(false))
        {
            command.CommandText += " UNION SELECT Id,2 FROM guild_cohorts WHERE StateCode IN(1,2,3)";
        }

        if (await TableExistsAsync("guild_cohort_stop_frontiers", cancellationToken).ConfigureAwait(false))
        {
            command.CommandText += " UNION SELECT CohortId,2 FROM guild_cohort_stop_frontiers WHERE Completed=0";
        }

        if (await TableExistsAsync("guild_workspace_leases", cancellationToken).ConfigureAwait(false))
        {
            command.CommandText += " UNION SELECT Id,1 FROM guild_workspace_leases WHERE StateCode IS NOT 3 OR InFlightMutationCount IS NOT 0";
        }

        List<DataRetentionConflict> conflicts = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            conflicts.Add(new(ErrorCodes.Guild.ReconciliationRequired, Guid.Parse(reader.GetString(0)).ToString("D"),
                reader.GetInt64(1) == 1 ? "A Guild workspace lease retains live or unresolved physical mutation ownership."
                    : reader.GetInt64(1) == 2 ? "A Cohort retains unresolved convergence or cancellation work."
                    : "A Guild Quest retains unresolved execution, delivery or accounting evidence."));
        }

        return [.. conflicts];
    }

    private const string GuildContentCandidatePrefix = "guild-content:";

    private const string GuildRecordCandidatePrefix = "guild-record:";

    private const string GuildContentRetentionPredicate = """
        content.ContentKindCode = 0 AND content.SensitivityCode = 0 AND content.CreatedAtUtc <= @cutoff
        AND NOT EXISTS (SELECT 1 FROM guild_agent_cards card WHERE card.ContentId = content.ContentId)
        AND NOT EXISTS (SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 17
            AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(content.ContentId, '-', '')))
        """;

    private async Task<string> GuildContentPredicateAsync(CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync("guild_quests", cancellationToken).ConfigureAwait(false))
        {
            return GuildContentRetentionPredicate;
        }

        return $"""
            content.SensitivityCode=0 AND content.CreatedAtUtc<=@cutoff
            AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode=17
                AND lower(replace(label.ArtifactId,'-',''))=lower(replace(content.ContentId,'-','')))
            AND NOT EXISTS(SELECT 1 FROM guild_envelopes envelope
                JOIN guild_inbox delivery ON delivery.EnvelopeId=envelope.Id
                JOIN guild_portal_inputs input ON input.InboxId=delivery.Id
                LEFT JOIN guild_quests c ON c.Id=input.RecipientQuestId
                WHERE envelope.ContentId=content.ContentId AND (c.Id IS NULL OR {GuildQuestRetentionPins.NonAccountingUnresolved}))
            AND ((content.ContentKindCode=0 AND NOT EXISTS(SELECT 1 FROM guild_agent_cards card WHERE card.ContentId=content.ContentId))
              OR (content.ContentKindCode IN(1,2,9,10) AND EXISTS(
                SELECT 1 FROM guild_quests c WHERE
                  ((content.ContentKindCode=9 AND EXISTS(SELECT 1 FROM guild_local_executions local WHERE local.QuestId=c.Id AND local.InputContentId=content.ContentId))
                   OR (content.ContentKindCode=10 AND EXISTS(SELECT 1 FROM guild_local_wait_checkpoints checkpoint WHERE checkpoint.QuestId=c.Id AND checkpoint.ContentId=content.ContentId))
                   OR (content.ContentKindCode=1 AND c.GoalContentId=content.ContentId)
                   OR (content.ContentKindCode=2 AND EXISTS(SELECT 1 FROM guild_result_receipts result WHERE result.QuestId=c.Id AND result.ContentId=content.ContentId)))
                  AND NOT {GuildQuestRetentionPins.NonAccountingUnresolved}
                  AND NOT EXISTS(SELECT 1 FROM guild_quests child WHERE child.ParentQuestId=c.Id)
                  AND NOT EXISTS(SELECT 1 FROM guild_result_applications application WHERE application.QuestId=c.Id)))
              OR (content.ContentKindCode=3 AND EXISTS(SELECT 1 FROM guild_envelopes envelope WHERE envelope.ContentId=content.ContentId)
                AND EXISTS(SELECT 1 FROM guild_inbox delivery JOIN guild_envelopes envelope ON envelope.Id=delivery.EnvelopeId
                    WHERE envelope.ContentId=content.ContentId)
                AND NOT EXISTS(SELECT 1 FROM guild_inbox delivery JOIN guild_envelopes envelope ON envelope.Id=delivery.EnvelopeId
                    LEFT JOIN guild_portal_receipts receipt ON receipt.InboxId=delivery.Id
                    WHERE envelope.ContentId=content.ContentId AND (delivery.DeliveryStateCode=1 OR delivery.AcknowledgedAtUtc IS NULL
                        OR receipt.InboxId IS NULL OR receipt.Digest<>envelope.IntentDigest
                        OR (delivery.DeliveryStateCode=3 AND receipt.EffectKindCode<>3)
                        OR (delivery.DeliveryStateCode=2 AND receipt.EffectKindCode=3)))))
            """;
    }

    private static async Task<bool> GuildContentAccountingClearWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, string contentId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "CREATE TEMP TABLE IF NOT EXISTS guild_retention_quest_proof(QuestId TEXT PRIMARY KEY,Clear INTEGER NOT NULL)";

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        string after = "";

        while (true)
        {
            command.CommandText = """
                WITH references_by_quest(QuestId) AS (
                  SELECT Id FROM guild_quests WHERE GoalContentId=$content
                  UNION SELECT QuestId FROM guild_result_receipts WHERE ContentId=$content
                  UNION SELECT QuestId FROM guild_local_executions WHERE InputContentId=$content
                  UNION SELECT QuestId FROM guild_local_wait_checkpoints WHERE ContentId=$content
                  UNION SELECT i.RecipientQuestId FROM guild_envelopes e JOIN guild_inbox d ON d.EnvelopeId=e.Id
                    JOIN guild_portal_inputs i ON i.InboxId=d.Id WHERE e.ContentId=$content)
                SELECT r.QuestId,c.RootQuestId FROM references_by_quest r LEFT JOIN guild_quests c ON c.Id=r.QuestId
                WHERE r.QuestId>$after ORDER BY r.QuestId LIMIT 100
                """;

            command.Parameters.Clear();

            command.Parameters.AddWithValue("$after", after);

            command.Parameters.AddWithValue("$content", contentId);

            List<(string Quest, string Root)> page = [];

            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    page.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
            }

            if (page.Count == 0) return true;

            foreach ((string quest, string root) in page)
            {
                command.CommandText = "SELECT Clear FROM guild_retention_quest_proof WHERE QuestId=$quest";

                command.Parameters.Clear();

                command.Parameters.AddWithValue("$quest", quest);

                object? cached = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                bool clear = cached is long value ? value == 1
                    : Guid.TryParseExact(root, "D", out Guid rootId) && Guid.TryParseExact(quest, "D", out Guid questId)
                      && await GuildQuestStore.AccountingClearWithinAsync(connection, transaction, rootId, questId, cancellationToken).ConfigureAwait(false);

                if (cached is null)
                {
                    command.CommandText = "INSERT INTO guild_retention_quest_proof(QuestId,Clear) VALUES($quest,$clear)";

                    command.Parameters.AddWithValue("$clear", clear ? 1 : 0);

                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!clear) return false;
            }

            after = page[^1].Quest;
        }
    }

    private async Task AddGuildCandidatesAsync(RetentionSettings retention, int limit,
        List<DataRetentionPlanItem> items, List<string> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count >= limit || !await TableExistsAsync("guild_content", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        string contentPredicate = await GuildContentPredicateAsync(cancellationToken).ConfigureAwait(false);

        bool quests = await TableExistsAsync("guild_quests", cancellationToken).ConfigureAwait(false);

        if (retention.GuildContent.Enabled)
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            await using SqliteCommand reset = connection.CreateCommand();

            reset.Transaction = transaction;

            reset.CommandText = "CREATE TEMP TABLE IF NOT EXISTS guild_retention_quest_proof(QuestId TEXT PRIMARY KEY,Clear INTEGER NOT NULL); DELETE FROM guild_retention_quest_proof";

            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            string afterAt = "", afterId = "";

            while (candidates.Count < limit)
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = $"SELECT content.ContentId,content.CreatedAtUtc FROM guild_content content WHERE {contentPredicate} AND (content.CreatedAtUtc>@afterAt OR content.CreatedAtUtc=@afterAt AND content.ContentId>@afterId) ORDER BY content.CreatedAtUtc,content.ContentId LIMIT 100";

                Add(command, "@cutoff", FormatTimestamp(PrunePlanningTimestamp.AddDays(-ArcanumSettingClamps.RetentionRuleDays(retention.GuildContent.Days))));

                Add(command, "@afterAt", afterAt);

                Add(command, "@afterId", afterId);

                List<(string Id, string Created)> page = [];

                await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    page.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
                }

                if (page.Count == 0) break;

                foreach ((string id, string created) in page)
                {
                    afterAt = created;

                    afterId = id;

                    if (quests && !await GuildContentAccountingClearWithinAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)) continue;

                    candidates.Add(GuildContentCandidatePrefix + Guid.Parse(id).ToString("D").ToUpperInvariant());

                    items.Add(new(RetentionDataClass.GuildContent, 1, 0, 0,
                        await MeasureGuildProjectionsAsync(connection, transaction, id, false, cancellationToken).ConfigureAwait(false)));

                    if (candidates.Count >= limit) break;
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (retention.GuildRecords.Enabled && candidates.Count < limit)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT CardId FROM guild_agent_cards WHERE Available = 0 AND UpdatedAtUtc <= @cutoff"
                + (quests ? " AND NOT EXISTS(SELECT 1 FROM guild_quests c WHERE c.CardId=guild_agent_cards.CardId)" : string.Empty)
                + " ORDER BY UpdatedAtUtc, CardId LIMIT @limit;";

            Add(command, "@cutoff", FormatTimestamp(PrunePlanningTimestamp.AddDays(-ArcanumSettingClamps.RetentionRuleDays(retention.GuildRecords.Days))));

            Add(command, "@limit", limit - candidates.Count);

            List<string> cardIds = [];

            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cardIds.Add(reader.GetString(0));
                }
            }

            foreach (string id in cardIds)
            {
                await using SqliteCommand overlaps = connection.CreateCommand();

                overlaps.CommandText = "SELECT ContentId FROM guild_agent_card_revisions WHERE CardId=@id;";

                Add(overlaps, "@id", id);

                bool selectedContent = false;

                await using (SqliteDataReader reader = await overlaps.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        selectedContent |= candidates.Contains(GuildContentCandidatePrefix + Guid.Parse(reader.GetString(0)).ToString("D").ToUpperInvariant(), StringComparer.Ordinal);
                    }
                }

                if (selectedContent)
                {
                    continue;
                }

                candidates.Add(GuildRecordCandidatePrefix + Guid.Parse(id).ToString("D").ToUpperInvariant());

                items.Add(new(RetentionDataClass.GuildRecords, 1, 0, 0,
                    await MeasureGuildProjectionsAsync(connection, null, id, true, cancellationToken).ConfigureAwait(false)));
            }
        }
    }

    private static async Task<long> MeasureGuildProjectionsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string id, bool card, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = card
            ? "SELECT (SELECT COUNT(*) FROM guild_agent_card_revisions WHERE CardId=@id)+(SELECT COUNT(*) FROM guild_agent_card_skills WHERE CardId=@id);"
            : "SELECT (SELECT COUNT(*) FROM guild_agent_card_revisions WHERE ContentId=@id)+(SELECT COUNT(*) FROM guild_agent_card_skills WHERE (CardId,Revision) IN (SELECT CardId,Revision FROM guild_agent_card_revisions WHERE ContentId=@id));";

        Add(command, "@id", id);

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    internal async Task<CandidateDeleteResult> DeleteGuildCandidateAsync(Guid id, bool card,
        DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(connection, cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = (SqliteTransaction)transaction;

        bool quests = await TableExistsAsync("guild_quests", cancellationToken).ConfigureAwait(false);

        string contentPredicate = await GuildContentPredicateAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = card
            ? "SELECT COUNT(*) FROM guild_agent_cards WHERE CardId = @id AND Available = 0 AND UpdatedAtUtc <= @cutoff"
                + (quests ? " AND NOT EXISTS(SELECT 1 FROM guild_quests c WHERE c.CardId=guild_agent_cards.CardId)" : string.Empty) + ";"
            : $"SELECT COUNT(*) FROM guild_content content WHERE content.ContentId = @id AND {contentPredicate};";

        Add(command, "@id", id.ToString("D").ToUpperInvariant());

        Add(command, "@cutoff", FormatTimestamp(cutoff));

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 1L)
        {
            return CandidateDeleteResult.Empty;
        }

        if (!card && quests)
        {
            await using SqliteCommand reset = connection.CreateCommand();

            reset.Transaction = (SqliteTransaction)transaction;

            reset.CommandText = "CREATE TEMP TABLE IF NOT EXISTS guild_retention_quest_proof(QuestId TEXT PRIMARY KEY,Clear INTEGER NOT NULL); DELETE FROM guild_retention_quest_proof";

            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (!await GuildContentAccountingClearWithinAsync(connection, (SqliteTransaction)transaction,
                id.ToString("D").ToUpperInvariant(), cancellationToken).ConfigureAwait(false)) return CandidateDeleteResult.Empty;
        }

        if (!card)
        {
            Result unlabeled = await labeledArtifactGuard.EnsureUnlabeledAsync(SensitiveArtifactKind.GuildContent,
                id, connection, transaction, cancellationToken).ConfigureAwait(false);

            if (unlabeled.IsFailure)
            {
                return CandidateDeleteResult.Empty;
            }
        }

        if (!card)
        {
            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, (SqliteTransaction)transaction,
                id.ToString("N"), cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand deletion = connection.CreateCommand();

        deletion.Transaction = (SqliteTransaction)transaction;

        Add(deletion, "@id", id.ToString("D").ToUpperInvariant());

        deletion.CommandText = card
            ? "DELETE FROM guild_agent_card_skills WHERE CardId = @id; DELETE FROM guild_agent_card_revisions WHERE CardId = @id; DELETE FROM guild_agent_cards WHERE CardId = @id;"
            : "DELETE FROM guild_agent_card_skills WHERE (CardId, Revision) IN (SELECT CardId, Revision FROM guild_agent_card_revisions WHERE ContentId = @id); DELETE FROM guild_agent_card_revisions WHERE ContentId = @id; DELETE FROM guild_content WHERE ContentId = @id;";

        long removed = await deletion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new(1, 0, 0, Math.Max(0, removed - 1), true);
    }
}
