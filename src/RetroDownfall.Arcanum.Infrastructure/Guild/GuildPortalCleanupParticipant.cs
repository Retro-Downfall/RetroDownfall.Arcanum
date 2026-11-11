using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Portal evidence shares the existing Guild owner cleanup transaction and cursor.</summary>
internal sealed class GuildPortalCleanupParticipant(TimeProvider clock) : IGuildOwnerCleanupParticipant
{
    public async ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        Result withheld = await GuildPortalWithholding.WithholdWithinAsync(connection, transaction, Affected(owner),
            InboxPredicate(owner), owner.Id, ErrorCodes.Guild.OwnerUnavailable, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);

        if (withheld.IsFailure)
        {
            return withheld;
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = Affected(owner) + """
            UPDATE guild_portal_outbox SET DeliveryStateCode=3
            WHERE DeliveryStateCode=1 AND QuestId IN(SELECT Id FROM affected);
            """;

        command.Parameters.AddWithValue("$identity", Id(owner.Id));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    public ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Portal effects are wholly transactional. Finalize rechecks the sealed obligations under
        // the same writer lock as every family participant and the shared cursor.
        return ValueTask.FromResult(Result.Success());
    }

    public async ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = Affected(owner) + "SELECT (SELECT COUNT(*) FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId"
            + $" WHERE ({GuildQuestRetentionPins.UnresolvedPortalInbox}) AND ({InboxPredicate(owner)}))"
            + "+(SELECT COUNT(*) FROM guild_portal_outbox WHERE DeliveryStateCode=1 AND QuestId IN(SELECT Id FROM affected))";

        command.Parameters.AddWithValue("$identity", Id(owner.Id));

        return Equals(0L, await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            ? Result.Success()
            : new Error(ErrorCodes.Guild.ReconciliationRequired, "Guild owner cleanup retains unresolved Portal delivery evidence.");
    }

    internal static Task<Result> WithholdRevokedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid questId, DateTimeOffset now, CancellationToken cancellationToken) =>
        GuildPortalWithholding.WithholdWithinAsync(connection, transaction, """
            WITH RECURSIVE affected(Id) AS (
                SELECT Id FROM guild_quests WHERE Id=$identity
                UNION SELECT child.Id FROM guild_quests child JOIN affected parent ON child.ParentQuestId=parent.Id)
            """, "i.RecipientQuestId IN(SELECT Id FROM affected)", questId, ErrorCodes.Guild.AuthorityRevoked, now, cancellationToken);

    internal static Task<Result> WithholdTerminalWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid questId, DateTimeOffset now, CancellationToken cancellationToken) =>
        GuildPortalWithholding.WithholdWithinAsync(connection, transaction, string.Empty, "i.RecipientQuestId=$identity",
            questId, ErrorCodes.Guild.Rejected, now, cancellationToken);

    private static string Affected(GuildCleanupOwner owner) => """
        WITH RECURSIVE affected(Id) AS (
            SELECT c.Id FROM guild_quests c WHERE
        """ + " " + (owner.Kind == 1 ? "(c.CampaignId=$identity COLLATE NOCASE OR c.WorkspaceId=$identity COLLATE NOCASE)"
            : "c.SessionId=$identity COLLATE NOCASE") + " " + """
            UNION SELECT child.Id FROM guild_quests child JOIN affected parent ON child.ParentQuestId=parent.Id)
        """;

    private static string InboxPredicate(GuildCleanupOwner owner) =>
        "i.RecipientQuestId IN(SELECT Id FROM affected) OR e.QuestId IN(SELECT Id FROM affected) OR "
        + (owner.Kind == 1 ? "e.CampaignId=$identity COLLATE NOCASE" : "e.SessionId=$identity COLLATE NOCASE");

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();
}

/// <summary>Durable withholding and body-erasure evidence, without reconstituting an envelope body.</summary>
internal static class GuildPortalWithholding
{
    private sealed record Pending(Guid InboxId, long Revision, byte[] Digest, Guid EnvelopeId,
        Guid RootQuestId, Guid SourceQuestId, Guid? RecipientQuestId, GuildEnvelopeKind Kind);

