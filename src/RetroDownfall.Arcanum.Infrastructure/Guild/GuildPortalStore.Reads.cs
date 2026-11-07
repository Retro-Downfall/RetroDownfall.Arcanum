using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildPortalReplayCursor(long Watermark, string Cursor);

internal sealed record GuildPortalReplayPage(GuildEventPage Page, string ResumeCursor, bool CaughtUp);

internal sealed partial class GuildPortalStore
{
    private const int PortalPageBytes = 1_048_576;

    private sealed record PortalReadBinding(string CallerKey, Guid Epoch, string VisibilityKey);

    private sealed record PortalCursor(long After, long Snapshot);

    private sealed record PortalEvent(long Sequence, GuildEventKind Kind, Guid ObjectId, long Revision, Guid QuestId,
        Guid? EnvelopeId, GuildPortalEventSource Source);

    private sealed record PortalReadScope(PortalReadBinding Binding, GuildQuestStored? OwnQuest);

    private sealed record PortalCursorToken(Guid Generation, Guid Epoch, Guid Root, Guid CursorId);

    internal Task<Result<GuildEventPage>> ReadEventsAsync(GuildCallerContext caller, GuildEventQuery query, CancellationToken ct)
    {
        if (query is null || query.RootQuestId == Guid.Empty || query.SubtreeQuestId == Guid.Empty || query.CohortId == Guid.Empty
            || query.PageSize is < 1 or > 100) return Task.FromResult(Result<GuildEventPage>.Failure(Invalid));

        return authority.InQuestTransactionAsync<GuildEventPage>(query.RootQuestId, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            string scopeKey = "events:" + (Id(query.SubtreeQuestId) ?? "-") + ":" + (Id(query.CohortId) ?? "-");

            Result<PortalCursor> cursor = await ReadCursorWithinAsync(connection, transaction, caller, binding.Value,
                query.RootQuestId, scopeKey, query.Cursor, token).ConfigureAwait(false);

            if (cursor.IsFailure) return cursor.Error;

            if (query.CohortId is not null) return Unavailable;

            Result<PortalReadScope> scope = await RequireReadScopeWithinAsync(connection, transaction, signets,
                caller, query.RootQuestId, binding.Value, query.Cursor is not null, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            if (query.SubtreeQuestId is { } subtree)
            {
                Result<GuildQuestStored> selected = await GuildQuestStore.ReadWithinAsync(connection, transaction, subtree, token).ConfigureAwait(false);

                if (selected.IsFailure || selected.Value.Quest.RootQuestId != query.RootQuestId) return Forbidden;

                Result<bool> selectedVisible = await EventVisibleWithinAsync(connection, transaction, signets, caller,
                    scope.Value, query.RootQuestId, subtree, null, null, token).ConfigureAwait(false);

                if (selectedVisible.IsFailure) return selectedVisible.Error;

                if (!selectedVisible.Value) return Forbidden;
            }

            Result<ImmutableArray<PortalEvent>> scanned = await ScanEventsWithinAsync(connection, transaction, query.RootQuestId,
                cursor.Value.After, cursor.Value.Snapshot, query.PageSize, token).ConfigureAwait(false);

            if (scanned.IsFailure) return scanned.Error;

            ImmutableArray<GuildChangeEventDto>.Builder items = ImmutableArray.CreateBuilder<GuildChangeEventDto>();

            long after = cursor.Value.After;

            foreach (PortalEvent row in scanned.Value)
            {
                Result<bool> visible = await EventVisibleWithinAsync(connection, transaction, signets, caller, scope.Value,
                    query.RootQuestId, row.QuestId, row.EnvelopeId, query.SubtreeQuestId, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (visible.Value) items.Add(new(row.Sequence, row.Kind, row.ObjectId, row.Revision, [], row.QuestId));

                after = row.Sequence;
            }

            string? next = null;

            if (after < cursor.Value.Snapshot)
            {
                next = await WriteCursorWithinAsync(connection, transaction, binding.Value, query.RootQuestId, scopeKey,
                    after, cursor.Value.Snapshot, token).ConfigureAwait(false);
            }

            GuildEventPage page = new(items.ToImmutable(), next, cursor.Value.Snapshot);

            return JsonSerializer.SerializeToUtf8Bytes(page, GuildJsonContext.Default.GuildEventPage).Length <= PortalPageBytes ? page : TooLarge;
        }, ct);
    }

    internal Task<Result<GuildPortalReplayCursor>> CaptureReplayCursorAsync(GuildCallerContext caller, Guid root, CancellationToken ct)
    {
        if (root == Guid.Empty) return Task.FromResult(Result<GuildPortalReplayCursor>.Failure(Invalid));

        return authority.InQuestTransactionAsync<GuildPortalReplayCursor>(root, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            Result<PortalReadScope> scope = await RequireReadScopeWithinAsync(connection, transaction, signets,
                caller, root, binding.Value, false, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, token).ConfigureAwait(false);

            if (head.IsFailure) return head.Error;

            string cursor = await WriteCursorWithinAsync(connection, transaction, binding.Value, root, "replay",
                head.Value.Last, head.Value.Last, token).ConfigureAwait(false);

            return new GuildPortalReplayCursor(head.Value.Last, cursor);
        }, ct);
    }

