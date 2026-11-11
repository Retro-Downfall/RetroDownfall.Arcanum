using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildCircleInputItem(Guid InboxId, Guid EnvelopeId, long Revision, string PromptDigest);

/// <summary>Ephemeral source observations; only a batch registered by its execution store may enter a provider effect.</summary>
internal sealed class GuildCircleInputBatch(ImmutableArray<GuildCircleInputItem> items, string prompt)
{
    internal ImmutableArray<GuildCircleInputItem> Items { get; } = items;

    internal string Prompt { get; } = prompt;
}

internal sealed partial class GuildQuestStore
{
    private readonly ConditionalWeakTable<GuildCircleInputBatch, GuildLocalInferenceExecution> _circleInputs = new();

    private sealed record PendingCircleInput(Guid Inbox, Guid Envelope, long Revision, Guid Stream, long SenderSequence, long EventSequence);

    private sealed class CircleInputPreparation
    {
        internal long? Snapshot { get; set; }

        internal long After { get; set; }

        internal ImmutableArray<GuildCircleInputItem>.Builder Items { get; } = ImmutableArray.CreateBuilder<GuildCircleInputItem>();

        internal HashSet<Guid> Selected { get; } = [];

        internal StringBuilder Prompt { get; } = new();

        internal int Bytes { get; set; }
    }

    internal async Task<Result<GuildCircleInputBatch?>> PrepareCircleInputAsync(GuildLocalInferenceExecution execution, CancellationToken ct)
    {
        if (execution is null || !ReferenceEquals(execution.Store, this) || !DriverShape(execution.Driver)) return InvalidReceipt;

        CircleInputPreparation state = new();

        while (true)
        {
            var page = await PrepareCircleInputPageAsync(execution, state, ct).ConfigureAwait(false);

            if (page.IsFailure) return page.Error;

            if (page.Value) break;

            // Each completed page returns from its root admission, releasing the root gate,
            // writer transaction and connection. The next admission rechecks the live driver.
        }

        if (state.Items.Count == 0) return Result<GuildCircleInputBatch?>.Success(null);

        GuildCircleInputBatch batch = new(state.Items.ToImmutable(), state.Prompt.ToString());

        _circleInputs.Add(batch, execution);

        return Result<GuildCircleInputBatch?>.Success(batch);
    }