    internal static async Task PrepareContentErasureWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid contentId, long revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Result withheld = await WithholdWithinAsync(connection, transaction, string.Empty, "e.ContentId=$identity",
            contentId, ErrorCodes.Guild.ContentErased, now, cancellationToken).ConfigureAwait(false);

        if (withheld.IsFailure)
        {
            throw new InvalidDataException(withheld.Error.Message);
        }

        await using SqliteCommand sources = connection.CreateCommand();

        sources.Transaction = transaction;

        sources.CommandText = """
            UPDATE guild_result_outbox SET DeliveryStateCode=3,Revision=Revision+1,AcknowledgedAtUtc=$now
            WHERE DeliveryStateCode=1 AND QuestId IN(
                SELECT QuestId FROM guild_envelopes WHERE ContentId=$content AND KindCode=2);
            UPDATE guild_portal_outbox SET DeliveryStateCode=3
            WHERE DeliveryStateCode=1 AND
                ((SourceKindCode=0 AND QuestId IN(SELECT Id FROM guild_quests WHERE GoalContentId=$content))
                 OR (SourceKindCode IN(1,2) AND QuestId IN(
                    SELECT QuestId FROM guild_result_receipts WHERE ContentId=$content
                    UNION SELECT QuestId FROM guild_envelopes WHERE ContentId=$content AND KindCode=2)));
            """;

        sources.Parameters.AddWithValue("$content", Id(contentId));

        UtcInstantSql.AddParameter(sources, "$now", now);

        _ = await sources.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        string after = string.Empty;