    internal Task<Result<GuildPortalReplayPage>> ReadReplayAsync(GuildCallerContext caller, Guid root, string cursor, CancellationToken ct)
    {
        if (root == Guid.Empty || string.IsNullOrEmpty(cursor)) return Task.FromResult(Result<GuildPortalReplayPage>.Failure(CursorInvalid));

        return authority.InQuestTransactionAsync<GuildPortalReplayPage>(root, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            Result<PortalCursor> saved = await ReadCursorWithinAsync(connection, transaction, caller, binding.Value,
                root, "replay", cursor, token).ConfigureAwait(false);

            if (saved.IsFailure) return saved.Error;

            Result<PortalReadScope> scope = await RequireReadScopeWithinAsync(connection, transaction, signets,
                caller, root, binding.Value, true, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, token).ConfigureAwait(false);

            if (head.IsFailure) return head.Error;

            Result<ImmutableArray<PortalEvent>> scanned = await ScanEventsWithinAsync(connection, transaction, root,
                saved.Value.After, head.Value.Last, 100, token).ConfigureAwait(false);

            if (scanned.IsFailure) return scanned.Error;

            ImmutableArray<GuildChangeEventDto>.Builder items = ImmutableArray.CreateBuilder<GuildChangeEventDto>();

            long after = saved.Value.After;

            foreach (PortalEvent row in scanned.Value)
            {
                Result<bool> visible = await EventVisibleWithinAsync(connection, transaction, signets, caller, scope.Value,
                    root, row.QuestId, row.EnvelopeId, null, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (visible.Value) items.Add(new(row.Sequence, row.Kind, row.ObjectId, row.Revision, [], row.QuestId));

                after = row.Sequence;
            }

            bool caughtUp = after == head.Value.Last;

            // A new durable token is issued even when no new event exists. An empty response is an
            // explicit current watermark, and never a request to spin on the same continuation.
            string resume = await WriteCursorWithinAsync(connection, transaction, binding.Value, root, "replay",
                after, head.Value.Last, token).ConfigureAwait(false);

            GuildEventPage page = new(items.ToImmutable(), caughtUp ? null : resume, head.Value.Last);

            return JsonSerializer.SerializeToUtf8Bytes(page, GuildJsonContext.Default.GuildEventPage).Length <= PortalPageBytes
                ? new GuildPortalReplayPage(page, resume, caughtUp) : TooLarge;
        }, ct);
    }

    internal Task<Result<GuildCircleInboxPage>> ReadOperatorInboxAsync(GuildCallerContext caller, GuildCircleInboxQuery query, CancellationToken ct) =>
        ReadInboxCoreAsync(caller, query, true, ct);

    internal Task<Result<GuildCircleInboxPage>> ReadInboxAsync(GuildCallerContext caller, GuildCircleInboxQuery query, CancellationToken ct) =>
        ReadInboxCoreAsync(caller, query, false, ct);

    private async Task<Result<GuildCircleInboxPage>> ReadInboxCoreAsync(GuildCallerContext caller, GuildCircleInboxQuery query, bool operatorInbox, CancellationToken ct)
    {
        if (caller is null || caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Run)
            || operatorInbox && caller.Kind != GuildCallerKind.Operator) return Forbidden;