    private Task<Result<bool>> PrepareCircleInputPageAsync(GuildLocalInferenceExecution execution, CircleInputPreparation state, CancellationToken ct) =>
        authority.InQuestTransactionAsync<bool>(execution.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var driver = execution.Driver;
                var allowed = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                GuildPortalStore portal = new(this, authority, policy, time);

                var readerCaller = await portal.DriverCallerWithinAsync(connection, transaction, driver, token).ConfigureAwait(false);

                if (readerCaller.IsFailure) return readerCaller.Error;

                GuildCallerContext caller = readerCaller.Value;

                if (state.Snapshot is null)
                {
                    await using var high = Command(connection, transaction,
                        "SELECT COALESCE(MAX(EventSequence),0) FROM guild_events WHERE RootQuestId=$root");

                    Add(high, "$root", Id(driver.Execution.RootQuestId));

                    if (await high.ExecuteScalarAsync(token).ConfigureAwait(false) is not long value || value < 0) return Unavailable;

                    state.Snapshot = value;
                }

                long snapshot = state.Snapshot.Value;

                long after = state.After;

                if (after >= snapshot || state.Items.Count == 100) return true;

                List<PendingCircleInput> rows = [];

                await using (var query = Command(connection, transaction, """
                    SELECT CASE WHEN typeof(i.Id)='text' AND length(CAST(i.Id AS BLOB))=36 THEN i.Id END,
                        CASE WHEN typeof(i.EnvelopeId)='text' AND length(CAST(i.EnvelopeId AS BLOB))=36 THEN i.EnvelopeId END,
                        i.Revision,CASE WHEN typeof(i.StreamId)='text' AND length(CAST(i.StreamId AS BLOB))=36 THEN i.StreamId END,
                        i.SenderSequence,event.EventSequence
                    FROM guild_events event JOIN guild_envelopes e ON e.Id=event.EnvelopeId AND e.Id=event.ObjectId
                        AND e.RootQuestId=event.RootQuestId
                    JOIN guild_inbox i ON i.EnvelopeId=e.Id
                    WHERE event.RootQuestId=$root AND event.SourceKindCode=3 AND event.EventKindCode=1 AND event.Revision=1
                        AND event.EventSequence>$after AND event.EventSequence<=$snapshot
                        AND i.RecipientQuestId=$quest AND i.RecipientKey=$recipient AND i.DeliveryStateCode=1
                        AND (i.LeaseOwner IS NULL OR i.LeaseOwner=$holder AND i.LeaseRevision=$lease AND i.OperationId=$operation)
                    ORDER BY event.EventSequence,i.Id LIMIT 100
                    """))
                {
                    Add(query, "$root", Id(driver.Execution.RootQuestId));
                    Add(query, "$after", after);
                    Add(query, "$snapshot", snapshot);
                    Add(query, "$quest", Id(driver.Execution.QuestId));
                    Add(query, "$recipient", "quest:" + Id(driver.Execution.QuestId));
                    Add(query, "$holder", driver.LeaseOwner);
                    Add(query, "$lease", driver.OperationRevision);
                    Add(query, "$operation", allowed.Value.Row.Quest.OperationId.ToString("N"));

                    await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(3)
                            || !Guid.TryParseExact(reader.GetString(0), "D", out Guid inbox) || inbox == Guid.Empty
                            || !Guid.TryParseExact(reader.GetString(1), "D", out Guid envelope) || envelope == Guid.Empty
                            || !Guid.TryParseExact(reader.GetString(3), "D", out Guid stream) || stream == Guid.Empty
                            || reader.GetValue(2) is not long revision || revision < 1
                            || reader.GetValue(4) is not long sender || sender < 1
                            || reader.GetValue(5) is not long sequence || sequence <= after || sequence > snapshot) return Unavailable;

                        rows.Add(new(inbox, envelope, revision, stream, sender, sequence));
                    }
                }

                if (rows.Count == 0) return true;

                foreach (var row in rows)
                {
                    if (state.Items.Count == 100) return true;

                    state.After = row.EventSequence;

                    var blocked = await HasUnpreparedCirclePredecessorWithinAsync(connection, transaction, driver, row, state.Selected, token).ConfigureAwait(false);

                    if (blocked.IsFailure) return blocked.Error;

                    if (blocked.Value) continue;

                    var envelope = await GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, row.Envelope, token).ConfigureAwait(false);

                    Result<string> materialized = envelope.IsFailure ? envelope.Error
                        : await portal.ReadInputPromptWithinAsync(connection, transaction, signets, caller, envelope.Value, token).ConfigureAwait(false);

                    if (materialized.IsFailure)
                    {
                        if (await CircleInputAlreadyObservedWithinAsync(connection, transaction, execution, row.Inbox, token).ConfigureAwait(false))
                            return materialized.Error;

                        var permanent = await PermanentPendingCircleFailureWithinAsync(connection, transaction, signets,
                            envelope.IsSuccess ? envelope.Value : null, row.Envelope, materialized.Error, token).ConfigureAwait(false);

                        if (permanent.IsFailure) return permanent.Error;

                        if (permanent.Value is { } reason)
                        {
                            var withheld = await GuildPortalWithholding.WithholdWithinAsync(connection, transaction, string.Empty,
                                "i.Id=$identity AND i.RecipientKey=$recipientKey AND NOT EXISTS(SELECT 1 FROM guild_portal_inputs observed WHERE observed.InboxId=i.Id)",
                                row.Inbox, reason, time.GetUtcNow(), token, maximumBatches: 1, batchSize: 1,
                                recipientKey: "quest:" + Id(driver.Execution.QuestId)).ConfigureAwait(false);

                            if (withheld.IsFailure) return withheld.Error;
                        }

                        // Unaccepted transient input stays pending. Its unselected predecessor
                        // blocks only that stream while the frozen keyset scan reaches other streams.
                        continue;
                    }

                    byte[] text = Encoding.UTF8.GetBytes(materialized.Value);

                    if (text.Length + state.Bytes + 2 > 1_048_576)
                    {
                        if (state.Items.Count != 0) return true;

                        continue;
                    }

                    if (!state.Selected.Add(row.Inbox)) return Unavailable;

                    state.Items.Add(new(row.Inbox, row.Envelope, row.Revision, Convert.ToHexString(SHA256.HashData(text))));
                    state.Prompt.AppendLine(materialized.Value);
                    state.Bytes += text.Length + 2;
                }

                return rows.Count < 100 || state.After >= snapshot || state.Items.Count == 100;
            }, ct);

    private static async Task<Result<bool>> HasUnpreparedCirclePredecessorWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestDriver driver, PendingCircleInput row, HashSet<Guid> selected, CancellationToken ct)
    {
        // Preparation continues only with fewer than 100 selected items. If 100 earlier
        // pending rows exist, at least one is not selected, without storing a blocked-stream set.
        await using var query = Command(connection, transaction, """
            SELECT CASE WHEN typeof(Id)='text' AND length(CAST(Id AS BLOB))=36 THEN Id END
            FROM guild_inbox WHERE RecipientKey=$recipient AND RecipientQuestId=$quest AND StreamId=$stream
                AND DeliveryStateCode=1 AND SenderSequence<$sequence ORDER BY SenderSequence,Id LIMIT 100
            """);

        Add(query, "$recipient", "quest:" + Id(driver.Execution.QuestId));
        Add(query, "$quest", Id(driver.Execution.QuestId));
        Add(query, "$stream", Id(row.Stream));
        Add(query, "$sequence", row.SenderSequence);

        await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || !Guid.TryParseExact(reader.GetString(0), "D", out Guid prior) || prior == Guid.Empty) return Unavailable;

            if (!selected.Contains(prior)) return true;
        }

        return false;
    }

    private async Task<bool> CircleInputAlreadyObservedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildLocalInferenceExecution execution, Guid inboxId, CancellationToken ct)
    {
        await using var query = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM guild_portal_inputs WHERE InboxId=$inbox AND RecipientQuestId=$quest)");

        Add(query, "$inbox", Id(inboxId));
        Add(query, "$quest", Id(execution.Driver.Execution.QuestId));

        if (Equals(1L, await query.ExecuteScalarAsync(ct).ConfigureAwait(false))) return true;

        foreach (var observation in _circleInputs)
            if (ReferenceEquals(observation.Value, execution) && observation.Key.Items.Any(item => item.InboxId == inboxId)) return true;

        return false;
    }

    private static async Task<Result<string?>> PermanentPendingCircleFailureWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, SignetAuthority.QuestTransaction signets, GuildEnvelope? envelope,
        Guid envelopeId, Error error, CancellationToken ct)
    {
        if (error.Code is ErrorCodes.Guild.AuthorityRevoked or ErrorCodes.Guild.AuthorityExpired or ErrorCodes.Guild.OwnerUnavailable)
            return error.Code;

        if (error.Code == ErrorCodes.Guild.ContentErased)
        {
            await using var erased = Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM guild_content_erasure_receipts proof WHERE proof.ContentId IN (
                    SELECT ContentId FROM guild_envelopes WHERE Id=$envelope
                    UNION ALL SELECT offer.GoalContentId FROM guild_distribution_notifications notice
                        JOIN guild_work_offers offer ON offer.Id=notice.ObjectId WHERE notice.EnvelopeId=$envelope AND notice.TypeCode IN(1,4)
                    UNION ALL SELECT bid.NoteContentId FROM guild_distribution_notifications notice
                        JOIN guild_work_bids bid ON bid.Id=notice.ObjectId WHERE notice.EnvelopeId=$envelope AND notice.TypeCode IN(2,3)
                    UNION ALL SELECT note.ContentId FROM guild_distribution_notifications notice
                        JOIN guild_cohort_ledger note ON note.Id=notice.ObjectId WHERE notice.EnvelopeId=$envelope AND notice.TypeCode=6
                    UNION ALL SELECT quest.GoalContentId FROM guild_distribution_notifications notice
                        JOIN guild_work_awards award ON award.Id=notice.ObjectId JOIN guild_quests quest ON quest.Id=award.QuestId
                        WHERE notice.EnvelopeId=$envelope AND notice.TypeCode=5
                    UNION ALL SELECT receipt.ContentId FROM guild_distribution_notifications notice
                        JOIN guild_result_receipts receipt ON receipt.QuestId=notice.ObjectId WHERE notice.EnvelopeId=$envelope AND notice.TypeCode=7))
                """);

            Add(erased, "$envelope", Id(envelopeId));

            return Equals(1L, await erased.ExecuteScalarAsync(ct).ConfigureAwait(false))
                ? error.Code : Result<string?>.Success(null);
        }

        if (error.Code == ErrorCodes.Guild.Forbidden && envelope is not null)
        {
            var source = await signets.ValidateCurrentGrantAsync(envelope.QuestId, ct).ConfigureAwait(false);

            if (source.IsFailure && source.Error.Code is ErrorCodes.Guild.AuthorityRevoked or ErrorCodes.Guild.AuthorityExpired or ErrorCodes.Guild.OwnerUnavailable)
                return source.Error.Code;
        }

        return Result<string?>.Success(null);
    }

    private async Task<Result> ValidateCircleInputWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildLocalInferenceExecution execution,
        IReadOnlyList<GuildCircleInputBatch>? batches, CancellationToken ct)
    {
        var driver = execution.Driver;

        GuildPortalStore portal = new(this, authority, policy, time);

        var readerCaller = await portal.DriverCallerWithinAsync(connection, transaction, driver, ct).ConfigureAwait(false);

        if (readerCaller.IsFailure) return readerCaller.Error;

        GuildCallerContext caller = readerCaller.Value;

        // Check accepted source references on every later round, including a resumed planner unit.
        // No previously rendered source gains a lifetime exemption by entering a checkpoint.
        string after = "";

        while (true)
        {
            List<(Guid Inbox, Guid Envelope)> accepted = [];

            await using (var query = Command(connection, transaction, """
                SELECT CASE WHEN typeof(p.InboxId)='text' AND length(CAST(p.InboxId AS BLOB))=36 THEN p.InboxId END,
                    CASE WHEN typeof(p.EnvelopeId)='text' AND length(CAST(p.EnvelopeId AS BLOB))=36 THEN p.EnvelopeId END
                FROM guild_portal_inputs p
                WHERE p.RecipientQuestId=$quest AND p.InboxId>$after ORDER BY p.InboxId LIMIT 100
                """))
            {
                Add(query, "$quest", Id(driver.Execution.QuestId));
                Add(query, "$after", after);

                await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1) || !Guid.TryParseExact(reader.GetString(0), "D", out Guid inbox)
                        || !Guid.TryParseExact(reader.GetString(1), "D", out Guid envelope)) return Unavailable;

                    accepted.Add((inbox, envelope));
                }
            }

            foreach (var item in accepted)
            {
                var envelope = await GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, item.Envelope, ct).ConfigureAwait(false);

                if (envelope.IsFailure) return envelope.Error;

                var current = await portal.ReadInputPromptWithinAsync(connection, transaction, signets, caller, envelope.Value, ct).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;
            }

            if (accepted.Count < 100) break;

            after = Id(accepted[^1].Inbox);
        }

        if (batches is null) return Result.Success();

        foreach (var batch in batches)
        {
            if (batch is null || !_circleInputs.TryGetValue(batch, out var owner) || !ReferenceEquals(owner, execution)) return InvalidReceipt;

            foreach (var item in batch.Items)
            {
                var state = await ReadCircleInputStateWithinAsync(connection, transaction, driver, item, ct).ConfigureAwait(false);

                if (state.IsFailure) return state.Error;

                var envelope = await GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, item.EnvelopeId, ct).ConfigureAwait(false);

                if (envelope.IsFailure) return envelope.Error;

                var current = await portal.ReadInputPromptWithinAsync(connection, transaction, signets, caller, envelope.Value, ct).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(current.Value))) != item.PromptDigest) return Conflict;
            }
        }

        return Result.Success();
    }

    private static async Task<Result<int>> ReadCircleInputStateWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestDriver driver, GuildCircleInputItem item, CancellationToken ct)
    {
        await using var query = Command(connection, transaction, """
            SELECT i.DeliveryStateCode FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId
            WHERE i.Id=$inbox AND i.EnvelopeId=$envelope AND i.RecipientQuestId=$quest AND i.RecipientKey=$recipient
                AND (i.DeliveryStateCode=1 AND i.Revision=$revision
                    AND (i.LeaseOwner IS NULL OR i.LeaseOwner=$holder AND i.LeaseRevision=$lease
                        AND i.OperationId=(SELECT OperationId FROM guild_quests WHERE Id=$quest))
                    OR i.DeliveryStateCode=2 AND EXISTS(SELECT 1 FROM guild_portal_inputs p
                        JOIN guild_portal_receipts r ON r.InboxId=p.InboxId
                        WHERE p.InboxId=i.Id AND p.RecipientQuestId=i.RecipientQuestId AND p.EnvelopeId=i.EnvelopeId
                            AND r.Digest=e.IntentDigest AND r.EffectKindCode=CASE WHEN e.KindCode=2 THEN 1 ELSE 0 END))
            """);

        Add(query, "$inbox", Id(item.InboxId));
        Add(query, "$envelope", Id(item.EnvelopeId));
        Add(query, "$quest", Id(driver.Execution.QuestId));
        Add(query, "$recipient", "quest:" + Id(driver.Execution.QuestId));
        Add(query, "$revision", item.Revision);
        Add(query, "$holder", driver.LeaseOwner);
        Add(query, "$lease", driver.OperationRevision);

        return await query.ExecuteScalarAsync(ct).ConfigureAwait(false) is long state && state is 1 or 2
            ? (int)state : InvalidReceipt;
    }

    private async Task<Result> CommitCircleInputWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildLocalInferenceExecution execution,
        IReadOnlyList<GuildCircleInputBatch>? batches, CancellationToken ct)
    {
        if (batches is null) return Result.Success();

        HashSet<Guid> accepted = [];

        foreach (var batch in batches)
        foreach (var item in batch.Items)
        {
            if (!accepted.Add(item.InboxId)) continue;

            var state = await ReadCircleInputStateWithinAsync(connection, transaction, execution.Driver, item, ct).ConfigureAwait(false);

            if (state.IsFailure) return state.Error;

            if (state.Value == 2) continue;

            await using (var predecessor = Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM guild_inbox i JOIN guild_inbox earlier
                    ON earlier.RecipientKey=i.RecipientKey AND earlier.StreamId=i.StreamId
                    WHERE i.Id=$inbox AND earlier.DeliveryStateCode=1 AND earlier.SenderSequence<i.SenderSequence)
                """))
            {
                Add(predecessor, "$inbox", Id(item.InboxId));

                if (Equals(1L, await predecessor.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Conflict;
            }

            var envelope = await GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, item.EnvelopeId, ct).ConfigureAwait(false);

            if (envelope.IsFailure) return envelope.Error;

            Guid receipt = Guid.NewGuid();
            int kind = envelope.Value.Kind == GuildEnvelopeKind.Result ? 1 : 0;

            if (kind == 1)
            {
                GuildPortalStore portal = new(this, authority, policy, time);

                var caller = await portal.DriverCallerWithinAsync(connection, transaction, execution.Driver, ct).ConfigureAwait(false);

                if (caller.IsFailure) return caller.Error;

                var recipient = await ReadWithinAsync(connection, transaction, execution.Driver.Execution.QuestId, ct).ConfigureAwait(false);

                if (recipient.IsFailure) return recipient.Error;

                await using var digest = Command(connection, transaction,
                    "SELECT Digest FROM guild_result_receipts WHERE QuestId=$quest AND ResultId=$result");

                Add(digest, "$quest", Id(envelope.Value.QuestId));
                Add(digest, "$result", Id(envelope.Value.Producer.ReceiptId));

                if (await digest.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] bytes || bytes.Length != 32) return Unavailable;

                GuildResultApplicationRequest application = new(new(receipt, Convert.ToHexString(bytes)),
                    recipient.Value.Quest.Id, envelope.Value.QuestId, envelope.Value.Producer.ReceiptId!.Value,
                    Convert.ToHexString(bytes), recipient.Value.Quest.Revision);

                var applied = await ApplyResultWithinAsync(connection, transaction, signets, caller.Value, application, ct).ConfigureAwait(false);

                if (applied.IsFailure) return applied.Error;
            }

            var committed = await GuildPortalStore.CommitInboxWithinAsync(connection, transaction, item.InboxId, item.EnvelopeId,
                item.Revision, receipt, kind, execution.Driver.Execution.QuestId, time.GetUtcNow(), ct).ConfigureAwait(false);

            if (committed.IsFailure) return committed.Error;
        }

        return Result.Success();
    }
}
