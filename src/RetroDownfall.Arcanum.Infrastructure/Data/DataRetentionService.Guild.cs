using System.Data.Common;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;

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
            ["guild_content_erasure_receipts", "guild_owner_cleanup", "guild_roster_refreshes", "guild_roster_cursors", "guild_declared_source_claims", "guild_declared_roots"],
            "Content-free Guild erasure and owner cleanup evidence; never aged out on a timer.",
            retention, cancellationToken).ConfigureAwait(false);
    }

    private const string GuildContentCandidatePrefix = "guild-content:";

    private const string GuildRecordCandidatePrefix = "guild-record:";

    // Later Guild slices must add eligibility for their content kinds alongside their active,
    // reconciliation and accounting pins. The roster foundation can authorize only card content.
    private const string GuildContentRetentionPredicate = """
        content.ContentKindCode = 0 AND content.SensitivityCode = 0 AND content.CreatedAtUtc <= @cutoff
        AND NOT EXISTS (SELECT 1 FROM guild_agent_cards card WHERE card.ContentId = content.ContentId)
        AND NOT EXISTS (SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 17
            AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(content.ContentId, '-', '')))
        """;

    private async Task AddGuildCandidatesAsync(RetentionSettings retention, int limit,
        List<DataRetentionPlanItem> items, List<string> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count >= limit || !await TableExistsAsync("guild_content", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (retention.GuildContent.Enabled)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"SELECT content.ContentId FROM guild_content content WHERE {GuildContentRetentionPredicate} ORDER BY content.CreatedAtUtc, content.ContentId LIMIT @limit;";

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

            command.CommandText = "SELECT CardId FROM guild_agent_cards WHERE Available = 0 AND UpdatedAtUtc <= @cutoff ORDER BY UpdatedAtUtc, CardId LIMIT @limit;";

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

        command.CommandText = card
            ? "SELECT COUNT(*) FROM guild_agent_cards WHERE CardId = @id AND Available = 0 AND UpdatedAtUtc <= @cutoff;"
            : $"SELECT COUNT(*) FROM guild_content content WHERE content.ContentId = @id AND {GuildContentRetentionPredicate};";

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

        command.Parameters.Clear();

        Add(command, "@id", id.ToString("D").ToUpperInvariant());

        command.CommandText = card
            ? "DELETE FROM guild_agent_card_skills WHERE CardId = @id; DELETE FROM guild_agent_card_revisions WHERE CardId = @id; DELETE FROM guild_agent_cards WHERE CardId = @id;"
            : "DELETE FROM guild_agent_card_skills WHERE (CardId, Revision) IN (SELECT CardId, Revision FROM guild_agent_card_revisions WHERE ContentId = @id); DELETE FROM guild_agent_card_revisions WHERE ContentId = @id; DELETE FROM guild_content WHERE ContentId = @id;";

        long removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new(1, 0, 0, Math.Max(0, removed - 1), true);
    }
}
