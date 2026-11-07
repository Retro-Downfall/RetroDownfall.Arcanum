using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    internal Task<Result<int>> DrainOutboxAsync(GuildCallerContext caller, Guid root, CancellationToken ct)
    {
        if (caller?.Kind != GuildCallerKind.Operator || root == Guid.Empty
            || caller.InstallationGeneration != policy.InstallationGeneration)
            return Task.FromResult(Result<int>.Failure(Forbidden));

        return authority.InQuestTransactionAsync<int>(root, async (connection, transaction, signets, token) =>
        {
            Result<GuildQuestStored> found = await GuildQuestStore.ReadWithinAsync(connection, transaction, root, token).ConfigureAwait(false);

            if (found.IsFailure) return found.Error;

            if (found.Value.Quest.RootQuestId != root) return Forbidden;

            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, found.Value, token).ConfigureAwait(false);

            if (visible.IsFailure) return visible.Error;

            List<(GuildReceiptSourceKind Kind, Guid Receipt, Guid Quest)> pending = [];

            await using (SqliteCommand read = Command(connection, transaction, """
                SELECT o.SourceKindCode,o.ReceiptId,o.QuestId FROM guild_portal_outbox o
                JOIN guild_quests q ON q.Id=o.QuestId WHERE q.RootQuestId=$root AND o.DeliveryStateCode=1
                ORDER BY o.CreatedAtUtc,o.SourceKindCode,o.ReceiptId LIMIT 100
                """))
            {
                Add(read, "$root", Id(root));

                await using SqliteDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    pending.Add(((GuildReceiptSourceKind)reader.GetInt32(0), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2))));
            }

            foreach (var item in pending)
            {
                Result<GuildQuestStored> source = await GuildQuestStore.ReadWithinAsync(connection, transaction, item.Quest, token).ConfigureAwait(false);

                if (source.IsFailure) return source.Error;

                // The durable source row is the only authority to construct a host receipt. A retry
                // runs this delivery transaction, never the child execution or its settlement again.
                GuildEnvelopeKind kind = item.Kind switch
                {
                    GuildReceiptSourceKind.QuestIssue => GuildEnvelopeKind.QuestIssued,
                    GuildReceiptSourceKind.Result => GuildEnvelopeKind.Result,
                    GuildReceiptSourceKind.Settlement => GuildEnvelopeKind.Settle,
                    _ => (GuildEnvelopeKind)(-1),
                };

                if (!Enum.IsDefined(kind)) return Unavailable;

                GuildCircleAddress address = source.Value.Quest.ParentQuestId is { } parent
                    ? new(GuildCircleAddressKind.Parent, parent, null, null)
                    : new(GuildCircleAddressKind.Operator, null, null, null);

                string receiptSource = item.Kind switch
                {
                    GuildReceiptSourceKind.QuestIssue => "questIssue",
                    GuildReceiptSourceKind.Result => "result",
                    _ => "settlement",
                };

                GuildCallerContext producer = new(GuildCallerKind.HostReceipt, item.Quest, caller.InvocationId,
                    caller.InstallationGeneration, receiptId: item.Receipt, receiptSource: receiptSource);

                // A permanent refusal may follow an attempted content write. Roll that attempt
                // back before recording withholding; unrelated admitted sources may still progress.
                await using SqliteCommand savepoint = Command(connection, transaction, "SAVEPOINT guild_portal_delivery");

                _ = await savepoint.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                Result<(string MimeType, string Body)> notice = await ReadHostNoticeWithinAsync(connection, transaction,
                    source.Value, kind, token).ConfigureAwait(false);

                Result<GuildMessageReceipt> published = notice.IsFailure ? notice.Error
                    : await PublishWithinAsync(connection, transaction, signets, producer, item.Quest,
                        new(item.Receipt, address, kind, notice.Value.MimeType, notice.Value.Body, null), token).ConfigureAwait(false);

                if (published.IsFailure)
                {
                    if (published.Error.Code is not (ErrorCodes.Guild.Rejected or ErrorCodes.Guild.AuthorityRevoked
                        or ErrorCodes.Guild.AuthorityExpired or ErrorCodes.Guild.ContentErased or ErrorCodes.Guild.OwnerUnavailable))
                        return published.Error;

                    savepoint.CommandText = "ROLLBACK TO guild_portal_delivery";

                    _ = await savepoint.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                savepoint.CommandText = "RELEASE guild_portal_delivery";

                _ = await savepoint.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await using SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_portal_outbox SET DeliveryStateCode=$state,EnvelopeId=$envelope
                    WHERE SourceKindCode=$kind AND ReceiptId=$receipt AND QuestId=$quest AND DeliveryStateCode=1
                    """);

                Add(update, "$state", published.IsSuccess ? 2 : 3);

                Add(update, "$envelope", published.IsSuccess ? Id(published.Value.MessageId) : null);

                Add(update, "$kind", (int)item.Kind);

                Add(update, "$receipt", Id(item.Receipt));

                Add(update, "$quest", Id(item.Quest));

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;

                if (published.IsFailure && item.Kind == GuildReceiptSourceKind.Result)
                {
                    await using SqliteCommand withhold = Command(connection, transaction, """
                        UPDATE guild_result_outbox SET DeliveryStateCode=3,Revision=Revision+1,AcknowledgedAtUtc=$now
                        WHERE QuestId=$quest AND ResultId=$result AND DeliveryStateCode=1 AND Revision<9223372036854775807
                        RETURNING Revision
                        """);

                    Add(withhold, "$quest", Id(item.Quest));

                    Add(withhold, "$result", Id(item.Receipt));

                    Add(withhold, "$now", UtcInstantText.Format(time.GetUtcNow()));

                    if (await withhold.ExecuteScalarAsync(token).ConfigureAwait(false) is long revision)
                    {
                        Result<long> projection = await GuildPortalEvents.AppendWithinAsync(connection, transaction, root,
                            item.Quest, GuildEventKind.QuestChanged, item.Receipt, revision, null, time.GetUtcNow(), token,
                            GuildPortalEventSource.ResultDelivery).ConfigureAwait(false);

                        if (projection.IsFailure) return projection.Error;
                    }
                    else
                    {
                        await using SqliteCommand pendingResult = Command(connection, transaction,
                            "SELECT 1 FROM guild_result_outbox WHERE QuestId=$quest AND ResultId=$result AND DeliveryStateCode=1");

                        Add(pendingResult, "$quest", Id(item.Quest));

                        Add(pendingResult, "$result", Id(item.Receipt));

                        if (await pendingResult.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) return Exhausted;
                    }
                }
            }

            return pending.Count;
        }, ct);
    }

    internal async Task<Result<GuildCircleAcknowledgement>> AcknowledgeOperatorAsync(GuildCallerContext caller, Guid envelopeId, CancellationToken ct)
    {
        if (caller?.Kind != GuildCallerKind.Operator || envelopeId == Guid.Empty
            || caller.InstallationGeneration != policy.InstallationGeneration) return Forbidden;

        // Discover immutable routing before taking the exact root gate. The owning transaction
        // reloads the envelope and all current authority before acknowledging anything.
        Result<GuildEnvelope> located = await authority.InQuestTransactionAsync<GuildEnvelope>(envelopeId,
            (connection, transaction, _, token) => ReadEnvelopeWithinAsync(connection, transaction, envelopeId, token), ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildCircleAcknowledgement>(located.Value.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildEnvelope> envelope = await ReadEnvelopeWithinAsync(connection, transaction, envelopeId, token).ConfigureAwait(false);

                if (envelope.IsFailure) return envelope.Error;

                Result visible = await RequireReaderWithinAsync(connection, transaction, signets, caller, envelope.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                string recipient = "operator:" + Id(caller.PrincipalId);

                Guid inbox;

                long revision;

                await using (SqliteCommand read = Command(connection, transaction, """
                    SELECT Id,Revision FROM guild_inbox WHERE EnvelopeId=$envelope
                      AND RecipientKey=$recipient AND RecipientQuestId IS NULL
                    """))
                {
                    Add(read, "$envelope", Id(envelopeId));

                    Add(read, "$recipient", recipient);

                    await using SqliteDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Forbidden;

                    inbox = Guid.Parse(reader.GetString(0));

                    revision = reader.GetInt64(1);
                }

                Result<GuildCircleAcknowledgement?> prior = await ReadApplicationWithinAsync(connection, transaction,
                    inbox, envelopeId, recipient, token).ConfigureAwait(false);

                if (prior.IsFailure) return prior.Error;

                if (prior.Value is { } accepted) return accepted;

                Result<byte[]> disclosure = await ReadBodyWithinAsync(connection, transaction, signets, caller, envelope.Value, token).ConfigureAwait(false);

                if (disclosure.IsFailure) return disclosure.Error;

                if (envelope.Value.Kind == GuildEnvelopeKind.Result)
                {
                    byte[] digest;

                    long deliveryRevision;

                    await using (SqliteCommand result = Command(connection, transaction, """
                        SELECT Digest,Revision FROM guild_result_outbox
                        WHERE QuestId=$quest AND ResultId=$result AND RecipientQuestId IS NULL
                        """))
                    {
                        Add(result, "$quest", Id(envelope.Value.QuestId));

                        Add(result, "$result", Id(envelope.Value.Producer.ReceiptId));

                        await using SqliteDataReader reader = await result.ExecuteReaderAsync(token).ConfigureAwait(false);

                        if (!await reader.ReadAsync(token).ConfigureAwait(false)
                            || reader.GetValue(0) is not byte[] bytes || bytes.Length != 32) return Unavailable;

                        digest = bytes;

                        deliveryRevision = reader.GetInt64(1);
                    }

                    Result<GuildDeliveryState> acknowledged = await quests.AcknowledgeRootResultWithinAsync(connection, transaction,
                        caller, envelope.Value.QuestId, envelope.Value.Producer.ReceiptId!.Value,
                        Convert.ToHexString(digest), deliveryRevision, token).ConfigureAwait(false);

                    if (acknowledged.IsFailure) return acknowledged.Error;
                }

                return await CommitInboxWithinAsync(connection, transaction, inbox, envelopeId, revision, Guid.NewGuid(),
                    2, null, time.GetUtcNow(), token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }
}
