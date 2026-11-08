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
            ["guild_workspace_lease_mutations", "guild_workspace_leases", "guild_workspace_authority_state", "guild_budget_evidence", "guild_budget_effects", "guild_quest_budget", "guild_budget_accounts", "guild_memory_attribution", "guild_portal_inputs", "guild_portal_receipts", "guild_inbox", "guild_portal_outbox", "guild_events", "guild_portal_cursors", "guild_envelopes", "guild_portal_streams", "guild_event_streams", "guild_continuations", "guild_result_inputs", "guild_result_applications", "guild_result_outbox", "guild_result_receipts", "guild_dispatches", "guild_quest_cancellations", "guild_local_wait_checkpoints", "guild_local_executions", "guild_quest_start_intents", "guild_quests", "guild_signets", "guild_signet_revocations", "guild_signet_authority_state", "guild_content_erasure_receipts", "guild_owner_cleanup", "guild_roster_refreshes", "guild_roster_cursors", "guild_declared_source_claims", "guild_declared_roots"],
            "Content-free Guild budget, physical spend, erasure and owner cleanup evidence; never aged out on a timer.",
            retention, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GuildPhysicalAccountingPinAsync(string runIdentity, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync("guild_budget_effects", cancellationToken).ConfigureAwait(false))
        {
            return "0";
        }

        string missingEvidence = await TableExistsAsync("guild_budget_evidence", cancellationToken).ConfigureAwait(false)
            ? "NOT EXISTS(SELECT 1 FROM guild_budget_evidence receipt WHERE receipt.EffectId=effect.EffectId AND receipt.AppliedRevision=effect.Revision)"
            : "1";

        return $"""
            EXISTS(SELECT 1 FROM guild_budget_effects effect
                WHERE lower(replace(effect.AccountingAssociationId,'-',''))=lower(replace({runIdentity},'-',''))
                  AND effect.KindCode=0
                  AND (effect.StateCode IN(0,1) OR effect.UnknownUsd=1 OR effect.UnknownTokens=1 OR {missingEvidence}))
            """;
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
                WHERE envelope.ContentId=content.ContentId AND (c.Id IS NULL OR {GuildQuestRetentionPins.ExecutionUnresolved}))
            AND ((content.ContentKindCode=0 AND NOT EXISTS(SELECT 1 FROM guild_agent_cards card WHERE card.ContentId=content.ContentId))
              OR (content.ContentKindCode IN(1,2,9,10) AND EXISTS(
                SELECT 1 FROM guild_quests c WHERE
                  ((content.ContentKindCode=9 AND EXISTS(SELECT 1 FROM guild_local_executions local WHERE local.QuestId=c.Id AND local.InputContentId=content.ContentId))
                   OR (content.ContentKindCode=10 AND EXISTS(SELECT 1 FROM guild_local_wait_checkpoints checkpoint WHERE checkpoint.QuestId=c.Id AND checkpoint.ContentId=content.ContentId))
                   OR (content.ContentKindCode=1 AND c.GoalContentId=content.ContentId)
                   OR (content.ContentKindCode=2 AND EXISTS(SELECT 1 FROM guild_result_receipts result WHERE result.QuestId=c.Id AND result.ContentId=content.ContentId)))
                  AND NOT {GuildQuestRetentionPins.Unresolved}
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
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"SELECT content.ContentId FROM guild_content content WHERE {contentPredicate} ORDER BY content.CreatedAtUtc, content.ContentId LIMIT @limit;";

            Add(command, "@cutoff", FormatTimestamp(PrunePlanningTimestamp.AddDays(-ArcanumSettingClamps.RetentionRuleDays(retention.GuildContent.Days))));

            Add(command, "@limit", limit - candidates.Count);

            List<string> ids = [];

            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(reader.GetString(0));
                }
            }

            foreach (string id in ids)
            {
                candidates.Add(GuildContentCandidatePrefix + Guid.Parse(id).ToString("D").ToUpperInvariant());

                items.Add(new(RetentionDataClass.GuildContent, 1, 0, 0,
                    await MeasureGuildProjectionsAsync(connection, null, id, false, cancellationToken).ConfigureAwait(false)));
            }
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

    private async Task<CandidateDeleteResult> DeleteGuildCandidateAsync(Guid id, bool card,
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