        while (true)
        {
            List<(Guid Envelope, Guid Root, Guid Quest)> references = [];

            await using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;

                read.CommandText = "SELECT Id,RootQuestId,QuestId FROM guild_envelopes WHERE ContentId=$content AND Id>$after ORDER BY Id LIMIT 100";

                read.Parameters.AddWithValue("$content", Id(contentId));

                read.Parameters.AddWithValue("$after", after);

                await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    references.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2))));
                }
            }

            if (references.Count == 0)
            {
                break;
            }

            foreach (var reference in references)
            {
                Result<long> appended = await GuildPortalEvents.AppendWithinAsync(connection, transaction, reference.Root,
                    reference.Quest, GuildEventKind.ContentErased, reference.Envelope, revision, reference.Envelope,
                    now, cancellationToken, GuildPortalEventSource.Envelope).ConfigureAwait(false);

                if (appended.IsFailure)
                {
                    throw new InvalidDataException(appended.Error.Message);
                }
            }

            after = Id(references[^1].Envelope);
        }

        after = string.Empty;

        while (true)
        {
            List<(Guid Root, Guid Quest)> references = [];

            await using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;

                read.CommandText = """
                    WITH refs(RootQuestId,QuestId) AS (
                        SELECT RootQuestId,Id FROM guild_quests WHERE GoalContentId=$content
                        UNION SELECT c.RootQuestId,c.Id FROM guild_quests c JOIN guild_result_receipts r ON r.QuestId=c.Id WHERE r.ContentId=$content
                        UNION SELECT c.RootQuestId,c.Id FROM guild_quests c JOIN guild_continuations r ON r.QuestId=c.Id WHERE r.ContentId=$content)
                    SELECT RootQuestId,MIN(QuestId) FROM refs WHERE RootQuestId>$after GROUP BY RootQuestId ORDER BY RootQuestId LIMIT 100
                    """;

                read.Parameters.AddWithValue("$content", Id(contentId));

                read.Parameters.AddWithValue("$after", after);

                await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    references.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))));
                }
            }

            if (references.Count == 0)
            {
                return;
            }

            foreach (var reference in references)
            {
                Result<long> appended = await GuildPortalEvents.AppendWithinAsync(connection, transaction, reference.Root,
                    reference.Quest, GuildEventKind.ContentErased, contentId, revision, null, now, cancellationToken,
                    GuildPortalEventSource.Content).ConfigureAwait(false);

                if (appended.IsFailure)
                {
                    throw new InvalidDataException(appended.Error.Message);
                }
            }

            after = Id(references[^1].Root);
        }
    }

    internal static async Task<Result> WithholdWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        string prefix, string predicate, Guid identity, string reason, DateTimeOffset now, CancellationToken cancellationToken,
        int maximumBatches = int.MaxValue, int batchSize = 100, string? recipientKey = null, bool updateResultOutbox = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBatches, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 100);

        int batches = 0;

        while (true)
        {
            List<Pending> pending = [];

            await using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;

                read.CommandText = prefix + "SELECT i.Id,i.Revision,e.IntentDigest,e.Id,e.RootQuestId,e.QuestId,i.RecipientQuestId,e.KindCode FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId"
                    + $" WHERE i.DeliveryStateCode=1 AND ({predicate}) ORDER BY i.Id LIMIT $batchSize";

                read.Parameters.AddWithValue("$identity", Id(identity));
                read.Parameters.AddWithValue("$batchSize", batchSize);
                read.Parameters.AddWithValue("$recipientKey", (object?)recipientKey ?? DBNull.Value);

                await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    pending.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), (byte[])reader.GetValue(2),
                        Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4)), Guid.Parse(reader.GetString(5)),
                        reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), (GuildEnvelopeKind)reader.GetInt32(7)));
                }
            }

            if (pending.Count == 0)
            {
                return Result.Success();
            }

            foreach (Pending item in pending)
            {
                await using SqliteCommand receipt = connection.CreateCommand();

                receipt.Transaction = transaction;

                receipt.CommandText = """
                    INSERT INTO guild_portal_receipts(InboxId,ApplicationReceiptId,Digest,EffectKindCode,CreatedAtUtc)
                    VALUES($inbox,$receipt,$digest,3,$now) ON CONFLICT(InboxId) DO NOTHING;
                    """;

                receipt.Parameters.AddWithValue("$inbox", Id(item.InboxId));

                receipt.Parameters.AddWithValue("$receipt", Id(Guid.NewGuid()));

                receipt.Parameters.AddWithValue("$digest", item.Digest);

                UtcInstantSql.AddParameter(receipt, "$now", now);

                _ = await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                receipt.CommandText = "SELECT COUNT(*) FROM guild_portal_receipts WHERE InboxId=$inbox AND Digest=$digest AND EffectKindCode=3";

                if (!Equals(1L, await receipt.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
                {
                    return new Error(ErrorCodes.Guild.ReconciliationRequired, "A pending Portal delivery has conflicting application evidence.");
                }

                receipt.CommandText = """
                    UPDATE guild_inbox SET DeliveryStateCode=3,Revision=Revision+1,LeaseOwner=NULL,LeaseRevision=NULL,
                        OperationId=NULL,ReasonCode=$reason,AcknowledgedAtUtc=$now
                    WHERE Id=$inbox AND Revision=$revision AND DeliveryStateCode=1 AND Revision<9223372036854775807;
                    """;

                receipt.Parameters.AddWithValue("$reason", reason);

                receipt.Parameters.AddWithValue("$revision", item.Revision);

                if (await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    return new Error(ErrorCodes.Guild.ReconciliationRequired, "A Portal delivery cannot commit its permanent disposition.");
                }

                if (updateResultOutbox && item.Kind == GuildEnvelopeKind.Result)
                {
                    await using SqliteCommand result = connection.CreateCommand();

                    result.Transaction = transaction;

                    result.CommandText = """
                        UPDATE guild_result_outbox SET DeliveryStateCode=3,Revision=Revision+1,AcknowledgedAtUtc=$now
                        WHERE QuestId=$source AND RecipientQuestId IS $recipient AND DeliveryStateCode=1;
                        """;

                    result.Parameters.AddWithValue("$source", Id(item.SourceQuestId));

                    result.Parameters.AddWithValue("$recipient", item.RecipientQuestId is { } recipient ? Id(recipient) : DBNull.Value);

                    UtcInstantSql.AddParameter(result, "$now", now);

                    _ = await result.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    item.RootQuestId, item.RecipientQuestId ?? item.SourceQuestId, GuildEventKind.MessageCommitted,
                    item.InboxId, item.Revision + 1, item.EnvelopeId, now, cancellationToken,
                    GuildPortalEventSource.Inbox).ConfigureAwait(false);

                if (projected.IsFailure)
                {
                    return projected.Error;
                }
            }

            if (++batches >= maximumBatches) return Result.Success();
        }
    }

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();
}