        if (query is null || query.RecipientQuestId == Guid.Empty || query.PageSize is < 1 or > 100) return Invalid;

        Guid root;

        if (query.Cursor is not null)
        {
            if (!TryParseCursor(query.Cursor, out PortalCursorToken parsed)) return CursorInvalid;

            root = parsed.Root;
        }
        else
        {
            Result<Guid> located = await authority.InQuestTransactionAsync<Guid>(caller.Execution?.RootQuestId ?? query.RecipientQuestId,
                async (connection, transaction, _, token) =>
                {
                    await using SqliteCommand command = Command(connection, transaction,
                        "SELECT CASE WHEN length(CAST(RootQuestId AS BLOB))=36 THEN RootQuestId ELSE NULL END FROM guild_quests WHERE Id=$id");

                    Add(command, "$id", Id(query.RecipientQuestId));

                    return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string value && Guid.TryParseExact(value, "D", out Guid found)
                        ? found : Forbidden;
                }, ct).ConfigureAwait(false);

            if (located.IsFailure) return located.Error;

            root = located.Value;
        }

        return await authority.InQuestTransactionAsync<GuildCircleInboxPage>(root, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            string scopeKey = (operatorInbox ? "operator-inbox:" : "inbox:") + Id(query.RecipientQuestId);

            Result<PortalCursor> cursor = await ReadCursorWithinAsync(connection, transaction, caller, binding.Value,
                root, scopeKey, query.Cursor, token).ConfigureAwait(false);

            if (cursor.IsFailure) return cursor.Error;

            Result<PortalReadScope> scope = await RequireReadScopeWithinAsync(connection, transaction, signets,
                caller, root, binding.Value, query.Cursor is not null, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            Result<GuildQuestStored> recipient = await GuildQuestStore.ReadWithinAsync(connection, transaction, query.RecipientQuestId, token).ConfigureAwait(false);

            if (recipient.IsFailure || recipient.Value.Quest.RootQuestId != root) return query.Cursor is null ? Forbidden : CursorGone;

            if (caller.Kind == GuildCallerKind.Run && caller.Execution!.QuestId != query.RecipientQuestId) return Forbidden;

            Result recipientVisible = await quests.RequireVisibleAsync(connection, transaction, caller, recipient.Value, token).ConfigureAwait(false);

            if (recipientVisible.IsFailure) return recipientVisible.Error;

            Result<ImmutableArray<PortalEvent>> scanned = await ScanEventsWithinAsync(connection, transaction, root,
                cursor.Value.After, cursor.Value.Snapshot, query.PageSize, token).ConfigureAwait(false);

            if (scanned.IsFailure) return scanned.Error;

            ImmutableArray<GuildCircleInboxItem>.Builder items = ImmutableArray.CreateBuilder<GuildCircleInboxItem>();

            long after = cursor.Value.After;

            int bytes = 512;

            foreach (PortalEvent row in scanned.Value)
            {
                if (row.Kind == GuildEventKind.MessageCommitted && row.Source == GuildPortalEventSource.Envelope && row.Revision == 1
                    && row.EnvelopeId is { } envelopeId && row.ObjectId == envelopeId)
                {
                    Result<GuildCircleInboxItem?> item = await ReadInboxItemWithinAsync(connection, transaction, signets,
                        caller, query.RecipientQuestId, envelopeId, operatorInbox, token).ConfigureAwait(false);

                    if (item.IsFailure) return item.Error;

                    if (item.Value is { } value)
                    {
                        int encoded = JsonSerializer.SerializeToUtf8Bytes(value, GuildJsonContext.Default.GuildCircleInboxItem).Length + 1;

                        if (bytes + encoded > PortalPageBytes)
                        {
                            if (items.Count == 0) return TooLarge;

                            break;
                        }

                        items.Add(value);

                        bytes += encoded;
                    }
                }

                after = row.Sequence;
            }

            string? next = null;

            if (after < cursor.Value.Snapshot)
                next = await WriteCursorWithinAsync(connection, transaction, binding.Value, root, scopeKey, after, cursor.Value.Snapshot, token).ConfigureAwait(false);

            GuildCircleInboxPage page = new(items.ToImmutable(), next, cursor.Value.Snapshot);

            return JsonSerializer.SerializeToUtf8Bytes(page, GuildJsonContext.Default.GuildCircleInboxPage).Length <= PortalPageBytes ? page : TooLarge;
        }, ct).ConfigureAwait(false);
    }

    private async Task<Result<GuildCircleInboxItem?>> ReadInboxItemWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, Guid recipient, Guid envelopeId, bool operatorInbox, CancellationToken ct)
    {
        Guid inboxId;

        long revision;

        GuildDeliveryState state;

        string? reason;

        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT CASE WHEN length(CAST(Id AS BLOB))=36 THEN Id ELSE NULL END,Revision,DeliveryStateCode,
                CASE WHEN length(CAST(ReasonCode AS BLOB))<=128 THEN ReasonCode ELSE NULL END
            FROM guild_inbox WHERE EnvelopeId=$envelope AND RecipientKey=$key
                AND (($operator=0 AND RecipientQuestId=$recipient) OR ($operator=1 AND RecipientQuestId IS NULL
                    AND EXISTS(SELECT 1 FROM guild_envelopes e WHERE e.Id=guild_inbox.EnvelopeId AND e.QuestId=$recipient)))
            """))
        {
            Add(command, "$envelope", Id(envelopeId));

            Add(command, "$recipient", Id(recipient));

            Add(command, "$key", operatorInbox ? "operator:" + Id(caller.PrincipalId) : "quest:" + Id(recipient));

            Add(command, "$operator", operatorInbox ? 1 : 0);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildCircleInboxItem?>.Success(null);

            if (reader.IsDBNull(0) || !Guid.TryParseExact(reader.GetString(0), "D", out inboxId)) return Unavailable;

            revision = reader.GetInt64(1);

            state = (GuildDeliveryState)reader.GetInt32(2);

            if (revision < 1 || state is not (GuildDeliveryState.Pending or GuildDeliveryState.Applied or GuildDeliveryState.Withheld)) return Unavailable;

            reason = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        GuildEnvelope? envelope = null;

        if (state != GuildDeliveryState.Withheld)
        {
            Result<GuildEnvelope> found = await ReadEnvelopeWithinAsync(connection, transaction, envelopeId, ct).ConfigureAwait(false);

            if (found.IsFailure) return found.Error;

            Result<byte[]> disclosed = await ReadBodyWithinAsync(connection, transaction, signets, caller, found.Value, ct).ConfigureAwait(false);

            if (disclosed.IsSuccess) envelope = found.Value;
            else if (disclosed.Error.Code is ErrorCodes.Guild.Forbidden or ErrorCodes.Guild.ContentErased or ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.NotFound)
                reason ??= disclosed.Error.Code;
            else return disclosed.Error;
        }

        return new GuildCircleInboxItem(inboxId, envelope, revision, state, reason, envelopeId);
    }

    private async Task<Result<PortalReadBinding>> ReadBindingWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, CancellationToken ct)
    {
        if (caller is null || caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Run)) return Forbidden;

        if (policy is not IGuildPortalDisclosurePolicy disclosure || string.IsNullOrWhiteSpace(disclosure.VisibilityEpoch)
            || Encoding.UTF8.GetByteCount(disclosure.VisibilityEpoch) > 65_536) return Unavailable;

        await using SqliteCommand command = Command(connection, transaction,
            "SELECT CASE WHEN typeof(Epoch)='text' AND length(CAST(Epoch AS BLOB))=36 THEN Epoch ELSE NULL END FROM guild_signet_authority_state WHERE StateKey=1");

        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string value
            || !Guid.TryParseExact(value, "D", out Guid epoch) || epoch == Guid.Empty || Id(epoch) != value) return Unavailable;

        GuildExecutionHandle? run = caller.Execution;

        string identity = ((int)caller.Kind).ToString(CultureInfo.InvariantCulture) + ":" + Id(caller.PrincipalId)
            + (run is null ? "" : ":" + Id(run.SignetId) + ":" + Id(run.AgentId) + ":" + Id(run.RunId) + ":" + Id(run.QuestId)
                + ":" + run.CredentialGeneration.ToString(CultureInfo.InvariantCulture) + ":" + run.RevocationGeneration.ToString(CultureInfo.InvariantCulture));

        return new PortalReadBinding(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))), epoch,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(disclosure.VisibilityEpoch))));
    }

    private async Task<Result<PortalReadScope>> RequireReadScopeWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, Guid root, PortalReadBinding binding, bool continuing, CancellationToken ct)
    {
        if (caller.InstallationGeneration != policy.InstallationGeneration) return continuing ? CursorGone : Forbidden;

        if (caller.Kind == GuildCallerKind.Run && caller.Execution?.RootQuestId != root) return Forbidden;

        Result<GuildQuestStored> found = await GuildQuestStore.ReadWithinAsync(connection, transaction, root, ct).ConfigureAwait(false);

        if (found.IsFailure) return continuing && found.Error.Code is ErrorCodes.Guild.NotFound or ErrorCodes.Guild.OwnerUnavailable ? CursorGone : found.Error;

        if (found.Value.Quest.RootQuestId != root || found.Value.Quest.ParentQuestId is not null) return Forbidden;

        Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, found.Value, ct).ConfigureAwait(false);

        if (visible.IsFailure) return continuing && visible.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? CursorGone : visible.Error;

        GuildQuestStored? own = null;

        if (caller.Kind == GuildCallerKind.Run)
        {
            if (caller.Execution is not { } execution || execution.RootQuestId != root) return Forbidden;

            Result<GuildGrant> live = await signets.VerifyAsync(execution, ct).ConfigureAwait(false);

            if (live.IsFailure) return live.Error;

            Result<GuildQuestStored> actor = await GuildQuestStore.ReadWithinAsync(connection, transaction, execution.QuestId, ct).ConfigureAwait(false);

            if (actor.IsFailure) return actor.Error;

            if (actor.Value.Quest.SignetId != execution.SignetId || actor.Value.Quest.AgentId != execution.AgentId
                || actor.Value.Quest.RunId != execution.RunId || actor.Value.Quest.RootQuestId != root) return Forbidden;

            Result actorVisible = await quests.RequireVisibleAsync(connection, transaction, caller, actor.Value, ct).ConfigureAwait(false);

            if (actorVisible.IsFailure) return actorVisible.Error;

            own = actor.Value;
        }

        return new PortalReadScope(binding, own);
    }

    private async Task<Result<bool>> EventVisibleWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, PortalReadScope scope, Guid root, Guid questId,
        Guid? envelopeId, Guid? subtree, CancellationToken ct)
    {
        if (scope.OwnQuest is { } own && questId != own.Quest.Id && questId != own.Quest.ParentQuestId)
        {
            await using SqliteCommand relation = Command(connection, transaction,
                "SELECT 1 FROM guild_quests WHERE Id=$quest AND ParentQuestId=$own AND RootQuestId=$root");

            Add(relation, "$quest", Id(questId));

            Add(relation, "$own", Id(own.Quest.Id));

            Add(relation, "$root", Id(root));

            if (await relation.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return false;
        }

        if (subtree is { } filter)
        {
            await using SqliteCommand ancestry = Command(connection, transaction, """
                WITH RECURSIVE ancestors(Id,ParentQuestId) AS (
                    SELECT Id,ParentQuestId FROM guild_quests WHERE Id=$quest AND RootQuestId=$root
                    UNION
                    SELECT parent.Id,parent.ParentQuestId FROM guild_quests parent JOIN ancestors child ON parent.Id=child.ParentQuestId
                    WHERE parent.RootQuestId=$root AND child.Id<>$subtree)
                SELECT 1 FROM ancestors WHERE Id=$subtree LIMIT 1
                """);

            Add(ancestry, "$quest", Id(questId));

            Add(ancestry, "$root", Id(root));

            Add(ancestry, "$subtree", Id(filter));

            if (await ancestry.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return false;
        }

        Result<GuildQuestStored> found = await GuildQuestStore.ReadWithinAsync(connection, transaction, questId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error.Code is ErrorCodes.Guild.NotFound or ErrorCodes.Guild.OwnerUnavailable ? false : found.Error;

        if (found.Value.Quest.RootQuestId != root) return Unavailable;

        Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, found.Value, ct).ConfigureAwait(false);

        if (visible.IsFailure)
            return visible.Error.Code is ErrorCodes.Guild.Forbidden or ErrorCodes.Guild.OwnerUnavailable ? false : visible.Error;

        if (envelopeId is { } envelope)
        {
            Result<GuildEnvelope> stored = await ReadEnvelopeWithinAsync(connection, transaction, envelope, ct).ConfigureAwait(false);

            if (stored.IsFailure) return stored.Error;

            if (stored.Value.RootQuestId != root) return Unavailable;

            // A related source Quest does not make every message from that source visible. The
            // immutable addressed audience is checked separately, including parent-to-sibling edges.
            Result audience = await RequireReaderWithinAsync(connection, transaction, signets, caller, stored.Value, ct).ConfigureAwait(false);

            if (audience.IsFailure)
                return audience.Error.Code is ErrorCodes.Guild.Forbidden or ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.NotFound
                    ? false : audience.Error;
        }

        return true;
    }

    private static async Task<Result<(long Last, long Retained)>> ReadEventHeadWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid root, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT LastSequence,RetainedAfterSequence FROM guild_event_streams WHERE RootQuestId=$root");

        Add(command, "$root", Id(root));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return (0L, 0L);

        long last = reader.GetInt64(0);

        long retained = reader.GetInt64(1);

        return last >= 0 && retained >= 0 && retained <= last ? (last, retained) : Unavailable;
    }

    private async Task<Result<PortalCursor>> ReadCursorWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, PortalReadBinding binding, Guid root, string scopeKey, string? cursor, CancellationToken ct)
    {
        Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, ct).ConfigureAwait(false);

        if (head.IsFailure) return head.Error;

        if (cursor is null) return new PortalCursor(head.Value.Retained, head.Value.Last);

        if (!TryParseCursor(cursor, out PortalCursorToken parsed)) return CursorInvalid;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT CASE WHEN length(CAST(InstallationGeneration AS BLOB))=36 AND length(CAST(CallerKey AS BLOB))<=256
                AND length(CAST(RootQuestId AS BLOB))=36 AND length(CAST(ScopeKey AS BLOB))<=512
                AND length(CAST(AuthorityEpoch AS BLOB))=36 AND length(CAST(VisibilityKey AS BLOB))<=256 THEN 1 ELSE 0 END,
                InstallationGeneration,CallerKey,RootQuestId,ScopeKey,AfterSequence,SnapshotSequence,AuthorityEpoch,VisibilityKey
            FROM guild_portal_cursors WHERE Id=$id
            """);

        Add(command, "$id", Id(parsed.CursorId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return parsed.Generation != policy.InstallationGeneration || parsed.Epoch != binding.Epoch ? CursorGone : CursorInvalid;

        if (reader.GetInt64(0) != 1) return Unavailable;

        if (reader.GetString(1) != Id(parsed.Generation) || reader.GetString(3) != Id(parsed.Root) || reader.GetString(7) != Id(parsed.Epoch)
            || parsed.Root != root || reader.GetString(2) != binding.CallerKey || reader.GetString(4) != scopeKey) return CursorInvalid;

        if (parsed.Generation != policy.InstallationGeneration || caller.InstallationGeneration != policy.InstallationGeneration
            || parsed.Epoch != binding.Epoch || reader.GetString(8) != binding.VisibilityKey) return CursorGone;

        long after = reader.GetInt64(5);

        long snapshot = reader.GetInt64(6);

        if (after < head.Value.Retained || after < 0 || after > snapshot || snapshot > head.Value.Last) return CursorGone;

        return new PortalCursor(after, snapshot);
    }

    private async Task<string> WriteCursorWithinAsync(SqliteConnection connection, SqliteTransaction transaction, PortalReadBinding binding,
        Guid root, string scopeKey, long after, long snapshot, CancellationToken ct)
    {
        Guid id = Guid.NewGuid();

        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_portal_cursors(Id,InstallationGeneration,CallerKey,RootQuestId,ScopeKey,AfterSequence,SnapshotSequence,CreatedAtUtc,AuthorityEpoch,VisibilityKey)
            VALUES($id,$generation,$caller,$root,$scope,$after,$snapshot,$now,$epoch,$visibility)
            """);

        Add(command, "$id", Id(id));

        Add(command, "$generation", Id(policy.InstallationGeneration));

        Add(command, "$caller", binding.CallerKey);

        Add(command, "$root", Id(root));

        Add(command, "$scope", scopeKey);

        Add(command, "$after", after);

        Add(command, "$snapshot", snapshot);

        Add(command, "$now", UtcInstantText.Format(time.GetUtcNow()));

        Add(command, "$epoch", Id(binding.Epoch));

        Add(command, "$visibility", binding.VisibilityKey);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return policy.InstallationGeneration.ToString("N") + "." + binding.Epoch.ToString("N") + "." + root.ToString("N") + "." + id.ToString("N");
    }

    private static bool TryParseCursor(string cursor, out PortalCursorToken parsed)
    {
        parsed = null!;

        if (cursor.Length != 131) return false;

        string[] parts = cursor.Split('.');

        if (parts.Length != 4) return false;

        Guid[] values = new Guid[4];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!Guid.TryParseExact(parts[i], "N", out values[i]) || values[i] == Guid.Empty
                || parts[i] != values[i].ToString("N")) return false;
        }

        parsed = new(values[0], values[1], values[2], values[3]);

        return true;
    }

    private static async Task<Result<ImmutableArray<PortalEvent>>> ScanEventsWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid root, long after, long snapshot, int pageSize, CancellationToken ct)
    {
        Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, ct).ConfigureAwait(false);

        if (head.IsFailure) return head.Error;

        if (after < head.Value.Retained || after < 0 || after > snapshot || snapshot > head.Value.Last) return CursorGone;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT EventSequence,EventKindCode,Revision,
                CASE WHEN length(CAST(ObjectId AS BLOB))=36 THEN ObjectId ELSE NULL END,
                CASE WHEN length(CAST(QuestId AS BLOB))=36 THEN QuestId ELSE NULL END,
                CASE WHEN length(CAST(EnvelopeId AS BLOB))=36 THEN EnvelopeId ELSE NULL END,SourceKindCode
            FROM guild_events WHERE RootQuestId=$root AND EventSequence>$after AND EventSequence<=$snapshot
            ORDER BY EventSequence LIMIT $take
            """);

        Add(command, "$root", Id(root));

        Add(command, "$after", after);

        Add(command, "$snapshot", snapshot);

        Add(command, "$take", pageSize);

        ImmutableArray<PortalEvent>.Builder rows = ImmutableArray.CreateBuilder<PortalEvent>();

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        long scanned = after;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long sequence = reader.GetInt64(0);

            if (scanned == long.MaxValue || sequence != scanned + 1) return CursorGone;

            GuildEventKind kind = (GuildEventKind)reader.GetInt32(1);

            long revision = reader.GetInt64(2);

            GuildPortalEventSource source = (GuildPortalEventSource)reader.GetInt32(6);

            if (!Enum.IsDefined(kind) || !Enum.IsDefined(source) || revision < 1 || reader.IsDBNull(3) || reader.IsDBNull(4)
                || !Guid.TryParseExact(reader.GetString(3), "D", out Guid objectId)
                || !Guid.TryParseExact(reader.GetString(4), "D", out Guid questId)) return Unavailable;

            Guid? envelopeId = reader.IsDBNull(5) ? null : Guid.TryParseExact(reader.GetString(5), "D", out Guid envelope) ? envelope : null;

            rows.Add(new(sequence, kind, objectId, revision, questId, envelopeId, source));

            scanned = sequence;
        }

        if (rows.Count < pageSize && scanned < snapshot) return CursorGone;

        return rows.ToImmutable();
    }

    private static Error CursorInvalid => new(ErrorCodes.Guild.CursorInvalid, "The Portal cursor does not match this caller and scope.");

    private static Error CursorGone => new(ErrorCodes.Guild.CursorGone, "The Portal history or authority changed; capture a new snapshot.");
}
